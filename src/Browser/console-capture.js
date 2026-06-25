/*
 * KY.AI browser-console capture snippet.
 *
 * ky-ai-ng injects this (a reversible <script> tag in the app's index.html, driven by ky-ai-browser)
 * so capturing needs zero edits to the app's own code. It patches console.* + uncaught errors +
 * unhandled rejections and POSTs them, batched, to ky-ai-browser's loopback ingest. Because the page
 * is on the dev server's origin and ky-ai-browser is on another loopback port, the POST is
 * cross-origin — kept a CORS-simple request (text/plain). ky-ai-browser enriches + buffers them; the
 * agent reads them over MCP via console_tail.
 *
 * __KYAI_TOKEN__ (a misroute tag, NOT a secret) and __KYAI_INGEST__ (ky-ai-browser's absolute ingest
 * URL) are replaced at serve time. The whole thing is best-effort: capture must never throw into, or
 * feed back from, the app.
 */
(function () {
  "use strict";
  if (window.__kyaiConsoleInstalled) return;
  window.__kyaiConsoleInstalled = true;

  var TOKEN = "__KYAI_TOKEN__";
  var INGEST = "__KYAI_INGEST__";   // absolute URL of ky-ai-browser's ingest (cross-origin; templated in)
  var FLUSH_MS = 250;
  var MAX_QUEUE = 1000;             // client-side cap; overflow is counted and reported to the server
  var MAX_BATCH_BYTES = 56 * 1024;  // stay under the ~64KB sendBeacon / fetch(keepalive) cap
  var MAX_ARGS = 50;
  var MAX_ARG_LEN = 8192;

  // Per-page-load id so the agent can segment by reload / HMR boundary. Date.now is fine here
  // (browser side; the resume-determinism constraint is a server-side concern).
  var pageLoadId = Math.random().toString(36).slice(2) + "-" + Date.now().toString(36);
  var dropped = 0;

  // Capture UNPATCHED console refs BEFORE patching, so the snippet's own diagnostics (and any
  // console output triggered while sending) can never feed back into the capture queue.
  var nativeConsole = {
    log: console.log.bind(console),
    info: console.info.bind(console),
    warn: console.warn.bind(console),
    error: console.error.bind(console),
    debug: (console.debug || console.log).bind(console)
  };

  var queue = [];
  var sending = false;
  var timer = null;

  function nowIso() { try { return new Date().toISOString(); } catch (e) { return ""; } }

  function serializeArg(a) {
    try {
      if (a === null) return "null";
      var t = typeof a;
      if (t === "undefined") return "undefined";
      if (t === "string") return a;
      if (t === "number" || t === "boolean") return String(a);
      if (t === "bigint") return a.toString() + "n";
      if (t === "symbol") return a.toString();
      if (t === "function") return "[Function: " + (a.name || "anonymous") + "]";
      if (a instanceof Error) return (a.name || "Error") + ": " + (a.message || "") + (a.stack ? "\n" + a.stack : "");
      if (typeof Node !== "undefined" && a instanceof Node) {
        return "[" + (a.nodeName || "Node") + (a.id ? "#" + a.id : "") + "]";
      }
      var seen = new WeakSet();
      return JSON.stringify(a, function (k, v) {
        if (typeof v === "object" && v !== null) {
          if (seen.has(v)) return "[Circular]";
          seen.add(v);
        }
        if (typeof v === "bigint") return v.toString() + "n";
        if (typeof v === "function") return "[Function: " + (v.name || "anonymous") + "]";
        if (typeof v === "undefined") return "undefined";
        return v;
      });
    } catch (e) {
      try { return String(a); } catch (e2) { return "[Unserializable]"; }
    }
  }

  function clamp(s) {
    if (typeof s !== "string") return s;
    return s.length <= MAX_ARG_LEN ? s : s.slice(0, MAX_ARG_LEN) + "…[truncated]";
  }

  // Pull source/line/col from the first frame that isn't our own. Tolerant of V8
  // ("at fn (url:line:col)") and Firefox/Safari ("fn@url:line:col") shapes.
  var FRAME = /\(?(\S+?):(\d+):(\d+)\)?\s*$/;
  function parseStack(stack) {
    if (!stack) return null;
    var lines = String(stack).split("\n");
    for (var i = 0; i < lines.length; i++) {
      var line = lines[i];
      if (line.indexOf("/__kyai/") >= 0 || line.indexOf("__kyaiConsole") >= 0) continue;
      var m = FRAME.exec(line);
      if (m) return { source: m[1], line: parseInt(m[2], 10), col: parseInt(m[3], 10) };
    }
    return null;
  }

  function enqueue(ev) {
    if (queue.length >= MAX_QUEUE) { dropped++; return; }
    queue.push(ev);
    schedule();
  }

  function schedule() {
    if (timer !== null) return;
    timer = setTimeout(function () { timer = null; flush(false); }, FLUSH_MS);
  }

  // Take a leading slice that stays under the byte cap (always at least one event); the rest stays
  // queued for the next flush.
  function takeBatch() {
    var batch = [];
    var bytes = 0;
    while (queue.length > 0) {
      var sz = 0;
      try { sz = JSON.stringify(queue[0]).length; } catch (e) { sz = 0; }
      if (batch.length > 0 && bytes + sz > MAX_BATCH_BYTES) break;
      batch.push(queue.shift());
      bytes += sz;
    }
    return batch;
  }

  function flush(useBeacon) {
    if (queue.length === 0) return;
    if (sending && !useBeacon) return;

    var batch = takeBatch();
    if (batch.length === 0) return;

    var body = { token: TOKEN, pageLoadId: pageLoadId, events: batch };
    if (dropped > 0) { body.droppedClient = dropped; dropped = 0; }

    var json;
    try { json = JSON.stringify(body); } catch (e) { return; }

    // text/plain keeps the cross-origin POST a "simple request" (no CORS preflight); the server
    // parses the JSON body regardless of the declared content type.
    if (useBeacon && navigator.sendBeacon) {
      try {
        if (navigator.sendBeacon(INGEST, new Blob([json], { type: "text/plain;charset=UTF-8" }))) return;
      } catch (e) { /* fall through to keepalive fetch */ }
    }

    sending = true;
    try {
      fetch(INGEST, {
        method: "POST",
        headers: { "Content-Type": "text/plain;charset=UTF-8" },
        body: json,
        keepalive: !!useBeacon,
        credentials: "omit",
        cache: "no-store"
      }).then(function () {
        sending = false;
        if (queue.length > 0) schedule();
      })["catch"](function () {
        sending = false;   // drop silently — NEVER console.* here, or we feed back
      });
    } catch (e) {
      sending = false;
    }
  }

  function record(level, args, withStack) {
    var serialized = [];
    var n = Math.min(args.length, MAX_ARGS);
    for (var i = 0; i < n; i++) serialized.push(clamp(serializeArg(args[i])));
    var ev = { level: level, args: serialized, text: serialized.join(" "), timestamp: nowIso() };
    var src = null;
    try { src = parseStack(new Error().stack); } catch (e) {}
    if (src) { ev.source = src.source; ev.line = src.line; ev.col = src.col; }
    if (withStack) { try { ev.stack = new Error().stack; } catch (e) {} }
    enqueue(ev);
  }

  ["log", "info", "warn", "error", "debug"].forEach(function (level) {
    var native = nativeConsole[level] || nativeConsole.log;
    console[level] = function () {
      try { record(level, Array.prototype.slice.call(arguments), level === "error"); }
      catch (e) { /* capture must never break the app */ }
      return native.apply(console, arguments);
    };
  });

  window.addEventListener("error", function (e) {
    try {
      var stack = e.error && e.error.stack ? String(e.error.stack) : null;
      var msg = String(e.message || (e.error && e.error.message) || "Uncaught error");
      var ev = {
        level: "exception",
        args: [msg],
        text: msg,
        source: e.filename || (stack ? (parseStack(stack) || {}).source : undefined),
        line: e.lineno || undefined,
        col: e.colno || undefined,
        stack: stack || undefined,
        timestamp: nowIso()
      };
      enqueue(ev);
    } catch (err) {}
  });

  window.addEventListener("unhandledrejection", function (e) {
    try {
      var reason = e.reason;
      var stack = reason && reason.stack ? String(reason.stack) : null;
      var msg = reason instanceof Error ? (reason.name + ": " + reason.message) : clamp(serializeArg(reason));
      var ev = { level: "unhandledrejection", args: [msg], text: msg, stack: stack || undefined, timestamp: nowIso() };
      var src = parseStack(stack);
      if (src) { ev.source = src.source; ev.line = src.line; ev.col = src.col; }
      enqueue(ev);
    } catch (err) {}
  });

  // Flush whatever is queued when the tab is hidden / navigates away (best-effort, beacon-first).
  function flushBeacon() { flush(true); }
  window.addEventListener("pagehide", flushBeacon);
  window.addEventListener("visibilitychange", function () {
    if (document.visibilityState === "hidden") flushBeacon();
  });

  // Announce the page load so the agent can see a fresh boundary (and confirm capture is live).
  enqueue({
    level: "info",
    args: ["[kyai] console capture attached", location.href, navigator.userAgent],
    text: "[kyai] console capture attached " + location.href,
    source: location.href,
    timestamp: nowIso()
  });
})();
