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

  /*
   * Eval return channel — the half-duplex complement to console capture. We long-poll ky-ai-browser
   * for work the agent's runtime-inspection tools queued (evaluate_js / query_dom / reload_page),
   * run it in the page, and POST the result back. Same loopback origin + misroute token as ingest;
   * text/plain keeps the cross-origin POST a CORS-simple request. Must never throw into the app, and
   * never console.* (that would feed back into capture).
   */
  var EVAL_BASE = INGEST.replace(/\/console$/, "/eval");
  var EVAL_POLL = EVAL_BASE + "/poll";
  var EVAL_RESULT = EVAL_BASE + "/result";

  // Human overrides: the Pause/Stop icons (on the badge, and Stop again on the paused pill) post here
  // directly (not through the eval channel — this is the human overriding the agent, not the agent's
  // own start/stop_interaction). Pause is the brief, resumable one (paired with resume). Stop/kill is
  // the hard one that also blocks reads — deliberately NOT paired with a revive route: resuming after a
  // kill happens by the human telling the agent in chat, not by clicking anything here.
  var INTERACTION_BASE = INGEST.replace(/\/console$/, "/interaction");
  var INTERACTION_PAUSE = INTERACTION_BASE + "/pause";
  var INTERACTION_RESUME = INTERACTION_BASE + "/resume";
  var INTERACTION_KILL = INTERACTION_BASE + "/kill";
  // The held-reload pill's click — hands the dev server's live-reload back for this session without
  // ending it (see the reloadHold module below). Same token-guard/CORS shape as the overrides above.
  var RELOAD_RELEASE = INGEST.replace(/\/console$/, "/reload/release");

  function evalTypeOf(v) {
    if (v === null) return "null";
    if (Array.isArray(v)) return "array";
    return typeof v;
  }

  function errPayload(e) {
    var msg;
    try { msg = (e instanceof Error) ? (e.name + ": " + e.message) : String(e); } catch (e2) { msg = "error"; }
    var out = { ok: false, error: msg };
    try { if (e && e.stack) out.stack = String(e.stack); } catch (e3) {}
    return out;
  }

  function postResult(id, payload) {
    try {
      fetch(EVAL_RESULT, {
        method: "POST",
        headers: { "Content-Type": "text/plain;charset=UTF-8" },
        body: JSON.stringify({ token: TOKEN, id: id, payload: payload }),
        credentials: "omit",
        cache: "no-store"
      })["catch"](function () { /* server gone — nothing to do */ });
    } catch (e) { /* never throw into the app */ }
  }

  // The human clicked Pause or Stop: reflect it locally right away (no need to wait for the round-trip)
  // and tell the server so the agent's next call is refused with a clear reason. Resume is the mirror
  // for Pause — only the human's own click clears it, never the agent. Stop has no such mirror; it
  // clears only when the agent's own start_interaction starts a clean new session (see EvalChannel).
  function postInteractionOverride(url) {
    try {
      fetch(url, {
        method: "POST",
        headers: { "Content-Type": "text/plain;charset=UTF-8" },
        body: JSON.stringify({ token: TOKEN }),
        credentials: "omit",
        cache: "no-store"
      })["catch"](function () { /* server gone — nothing to do */ });
    } catch (e) { /* never throw into the app */ }
  }
  function onUserPause() { try { overlay.showPaused(); } catch (e) {} postInteractionOverride(INTERACTION_PAUSE); }
  function onUserResume() { try { overlay.clearPaused(); } catch (e) {} postInteractionOverride(INTERACTION_RESUME); }
  function onUserKill() { try { overlay.showKilled(); } catch (e) {} postInteractionOverride(INTERACTION_KILL); }

  // JSON-safe deep copy of a value (caps depth/breadth, tags functions/DOM/Errors, breaks cycles) so
  // evaluate_js can return real structured JSON (asJson:true) instead of a one-line string rendering.
  // opts.maxDepth / opts.maxBreadth tune the caps (default 6 / 500 — the eval-channel defaults);
  // read_component passes tighter caps so a component holding a big object graph can't blow the token
  // budget. Both depth and breadth overflow are MARKED (not silently dropped) so the reader can tell a
  // value was elided and widen if needed.
  function jsonSafe(v, opts) {
    var maxDepth = (opts && typeof opts.maxDepth === "number") ? opts.maxDepth : 6;
    var maxBreadth = (opts && typeof opts.maxBreadth === "number") ? opts.maxBreadth : 500;
    var seen = (typeof WeakSet === "function") ? new WeakSet() : null;
    function clean(val, depth) {
      if (val === null) return null;
      var t = typeof val;
      if (t === "string" || t === "number" || t === "boolean") return val;
      if (t === "undefined") return "undefined";
      if (t === "bigint") return val.toString() + "n";
      if (t === "symbol") return val.toString();
      if (t === "function") return "[Function: " + (val.name || "anonymous") + "]";
      if (val instanceof Error) return val.name + ": " + val.message;
      if (typeof Node !== "undefined" && val instanceof Node) return "[" + (val.nodeName || "Node") + (val.id ? "#" + val.id : "") + "]";
      if (depth >= maxDepth) return Array.isArray(val) ? "[Array(" + val.length + ")]" : "[Object …]";
      if (seen) { if (seen.has(val)) return "[Circular]"; seen.add(val); }
      if (Array.isArray(val)) {
        var a = [], n = Math.min(val.length, maxBreadth);
        for (var i = 0; i < n; i++) a.push(clean(val[i], depth + 1));
        if (val.length > n) a.push("…+" + (val.length - n) + " more");
        return a;
      }
      var out = {}, allKeys = Object.keys(val), keys = allKeys.slice(0, maxBreadth);
      for (var j = 0; j < keys.length; j++) { try { out[keys[j]] = clean(val[keys[j]], depth + 1); } catch (e) { out[keys[j]] = "[unreadable]"; } }
      if (allKeys.length > keys.length) out["…"] = "+" + (allKeys.length - keys.length) + " more keys";
      return out;
    }
    try { return clean(v, 0); } catch (e) { return "[unserializable]"; }
  }

  function evalOk(req, v) {
    return req.asJson ? { ok: true, type: evalTypeOf(v), json: jsonSafe(v) } : { ok: true, type: evalTypeOf(v), value: clamp(serializeArg(v)) };
  }

  // returns a payload, or a Promise<payload> when awaitPromise resolves a thenable
  function doEval(req) {
    try {
      overlay.hint("reading: " + short(req.expression, 60));
      var indirect = eval;                 // indirect eval → evaluate in global scope
      var result = indirect(req.expression);
      if (req.awaitPromise && result && typeof result.then === "function") {
        return result.then(function (v) { return evalOk(req, v); }, function (e) { return errPayload(e); });
      }
      return evalOk(req, result);
    } catch (e) { return errPayload(e); }
  }

  // Describe an element. Minimal by default — { tag, id?, text } — which is all a click/key/etc.
  // confirmation needs ("did I hit the right thing"); pass detail:true for the full picture
  // (classes, every attribute, rect, clipped outerHTML). Keeping the default terse is the main
  // token lever on multi-step flows.
  function describeEl(el, detail) {
    var out = { tag: (el.tagName || "").toLowerCase() };
    try { if (el.id) out.id = el.id; } catch (e) {}
    try {
      var t = (el.textContent || "").replace(/\s+/g, " ").trim();
      out.text = t.length > 200 ? t.slice(0, 200) + "…" : t;
    } catch (e) {}
    if (!detail) return out;
    out.classes = []; out.attributes = {};
    try { if (el.classList) out.classes = Array.prototype.slice.call(el.classList); } catch (e) {}
    try {
      for (var i = 0; i < el.attributes.length; i++) {
        var a = el.attributes[i];
        out.attributes[a.name] = clamp(a.value);
      }
    } catch (e) {}
    try {
      var r = el.getBoundingClientRect();
      out.rect = { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) };
    } catch (e) {}
    try {
      var h = el.outerHTML || "";
      out.html = h.length > 1000 ? h.slice(0, 1000) + "…" : h;
    } catch (e) {}
    return out;
  }

  function doQuery(req) {
    try {
      overlay.hint("reading: " + short(req.selector, 60));
      var nodes = document.querySelectorAll(req.selector);
      var count = nodes.length;
      var max = req.all ? Math.min(count, req.limit || 20) : Math.min(count, 1);
      var els = [];
      for (var i = 0; i < max; i++) els.push(describeEl(nodes[i], req.detail !== false));  // inspection → detailed unless detail:false
      return { ok: true, selector: req.selector, count: count, returned: els.length, elements: els };
    } catch (e) { var p = errPayload(e); p.selector = req.selector; return p; }
  }

  /*
   * Component-state readback — the data behind what rendered, not just the text. The rendered text
   * proves "the user saw it change"; the bound model proves "the app's state actually changed". The
   * trap with a hand-written ng.getComponent(el).value is that modern Angular values are SIGNALS:
   * cmp.value is a getter FUNCTION, so reading it (not calling it) hands you the function and comes
   * back empty — you must call cmp.value(). This walks the component, calls signal getters, unwraps
   * FormControls, and lists drivable methods, so a clean programmatic read works regardless of how
   * the value is held. Generic Angular, not specific to any component library (Mantic et al.).
   */

  // Is `v` an Angular signal getter? Signals are functions branded with a SIGNAL symbol; writable/
  // computed ones also carry set/update. Reading a signal is a pure get, so calling it is safe (unlike
  // blindly invoking an arbitrary zero-arg method, which could have side effects).
  function isAngularSignal(v) {
    if (typeof v !== "function") return false;
    try {
      var syms = Object.getOwnPropertySymbols(v);
      for (var i = 0; i < syms.length; i++) if (String(syms[i]) === "Symbol(SIGNAL)") return true;
    } catch (e) {}
    return typeof v.set === "function" && typeof v.update === "function";
  }

  // An Angular forms AbstractControl (FormControl/Group/Array) — value lives behind .value, not the field.
  function isFormControl(v) {
    return !!v && typeof v === "object" && typeof v.setValue === "function" && ("value" in v) && ("valid" in v);
  }

  // read_component is lean BY KIND, then capped by SIZE. The default payload expands only the things
  // you usually want — signals (resolved), FormControls (unwrapped) and plain scalars — and collapses
  // every complex/framework object (injected services, RxJS Subjects, ElementRef/DestroyRef,
  // errorHandler, _lView graphs) to a one-line type tag, listing its name under `objects` so you know
  // it's there. That alone kills ~90% of the noise. On top of that, an expanded value over the
  // per-field cap (or once the running total is spent) is summarized too. Both are escape-hatched:
  // pass `fields:["options","foo"]` to expand exactly those (returned in full, only depth-limited), or
  // raise `depth`.
  var COMP_DEFAULT_DEPTH = 3;     // signals usually nest shallowly; deep graphs are the blow-up case
  var COMP_MAX_BREADTH = 50;
  var COMP_FIELD_CHARS = 2000;    // a single expanded value over this is summarized (unless requested)
  var COMP_TOTAL_CHARS = 16000;   // running budget across all auto-expanded values

  function compSummary(v) {
    if (Array.isArray(v)) return "[Array(" + v.length + ")]";
    if (v && typeof v === "object") {
      var ctor = (v.constructor && v.constructor.name) || "Object";
      if (ctor !== "Object") return "[" + ctor + "]";                  // Subject, ElementRef, DestroyRef, …
      var n = 0; try { n = Object.keys(v).length; } catch (e) {}
      return "[Object(" + n + " keys)]";
    }
    return "[" + (typeof v) + "]";
  }

  // A scalar is cheap to include verbatim; complex objects/arrays are the blow-up risk.
  function compIsScalar(v) {
    var t = typeof v;
    return v === null || t === "string" || t === "number" || t === "boolean" || t === "bigint" || t === "undefined" || t === "symbol";
  }

  // Read the bound state of the Angular component on (or above) `target` (an element or selector).
  // Returns { ok, component, state, signals, formControls?, methods, note? }, where `state` is a
  // JSON-safe snapshot with signals already resolved (called) and FormControls unwrapped to their
  // value, and `methods` are the callable members you can drive (e.g. selectIndex, setValue).
  // By default `state` expands signals + FormControls + scalars and collapses complex objects to type
  // tags; opts.fields:["name",…] expands exactly those instead (in full, depth-limited); opts.depth
  // overrides the nesting cap. `signals`/`formControls`/`methods`/`objects` always list ALL names
  // (cheap), so the discovery surface stays complete — anything in `objects` is expandable via fields.
  function readComponent(target, opts) {
    opts = opts || {};
    overlay.hint("reading component: " + short(typeof target === "string" ? target : "", 60));
    var el = (typeof target === "string") ? document.querySelector(target) : target;
    if (!el) return { ok: false, error: "no element matches " + (typeof target === "string" ? "selector " + target : "target") };
    var ng = window.ng;
    if (!ng || typeof ng.getComponent !== "function")
      return { ok: false, error: "Angular debug API (window.ng) unavailable — needs a dev / non-production build" };
    var cmp = null, node = el, hops = 0;
    while (node && !cmp && hops < 50) { try { cmp = ng.getComponent(node); } catch (e) {} node = node.parentElement; hops++; }
    if (!cmp) return { ok: false, error: "no Angular component found on or above the element" };

    var fields = Array.isArray(opts.fields) && opts.fields.length ? opts.fields : null;
    var depth = (typeof opts.depth === "number" && opts.depth >= 0) ? Math.min(opts.depth, 6) : COMP_DEFAULT_DEPTH;
    var total = 0, trimmed = false, collapsed = false;

    function isRequested(k) { return fields && fields.indexOf(k) >= 0; }

    // Expand a value into `state[k]`, depth-limited. When the caller named `k` in `fields` it's
    // returned in full; otherwise it's size-budgeted — a value over the per-field cap (or once the
    // running total is spent) is replaced with a one-line summary pointing at the fields escape hatch.
    function expand(k, value) {
      var safe = jsonSafe(value, { maxDepth: depth, maxBreadth: COMP_MAX_BREADTH });
      if (isRequested(k)) { state[k] = safe; return; }
      var len = 0; try { len = JSON.stringify(safe).length; } catch (e) { len = 0; }
      if (len > COMP_FIELD_CHARS || total > COMP_TOTAL_CHARS) {
        state[k] = compSummary(value) + " — " + len + " chars elided; read via fields:[\"" + k + "\"]";
        trimmed = true;
        return;
      }
      state[k] = safe; total += len;
    }

    var name = (cmp.constructor && cmp.constructor.name) || "Component";
    var state = {}, signals = [], controls = [], methods = [], objects = [];
    var keys = [];
    try { keys = Object.keys(cmp); } catch (e) {}
    for (var i = 0; i < keys.length && i < 200; i++) {
      var k = keys[i], raw;
      try { raw = cmp[k]; } catch (e) { state[k] = "[unreadable]"; continue; }
      try {
        // Always classify (names are cheap and complete the discovery surface); only EXPAND a value
        // when it's primary (signal/FormControl), a plain scalar, or the caller asked for it by name.
        if (isAngularSignal(raw)) { signals.push(k); if (!fields || isRequested(k)) expand(k, raw()); }
        else if (isFormControl(raw)) { controls.push(k); if (!fields || isRequested(k)) expand(k, raw.value); }
        else if (typeof raw === "function") { methods.push(k); }
        else if (compIsScalar(raw)) { if (!fields || isRequested(k)) expand(k, raw); }
        else {
          // complex object / array — framework plumbing or large data. Default: collapse to a tag.
          // Under a `fields` filter, list the name only (like signals/scalars) unless it was requested.
          objects.push(k);
          if (isRequested(k)) expand(k, raw);
          else if (!fields) { state[k] = compSummary(raw); collapsed = true; }
        }
      } catch (e) { state[k] = "[unreadable]"; }
    }
    // The component class's own methods live on the prototype (Angular components extend Object
    // directly, so stop at Object.prototype) — these are what you can drive as a fallback.
    try {
      var proto = Object.getPrototypeOf(cmp);
      while (proto && proto !== Object.prototype && methods.length < 80) {
        var pk = Object.getOwnPropertyNames(proto);
        for (var j = 0; j < pk.length; j++) {
          var nm = pk[j];
          if (nm === "constructor" || methods.indexOf(nm) >= 0) continue;
          var desc = Object.getOwnPropertyDescriptor(proto, nm);
          if (desc && typeof desc.value === "function") methods.push(nm);
        }
        proto = Object.getPrototypeOf(proto);
      }
    } catch (e) {}

    var out = { ok: true, component: name, state: state, signals: signals, methods: methods };
    if (controls.length) out.formControls = controls;
    if (objects.length) out.objects = objects;
    if (collapsed || trimmed) {
      out.note = collapsed
        ? "complex/framework fields are collapsed to type tags by default — expand the ones you need with fields:[…] (raise depth to nest deeper)"
        : "some expanded values were summarized to stay under the token cap — re-read with fields:[…] or raise depth";
    }
    return out;
  }

  window.__kyai = window.__kyai || {};
  window.__kyai.readComponent = readComponent;
  window.__kyai.isSignal = isAngularSignal;

  // ---- synthetic input helpers (events are isTrusted:false — they drive JS handlers, not CSS :hover) ----

  // elementFromPoint, descending into open shadow roots so a web component's inner element is hit.
  function elementFromPointDeep(x, y) {
    var el = null;
    try { el = document.elementFromPoint(x, y); } catch (e) { return null; }
    while (el && el.shadowRoot) {
      var inner = el.shadowRoot.elementFromPoint(x, y);
      if (!inner || inner === el) break;
      el = inner;
    }
    return el;
  }

  // Build a Pointer/Mouse event of `type` at (x,y). bubbles defaults true; pass opts.bubbles=false for
  // enter/leave (which don't bubble). Falls back PointerEvent → MouseEvent → legacy initMouseEvent.
  function mkMouse(type, x, y, opts) {
    opts = opts || {};
    var init = {
      bubbles: opts.bubbles === false ? false : true,
      cancelable: true, composed: true, view: window,
      clientX: x, clientY: y, screenX: x, screenY: y,
      button: opts.button || 0, buttons: opts.buttons || 0,
      ctrlKey: !!opts.ctrl, shiftKey: !!opts.shift, altKey: !!opts.alt, metaKey: !!opts.meta
    };
    if (type.indexOf("pointer") === 0 && typeof PointerEvent === "function") {
      init.pointerId = 1; init.pointerType = "mouse"; init.isPrimary = true;
      try { return new PointerEvent(type, init); } catch (e) {}
    }
    try { return new MouseEvent(type, init); } catch (e) {}
    try {
      var ev = document.createEvent("MouseEvents");
      ev.initMouseEvent(type, init.bubbles, true, window, 0, x, y, x, y, init.ctrlKey, init.altKey, init.shiftKey, init.metaKey, init.button, null);
      return ev;
    } catch (e2) { return null; }
  }

  function fire(el, type, x, y, opts) {
    try { var ev = mkMouse(type, x, y, opts); if (ev) el.dispatchEvent(ev); } catch (e) {}
  }

  function delay(ms) {
    return ms > 0 ? new Promise(function (r) { setTimeout(r, ms); }) : Promise.resolve();
  }

  // Find the most specific element matching visible text: the deepest element (inside `withinSel`,
  // else the whole document) whose collapsed textContent equals (exact) or contains (exact:false)
  // `text`, preferring visible ones. Lets click/etc. target by label instead of selector/coordinate.
  function findByText(text, withinSel, exact) {
    var root = withinSel ? document.querySelector(withinSel) : (document.body || document.documentElement);
    if (!root) return null;
    var all = root.querySelectorAll("*");
    var matches = [];
    for (var i = 0; i < all.length; i++) {
      var el = all[i];
      var t = (el.textContent || "").replace(/\s+/g, " ").trim();
      if (exact ? (t === text) : (t.indexOf(text) >= 0)) matches.push(el);
    }
    if (!matches.length) return null;
    // keep the leaves of the match set (no other match nested inside) → the most specific elements
    var leaves = matches.filter(function (m) { return !matches.some(function (o) { return o !== m && m.contains(o); }); });
    var pool = leaves.length ? leaves : matches;
    var vis = pool.filter(function (e) { var r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; });
    return vis[0] || pool[0];
  }

  // Resolve a request's target to { el, x, y }: by selector, by visible text (text[, within, exact]),
  // or by viewport coordinate (hit-tested through shadow DOM). Selector/text centre the element.
  function resolveTarget(req) {
    var el = null;
    if (req.selector) el = document.querySelector(req.selector);
    else if (req.text != null && req.text !== "") el = findByText(req.text, req.within, req.exact !== false);
    if (req.selector || (req.text != null && req.text !== "")) {
      if (!el) return { el: null };
      try { el.scrollIntoView({ block: "center", inline: "center" }); } catch (e) {}
      var r = el.getBoundingClientRect();
      return { el: el, x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
    }
    if (typeof req.x !== "number" || typeof req.y !== "number") return { el: null, badArgs: true };
    return { el: elementFromPointDeep(req.x, req.y), x: req.x, y: req.y };
  }

  /*
   * Supervision overlay — a fixed, non-interactable (pointer-events:none, so it never intercepts the
   * synthetic events or the human's real ones) red frame with a cursor icon, shown while interaction
   * is open. Each action animates the cursor (ripple on click, key cap on a key, glide on move) so a
   * watching human sees exactly what the agent is doing. Lives in a shadow root so the app's CSS and
   * its document.querySelectorAll never touch it. All best-effort — never throws into the app.
   */
  var OVERLAY_CSS =
    ".kyai-frame{position:absolute;inset:0;box-sizing:border-box;border:3px solid rgba(229,57,53,.95);display:none;}" +
    // One centered row holding every top pill, so they sit side by side and the GROUP stays centered as
    // pills come and go. They can't each center themselves (translateX(-50%) would stack them on the same
    // spot); the row owns the positioning and the pills are plain in-flow flex items.
    ".kyai-topbar{position:absolute;top:8px;left:50%;transform:translateX(-50%);display:flex;align-items:flex-start;gap:6px;max-width:92vw;}" +
    ".kyai-badge{display:flex;align-items:center;gap:6px;font:600 11px/1.4 system-ui,sans-serif;color:#fff;background:rgba(229,57,53,.95);padding:3px 8px 3px 10px;border-radius:10px;letter-spacing:.3px;white-space:nowrap;max-width:70vw;display:none;}" +
    ".kyai-badge-text{flex:1 1 auto;min-width:0;overflow:hidden;white-space:nowrap;text-overflow:ellipsis;}" +
    // The real, clickable controls in an otherwise pointer-events:none overlay — pointer-events:auto
    // opts just these icons back in so the rest of the frame never intercepts the page's own clicks.
    // Reused on both the badge (Pause + Stop) and the paused pill (Stop again, for a direct escalation).
    // SVG, not a Unicode ⏸/⏹ glyph — those render via an emoji/symbol font whose visible ink sits at an
    // inconsistent, often asymmetric offset within its character cell, so flex-centering the TEXT still
    // left the icon looking off-center. A hand-drawn shape has an exact, known bounding box instead.
    ".kyai-icon-btn{flex:none;pointer-events:auto;cursor:pointer;display:none;align-items:center;justify-content:center;width:16px;height:16px;border-radius:50%;background:rgba(0,0,0,.3);}" +
    ".kyai-icon-btn:hover{background:rgba(0,0,0,.5);}" +
    // Nudged up/left by a pixel: each glyph's ink sits marginally low-right of its viewBox centre, so
    // pure flex-centering of the SVG box still left the visible shape looking off inside the circle.
    ".kyai-icon-btn svg{display:block;transform:translate(-1px,-1px);}" +
    ".kyai-paused{display:flex;align-items:center;gap:6px;font:600 11px/1.4 system-ui,sans-serif;color:#fff;background:rgba(66,66,66,.95);padding:3px 8px 3px 10px;border-radius:10px;letter-spacing:.3px;white-space:nowrap;max-width:80vw;display:none;}" +
    ".kyai-paused-text{pointer-events:auto;cursor:pointer;flex:1 1 auto;min-width:0;overflow:hidden;text-overflow:ellipsis;}" +
    ".kyai-paused-text:hover{text-decoration:underline;}" +
    // The held-reload pill: a SECOND badge sharing the row with the session badge (it only ever shows
    // while a session is open, so it always has that badge beside it). Same red as the badge — it's part
    // of the agent-is-driving state, not a separate mode like the grey paused pill. Its ▶ is a real
    // control (the pill's text isn't clickable); the ↻ ahead of the text is decoration.
    ".kyai-reload{display:flex;align-items:center;gap:6px;font:600 11px/1.4 system-ui,sans-serif;color:#fff;background:rgba(229,57,53,.95);padding:3px 8px 3px 10px;border-radius:10px;letter-spacing:.3px;white-space:nowrap;max-width:70vw;display:none;}" +
    ".kyai-reload-icon{flex:none;display:flex;align-items:center;justify-content:center;}" +
    ".kyai-reload-icon svg{display:block;}" +
    ".kyai-reload-text{flex:1 1 auto;min-width:0;overflow:hidden;text-overflow:ellipsis;}" +
    ".kyai-cursor{position:absolute;left:0;top:0;width:24px;height:24px;will-change:transform;filter:drop-shadow(0 1px 2px rgba(0,0,0,.5));display:none;}" +
    ".kyai-cursor svg{display:block;}" +
    ".kyai-ripple{position:absolute;width:10px;height:10px;margin:-5px 0 0 -5px;border:2px solid rgba(229,57,53,.9);border-radius:50%;animation:kyai-rip .6s ease-out forwards;}" +
    "@keyframes kyai-rip{from{transform:scale(.3);opacity:.9}to{transform:scale(4.5);opacity:0}}" +
    ".kyai-clabel{position:absolute;top:50%;left:50%;transform:translate(-50%,-50%) scale(.92);font:600 15px/1.4 system-ui,sans-serif;color:#fff;background:rgba(229,57,53,.95);padding:8px 16px;border-radius:8px;letter-spacing:.2px;white-space:nowrap;max-width:80vw;overflow:hidden;text-overflow:ellipsis;box-shadow:0 4px 14px rgba(0,0,0,.35);animation:kyai-clabel 2s ease-out forwards;}" +
    "@keyframes kyai-clabel{0%{opacity:0;transform:translate(-50%,-50%) scale(.92)}8%{opacity:1;transform:translate(-50%,-50%) scale(1)}85%{opacity:1}100%{opacity:0;transform:translate(-50%,-50%) scale(.97)}}";
  var CURSOR_SVG = '<svg width="24" height="24" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">' +
    '<path d="M4 2l6 14 2.2-5.6L18 8z" fill="#fff" stroke="#e53935" stroke-width="1.6" stroke-linejoin="round"/></svg>';
  var ICON_PAUSE_SVG = '<svg width="9" height="9" viewBox="0 0 9 9" xmlns="http://www.w3.org/2000/svg">' +
    '<rect x="1.5" y="0.5" width="2" height="8" rx="0.5" fill="#fff"/><rect x="5.5" y="0.5" width="2" height="8" rx="0.5" fill="#fff"/></svg>';
  var ICON_STOP_SVG = '<svg width="9" height="9" viewBox="0 0 9 9" xmlns="http://www.w3.org/2000/svg">' +
    '<rect x="1" y="1" width="7" height="7" rx="1" fill="#fff"/></svg>';
  // Play triangle — the held-reload pill's "let Angular reload again" control. Drawn on the same 9x9
  // grid as Pause/Stop so all three icon buttons share one optical size.
  var ICON_PLAY_SVG = '<svg width="9" height="9" viewBox="0 0 9 9" xmlns="http://www.w3.org/2000/svg">' +
    '<path d="M2.25 1L8 4.5 2.25 8z" fill="#fff"/></svg>';
  // Circular-arrow refresh mark for the held-reload pill. Unlike the Pause/Stop/Play icons this one is
  // decoration, not a control, so it takes no pointer-events.
  var ICON_RELOAD_SVG = '<svg width="11" height="11" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">' +
    '<path d="M12 4V1L8 5l4 4V6c3.31 0 6 2.69 6 6 0 1.01-.25 1.97-.7 2.8l1.46 1.46A7.93 7.93 0 0020 12c0-4.42-3.58-8-8-8zm0 14c-3.31 0-6-2.69-6-6 0-1.01.25-1.97.7-2.8L5.24 7.74A7.93 7.93 0 004 12c0 4.42 3.58 8 8 8v3l4-4-4-4v3z" fill="#fff"/></svg>';

  /*
   * Supervision overlay — a fixed, non-interactable red frame + cursor, shown only while a session is
   * open (start_interaction..stop_interaction). The top-center badge is shared with the read-only
   * indicator below: only one badge ever exists, so a read that happens mid-session doesn't lay a
   * second pill on top of "● ky-ai agent interacting" — it just borrows the same spot for a moment and
   * hands it back. Lives in a shadow root so the app's CSS/querySelectorAll never touch it.
   *
   * Icons break the "non-interactable" rule on purpose (pointer-events:auto on just those elements):
   *   ⏸ Pause  — brief, resumable "hands off for a moment"; reads still work. Swaps the badge for a
   *              paused pill; resumed by clicking its text (or the agent can call wait_for_resume
   *              instead of retrying itself). The paused pill carries its OWN Stop icon too, for a
   *              direct Pause→Stop escalation without resuming first.
   *   ⏹ Stop   — the hard one: kills the WHOLE session, reads included, no auto-retry path for the
   *              agent at all. There is deliberately no pill, no UI of any kind for it afterwards —
   *              clicking it removes everything. Resuming means the human tells the agent in chat,
   *              which then calls start_interaction for a clean new session (that clears the kill
   *              server-side; see EvalChannel.SetInteraction) — nothing to click here for that.
   * Both are the human's own override — the agent never gets a tool call that clears the pause, and
   * there is no tool OR UI control at all that clears a kill except the agent's own fresh session.
   */
  var overlay = (function () {
    var host = null, root = null, frame = null, cursor = null, topbar = null, badge = null, badgeText = null,
      badgePause = null, badgeKill = null, pausedPill = null, pausedText = null, pausedKill = null,
      reloadPill = null, reloadText = null, reloadPlay = null;
    var shown = false, paused = false, killed = false, cx = 0, cy = 0, curLabel = null, curLabelTimer = null, hintTimer = null;
    function vw() { return window.innerWidth || (document.documentElement || {}).clientWidth || 0; }
    function vh() { return window.innerHeight || (document.documentElement || {}).clientHeight || 0; }

    function ensure() {
      if (host) return;
      host = document.createElement("kyai-overlay");
      host.setAttribute("aria-hidden", "true");
      var s = host.style;
      s.position = "fixed"; s.left = "0"; s.top = "0"; s.width = "100%"; s.height = "100%";
      s.margin = "0"; s.padding = "0"; s.border = "0"; s.pointerEvents = "none"; s.zIndex = "2147483647";
      root = host.attachShadow ? host.attachShadow({ mode: "open" }) : host;
      var style = document.createElement("style"); style.textContent = OVERLAY_CSS; root.appendChild(style);
      frame = document.createElement("div"); frame.className = "kyai-frame"; root.appendChild(frame);
      topbar = document.createElement("div"); topbar.className = "kyai-topbar";
      badge = document.createElement("div"); badge.className = "kyai-badge";
      badgeText = document.createElement("span"); badgeText.className = "kyai-badge-text"; badge.appendChild(badgeText);
      badgePause = document.createElement("span"); badgePause.className = "kyai-icon-btn"; badgePause.innerHTML = ICON_PAUSE_SVG;
      badgePause.title = "Pause"; badgePause.setAttribute("role", "button"); badgePause.setAttribute("tabindex", "0");
      badgePause.addEventListener("click", function (e) { e.preventDefault(); e.stopPropagation(); onUserPause(); });
      badge.appendChild(badgePause);
      badgeKill = document.createElement("span"); badgeKill.className = "kyai-icon-btn"; badgeKill.innerHTML = ICON_STOP_SVG;
      badgeKill.title = "Stop"; badgeKill.setAttribute("role", "button"); badgeKill.setAttribute("tabindex", "0");
      badgeKill.addEventListener("click", function (e) { e.preventDefault(); e.stopPropagation(); onUserKill(); });
      badge.appendChild(badgeKill);
      topbar.appendChild(badge);
      pausedPill = document.createElement("div"); pausedPill.className = "kyai-paused";
      pausedText = document.createElement("span"); pausedText.className = "kyai-paused-text";
      pausedText.textContent = "⏸ paused — click to let ky-ai continue";
      pausedText.setAttribute("role", "button"); pausedText.setAttribute("tabindex", "0");
      pausedText.addEventListener("click", function (e) { e.preventDefault(); e.stopPropagation(); onUserResume(); });
      pausedPill.appendChild(pausedText);
      pausedKill = document.createElement("span"); pausedKill.className = "kyai-icon-btn"; pausedKill.innerHTML = ICON_STOP_SVG;
      pausedKill.title = "Stop"; pausedKill.setAttribute("role", "button"); pausedKill.setAttribute("tabindex", "0");
      pausedKill.style.display = "flex";   // always visible whenever the parent pill is (pill's own display gates it)
      pausedKill.addEventListener("click", function (e) { e.preventDefault(); e.stopPropagation(); onUserKill(); });
      pausedPill.appendChild(pausedKill);
      topbar.appendChild(pausedPill);
      reloadPill = document.createElement("div"); reloadPill.className = "kyai-reload";
      var reloadIcon = document.createElement("span"); reloadIcon.className = "kyai-reload-icon"; reloadIcon.innerHTML = ICON_RELOAD_SVG;
      reloadPill.appendChild(reloadIcon);
      reloadText = document.createElement("span"); reloadText.className = "kyai-reload-text";
      reloadText.textContent = "Angular reload paused";
      reloadPill.appendChild(reloadText);
      reloadPlay = document.createElement("span"); reloadPlay.className = "kyai-icon-btn"; reloadPlay.innerHTML = ICON_PLAY_SVG;
      reloadPlay.title = "Continue Angular reloading";
      reloadPlay.setAttribute("role", "button"); reloadPlay.setAttribute("tabindex", "0");
      reloadPlay.style.display = "flex";   // always visible whenever the parent pill is (pill's own display gates it)
      reloadPlay.addEventListener("click", function (e) { e.preventDefault(); e.stopPropagation(); onUserContinueReload(); });
      reloadPill.appendChild(reloadPlay);
      topbar.appendChild(reloadPill);
      root.appendChild(topbar);
      cursor = document.createElement("div"); cursor.className = "kyai-cursor"; cursor.innerHTML = CURSOR_SVG; root.appendChild(cursor);
      put(Math.round(vw() / 2), Math.round(vh() / 2), 0);
      (document.body || document.documentElement).appendChild(host);
    }
    // Cursor motion, in two flavours. doMove retargets every ~16ms and leans on a fixed trailing
    // transition to smooth those steps into one glide — that stays exactly as it was. A ONE-SHOT glide
    // (click/focus) instead scales its duration with the distance travelled, so a cross-screen jump is
    // followable rather than a blur, and a cursor already on target costs nothing at all (e.g. when the
    // agent called move first).
    var CURSOR_STEP_MS = 120;                                        // doMove's per-step trailing transition
    var CURSOR_PX_PER_MS = 2.2, CURSOR_MIN_MS = 80, CURSOR_MAX_MS = 320;
    function put(x, y, ms) {
      cx = x; cy = y;
      if (!cursor) return;
      cursor.style.transition = ms > 0 ? "transform " + ms + "ms linear" : "none";
      cursor.style.transform = "translate(" + x + "px," + y + "px)";
    }
    function glideMsTo(x, y) {
      var dx = x - cx, dy = y - cy;
      var d = Math.sqrt(dx * dx + dy * dy);
      if (d < 1) return 0;
      return Math.max(CURSOR_MIN_MS, Math.min(CURSOR_MAX_MS, Math.round(d / CURSOR_PX_PER_MS)));
    }
    function ripple(x, y) {
      if (!root) return;
      for (var k = 0; k < 2; k++) {
        var r = document.createElement("div");
        r.className = "kyai-ripple"; r.style.left = x + "px"; r.style.top = y + "px"; r.style.animationDelay = (k * 90) + "ms";
        root.appendChild(r);
        (function (n) { setTimeout(function () { try { n.remove(); } catch (e) {} }, 800); })(r);
      }
    }
    // Replaces (rather than stacks) the previous label — a burst of fast actions (e.g. several key
    // presses in a row) would otherwise pile up overlapping, unreadable text at the same spot.
    function clabel(text) {
      if (!root) return;
      if (curLabelTimer) { clearTimeout(curLabelTimer); curLabelTimer = null; }
      if (curLabel) { try { curLabel.remove(); } catch (e) {} curLabel = null; }
      var el = document.createElement("div");
      el.className = "kyai-clabel"; el.textContent = text;
      root.appendChild(el);
      curLabel = el;
      curLabelTimer = setTimeout(function () { try { el.remove(); } catch (e) {} if (curLabel === el) curLabel = null; curLabelTimer = null; }, 2000);
    }
    // The one top-center badge: while a session is open it idles on "● ky-ai agent interacting" with
    // the Pause/Stop icons at its end; a read hint borrows the text for HINT_MS then hands it back (or,
    // with no session open, just disappears — there's no persistent state to return to, which itself
    // reads as "no session"). The icons only ever show while a session is actually open.
    var SESSION_TEXT = "● ky-ai agent interacting", HINT_MS = 2000;
    function setHint(text) {
      ensure();
      if (hintTimer) { clearTimeout(hintTimer); hintTimer = null; }
      badgeText.textContent = (shown ? "● " : "○ ") + text;
      badge.style.display = "flex";
      badgePause.style.display = badgeKill.style.display = shown ? "flex" : "none";
      hintTimer = setTimeout(function () {
        hintTimer = null;
        if (shown) { badgeText.textContent = SESSION_TEXT; }
        else { badge.style.display = "none"; badgePause.style.display = badgeKill.style.display = "none"; }
      }, HINT_MS);
    }
    return {
      // Persistent, supervised session — started by start_interaction, cleared by stop_interaction.
      // All manipulation kinds (click/move/key/type/scroll/focus/navigate) are server-gated behind this,
      // so by the time any of them runs here the frame is already showing.
      show: function () {
        try {
          ensure(); shown = true; paused = false;
          frame.style.display = "block"; cursor.style.display = "block";
          pausedPill.style.display = "none";
          if (!hintTimer) { badgeText.textContent = SESSION_TEXT; badge.style.display = "flex"; }
          badgePause.style.display = badgeKill.style.display = "flex";
        } catch (e) {}
      },
      hide: function () {
        try {
          shown = false;
          if (host) { frame.style.display = "none"; cursor.style.display = "none"; badgePause.style.display = badgeKill.style.display = "none"; }
          if (!hintTimer && badge) badge.style.display = "none";
          if (!paused && pausedPill) pausedPill.style.display = "none";
          if (reloadPill) reloadPill.style.display = "none";   // the hold is session-scoped — no session, no pill
        } catch (e) {}
      },
      // The human's own Pause click: end the session right now and swap the badge for the paused pill —
      // distinct from hide() (which the server can also drive via stop_interaction) so a reload or a
      // stray overlay(show:false) doesn't quietly clear a pause the human hasn't lifted yet.
      showPaused: function () {
        try {
          ensure(); paused = true; shown = false;
          frame.style.display = "none"; cursor.style.display = "none"; badgePause.style.display = badgeKill.style.display = "none";
          badge.style.display = "none"; pausedPill.style.display = "flex";
          reloadPill.style.display = "none";   // pausing hands live-reload back (see reloadHold)
        } catch (e) {}
      },
      clearPaused: function () { try { paused = false; if (pausedPill) pausedPill.style.display = "none"; } catch (e) {} },
      isPaused: function () { return paused; },
      // The human's own (harder) Stop click, from either the badge or the paused pill: end everything
      // right now and show NO ui at all — no pill, nothing. Unlike pause there is no page-side control
      // that clears this; only the agent's own fresh start_interaction does (see EvalChannel), which the
      // human triggers by telling the agent to continue in chat, not by clicking anything here.
      showKilled: function () {
        try {
          killed = true; shown = false; paused = false;
          if (host) { frame.style.display = "none"; cursor.style.display = "none"; badgePause.style.display = badgeKill.style.display = "none"; badge.style.display = "none"; }
          if (pausedPill) pausedPill.style.display = "none";
          if (reloadPill) reloadPill.style.display = "none";
        } catch (e) {}
      },
      clearKilled: function () { killed = false; },
      isKilled: function () { return killed; },
      shown: function () { return shown; },
      cursorTo: function (x, y) { try { if (shown) put(x, y, CURSOR_STEP_MS); } catch (e) {} },
      // Glide to (x,y) and resolve only once the cursor has visually ARRIVED, so the caller can act at
      // the moment the human sees it land. Without this the effect races the animation — a popup would
      // open while the cursor was still travelling, making the overlay lie about when things happened.
      // Resolves immediately when there's no session to watch, or the cursor is already on target.
      cursorGlide: function (x, y) {
        try {
          if (!shown) return Promise.resolve();
          var ms = glideMsTo(x, y);
          put(x, y, ms);
          if (ms <= 0) return Promise.resolve();
          // Wait for the real transitionend, not a same-length timer: the timer starts counting NOW but
          // the transition only starts at the next style flush, so a timer always fires ~a frame early
          // and the click would land while the cursor was still visibly short of the target. The timeout
          // is only a fallback for a transition that never starts or gets interrupted.
          return new Promise(function (r) {
            var done = false;
            function finish() {
              if (done) return;
              done = true;
              try { cursor.removeEventListener("transitionend", onEnd); } catch (e) {}
              r();
            }
            function onEnd(e) { if (!e || e.propertyName === "transform") finish(); }
            try { cursor.addEventListener("transitionend", onEnd); } catch (e) {}
            setTimeout(finish, ms + 150);
          });
        } catch (e) { return Promise.resolve(); }
      },
      rippleAt: function (x, y) { try { if (shown) ripple(x, y); } catch (e) {} },
      // Short, center-screen label for a manipulate action ("insert text: xxx", "[ENTER]", "navigate to: xxx").
      centerLabel: function (text) { try { if (shown) clabel(text); } catch (e) {} },
      // Read-only hint ("reading: .selector") — shares the one badge above instead of a second pill.
      hint: function (text) { try { setHint(text); } catch (e) {} },
      // The held-reload pill, stacked under the badge — shown once the hold has actually swallowed a
      // dev-server reload, so it reads as "your change is waiting", not as idle chrome on every session.
      showReloadHeld: function () { try { ensure(); reloadPill.style.display = "flex"; } catch (e) {} },
      hideReloadHeld: function () { try { if (reloadPill) reloadPill.style.display = "none"; } catch (e) {} }
    };
  })();

  function short(text, max) {
    var s = String(text || "");
    return s.length > max ? s.slice(0, max) + "…" : s;
  }

  // Reconcile the overlay with the server's interaction flag (idempotent) — restores it after a reload.
  function reconcileOverlay(active) {
    try {
      if (active && !overlay.shown()) overlay.show();
      else if (!active && overlay.shown()) overlay.hide();
    } catch (e) {}
  }

  // Reconcile the paused pill with the server's Paused flag (idempotent) — restores it after a
  // reload, so a page refresh mid-pause doesn't silently drop back into "no session" and let the agent
  // in unnoticed.
  function reconcilePaused(paused) {
    try {
      if (paused && !overlay.isPaused()) overlay.showPaused();
      else if (!paused && overlay.isPaused()) overlay.clearPaused();
    } catch (e) {}
  }

  // Reconcile the (invisible) killed flag with the server's Killed state (idempotent) — there's no pill
  // to restore, but a reload right after a kill should still keep the overlay hidden and the doBatch
  // abort check armed until the agent's own start_interaction clears it server-side.
  function reconcileKilled(killed) {
    try {
      if (killed && !overlay.isKilled()) overlay.showKilled();
      else if (!killed && overlay.isKilled()) overlay.clearKilled();
    } catch (e) {}
  }

  /*
   * Angular reload hold — keep the dev server from yanking the page out from under the agent mid-test.
   *
   * While a session is open (start_interaction..stop_interaction) the Angular dev server would still
   * rebuild on every save and push a reload/HMR update to the page, destroying whatever the agent was
   * driving. This intercepts that push at the page end: the build still runs and ky-ai-ng still reports
   * its verdict — only the page's REACTION is deferred.
   *
   * How it hooks in: Angular's dev server (vite) delivers reloads over a WebSocket it tags with the
   * "vite-hmr" subprotocol, and its client attaches via addEventListener. This snippet is a CLASSIC
   * script and /@vite/client is a deferred MODULE, so we always run first — early enough to wrap the
   * WebSocket constructor and register our own message listener BEFORE vite's. Being first is what lets
   * stopImmediatePropagation() drop a message before vite's own handler ever sees it. Vite's separate
   * "vite-ping" socket is left alone.
   *
   * Releasing is deliberately asymmetric, because "who is looking at the page right now" differs:
   *   - the agent finished (stop_interaction)  ⇒ force one catch-up reload, so the page resyncs to the
   *     code on disk. Necessary, not cosmetic: swallowed `update` messages leave vite's client module
   *     graph behind the server's, and a hard reload is the only reliable way back.
   *   - the human clicked the pill, or hit Pause/Stop ⇒ NO reload. They're mid-look at this page; a
   *     reload would destroy the very state they wanted. Live-reload simply resumes from here, exactly
   *     as if it had never been held (their next save reloads normally).
   */
  var reloadHold = (function () {
    var HOLD_TYPES = { "update": 1, "full-reload": 1, "prune": 1 };
    var holding = false;        // server says: session open, hold not released
    var held = false;           // we actually swallowed something (⇒ the page is now behind the code)
    var releasedLocally = false; // the human clicked "continue" — sticky until the server agrees

    function isViteHmr(protocols) {
      if (protocols === "vite-hmr") return true;
      try { return !!protocols && typeof protocols.indexOf === "function" && protocols.indexOf("vite-hmr") >= 0; }
      catch (e) { return false; }
    }
    function msgType(data) {
      try { return typeof data === "string" ? (JSON.parse(data) || {}).type : null; } catch (e) { return null; }
    }
    // Registered from inside the WebSocket constructor, so it always precedes vite's own listener.
    function attach(ws) {
      try {
        ws.addEventListener("message", function (e) {
          try {
            if (!holding) return;                       // not holding → vite handles it as normal
            if (!HOLD_TYPES[msgType(e.data)]) return;   // connected/ping/error/custom → never our business
            held = true;
            overlay.showReloadHeld();
            e.stopImmediatePropagation();               // we're first ⇒ vite's handler never runs
          } catch (e2) { /* never break the app's HMR on our account */ }
        });
      } catch (e) {}
    }
    return {
      // Wrap the WebSocket constructor via a Proxy (keeps statics/prototype/instanceof intact, unlike a
      // plain function wrapper). Runs synchronously at snippet parse — i.e. before vite's client exists.
      install: function () {
        try {
          var Native = window.WebSocket;
          if (!Native) return;
          window.WebSocket = new Proxy(Native, {
            construct: function (Target, args) {
              var ws = new Target(args[0], args[1]);
              if (isViteHmr(args[1])) attach(ws);
              return ws;
            }
          });
        } catch (e) { /* no Proxy / locked-down env → holding just never engages */ }
      },
      // Drive the hold off the poll's holdReload flag. paused/killed mean a human is looking at the page,
      // so a release must not reload it (see the asymmetry note above).
      reconcile: function (serverHold, paused, killed) {
        try {
          if (serverHold) {
            if (releasedLocally) return;            // our release is in flight — don't re-arm behind it
            if (!holding) { holding = true; held = false; }
            if (held) overlay.showReloadHeld();     // survives a reload mid-session
          } else if (holding || releasedLocally) {
            var catchUp = held && !releasedLocally && !paused && !killed;
            holding = false; held = false; releasedLocally = false;
            overlay.hideReloadHeld();
            if (catchUp) setTimeout(function () { try { location.reload(); } catch (e) {} }, 0);
          }
        } catch (e) {}
      },
      // The pill's click: hand live-reload back for this session, leaving the page exactly as it is.
      release: function () {
        if (!holding) return;
        releasedLocally = true; holding = false; held = false;
        overlay.hideReloadHeld();
      },
      isHolding: function () { return holding; }
    };
  })();
  reloadHold.install();

  function onUserContinueReload() { reloadHold.release(); postInteractionOverride(RELOAD_RELEASE); }

  // Center-screen label for a key press, e.g. "[ENTER]", "[CTRL+S]", "[ESC]".
  function keyCenterLabel(req) {
    var mods = (req.ctrl ? "CTRL+" : "") + (req.alt ? "ALT+" : "") + (req.shift ? "SHIFT+" : "") + (req.meta ? "META+" : "");
    var key = req.key === " " ? "SPACE" : String(req.key || "").toUpperCase();
    return "[" + mods + key + "]";
  }

  // How long the click's own animation (the ripple at the cursor) plays BEFORE the click is dispatched.
  // Without this beat the ripple and the page's reaction start on the same tick, so a click that
  // navigates swaps the page out from under its own ripple — the human sees the aftermath but never sees
  // the click land, which is the whole point of the overlay. Long enough for the first ring to expand and
  // the second (90ms-delayed) one to appear, without making every click feel sluggish.
  var CLICK_FX_MS = 150;

  // returns a Promise<payload>. Ordering is the point: the cursor glides to the target, the ripple plays
  // at it, and only THEN is the click dispatched — so a watching human sees pointer → click → reaction,
  // in that order. It used to dispatch on the same tick as both, so the page reacted mid-glide.
  // Nothing is dispatched during the glide/ripple (they're purely visual), so the page can't shift under
  // the already-resolved target while we wait.
  function doClick(req) {
    try {
      var t = resolveTarget(req);
      if (t.badArgs) return { ok: false, error: "click requires selector, text, or x,y" };
      if (!t.el) return { ok: false, error: "no element matches " + (req.selector ? "selector" : req.text ? "text" : "point"), selector: req.selector, text: req.text };
      var el = t.el, x = t.x, y = t.y;
      return overlay.cursorGlide(x, y).then(function () {
        overlay.rippleAt(x, y);   // ripple at the click point already shows this — no need for a center label too
        return delay(overlay.shown() ? CLICK_FX_MS : 0);   // no session ⇒ nobody watching ⇒ don't stall
      }).then(function () {
        try {
          var btn = req.button === "right" ? 2 : req.button === "middle" ? 1 : 0;
          var opts = { button: btn, buttons: 1, ctrl: req.ctrl, shift: req.shift, alt: req.alt, meta: req.meta };
          fire(el, "pointerover", x, y, opts); fire(el, "pointerenter", x, y, { bubbles: false });
          fire(el, "mouseover", x, y, opts); fire(el, "mouseenter", x, y, { bubbles: false });
          fire(el, "pointermove", x, y, opts); fire(el, "mousemove", x, y, opts);
          fire(el, "pointerdown", x, y, opts); fire(el, "mousedown", x, y, opts);
          try { if (el.focus) el.focus(); } catch (e) {}
          fire(el, "pointerup", x, y, opts); fire(el, "mouseup", x, y, opts);
          if (btn === 2) { fire(el, "contextmenu", x, y, opts); }
          else if (typeof el.click === "function") { try { el.click(); } catch (e) { fire(el, "click", x, y, opts); } } // click() runs default actions
          else { fire(el, "click", x, y, opts); }
          return { ok: true, action: "click", button: req.button || "left", point: { x: x, y: y }, target: describeEl(el, req.detail) };
        } catch (e) { return errPayload(e); }
      });
    } catch (e) { return errPayload(e); }
  }

  // returns a Promise<payload> — the move animates over its duration
  function doMove(req) {
    return new Promise(function (resolve) {
      try {
        if (typeof req.toX !== "number" || typeof req.toY !== "number") { resolve({ ok: false, error: "move requires toX and toY" }); return; }
        var toX = req.toX, toY = req.toY;
        var fromX = typeof req.fromX === "number" ? req.fromX : toX;
        var fromY = typeof req.fromY === "number" ? req.fromY : toY;
        var dur = typeof req.durationMs === "number" ? req.durationMs : 300;
        var steps = req.steps || Math.max(1, Math.min(60, Math.round(dur / 16)));
        var interval = steps > 1 ? dur / (steps - 1) : 0;
        var prev = null, traversed = 0, i = 0;
        function step() {
          var p = steps === 1 ? 1 : i / (steps - 1);
          var x = Math.round(fromX + (toX - fromX) * p);
          var y = Math.round(fromY + (toY - fromY) * p);
          var el = elementFromPointDeep(x, y);
          if (el !== prev) {
            if (prev) { fire(prev, "pointerout", x, y, {}); fire(prev, "pointerleave", x, y, { bubbles: false }); fire(prev, "mouseout", x, y, {}); fire(prev, "mouseleave", x, y, { bubbles: false }); }
            if (el) { fire(el, "pointerover", x, y, {}); fire(el, "pointerenter", x, y, { bubbles: false }); fire(el, "mouseover", x, y, {}); fire(el, "mouseenter", x, y, { bubbles: false }); traversed++; }
            prev = el;
          }
          if (el) { fire(el, "pointermove", x, y, {}); fire(el, "mousemove", x, y, {}); }
          overlay.cursorTo(x, y);
          i++;
          if (i < steps) { setTimeout(step, interval); }
          else { resolve({ ok: true, action: "move", from: { x: fromX, y: fromY }, to: { x: toX, y: toY }, steps: steps, durationMs: dur, traversed: traversed, finalTarget: prev ? describeEl(prev, req.detail) : null }); }
        }
        step();
      } catch (e) { resolve(errPayload(e)); }
    });
  }

  function doKey(req) {
    try {
      if (!req.key) return { ok: false, error: "key is required" };
      var el = req.selector ? document.querySelector(req.selector) : (document.activeElement || document.body);
      if (!el) return { ok: false, error: req.selector ? "no element matches selector" : "no focused element", selector: req.selector };
      try { if (req.selector && el.focus) el.focus(); } catch (e) {}
      var init = { bubbles: true, cancelable: true, composed: true, key: req.key, code: req.code || "", ctrlKey: !!req.ctrl, shiftKey: !!req.shift, altKey: !!req.alt, metaKey: !!req.meta };
      function kev(type) { try { return new KeyboardEvent(type, init); } catch (e) { return null; } }
      overlay.centerLabel(keyCenterLabel(req));
      var d = kev("keydown"); if (d) el.dispatchEvent(d);
      if (req.key.length === 1) { var pr = kev("keypress"); if (pr) el.dispatchEvent(pr); } // printable only
      var u = kev("keyup"); if (u) el.dispatchEvent(u);
      return { ok: true, action: "key", key: req.key, target: describeEl(el, req.detail) };
    } catch (e) { return errPayload(e); }
  }

  // set a form field's value through the native setter so framework value-trackers observe the change
  function setNativeValue(el, value) {
    try {
      var desc = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), "value");
      if (desc && desc.set) { desc.set.call(el, value); return; }
    } catch (e) {}
    try { el.value = value; } catch (e2) {}
  }

  function doType(req) {
    try {
      if (!req.selector) return { ok: false, error: "type requires a selector" };
      var el = document.querySelector(req.selector);
      if (!el) return { ok: false, error: "no element matches selector", selector: req.selector };
      try { if (el.focus) el.focus(); } catch (e) {}
      var text = req.text == null ? "" : String(req.text);
      overlay.centerLabel(text ? "insert text: " + (text.length > 40 ? text.slice(0, 40) + "…" : text) : "clear text");
      var tag = (el.tagName || "").toUpperCase();
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") {
        setNativeValue(el, req.append ? ((el.value || "") + text) : text);
        try { el.dispatchEvent(new Event("input", { bubbles: true, composed: true })); } catch (e) {}
        try { el.dispatchEvent(new Event("change", { bubbles: true, composed: true })); } catch (e) {}
        return { ok: true, action: "type", value: el.value, target: describeEl(el, req.detail) };
      } else if (el.isContentEditable) {
        if (!req.append) el.textContent = "";
        el.textContent = (el.textContent || "") + text;
        try { var ie = (typeof InputEvent === "function") ? new InputEvent("input", { bubbles: true, composed: true, data: text, inputType: "insertText" }) : new Event("input", { bubbles: true }); el.dispatchEvent(ie); } catch (e) {}
        return { ok: true, action: "type", value: el.textContent, target: describeEl(el, req.detail) };
      }
      return { ok: false, error: "target is not a form field or contenteditable", target: describeEl(el, req.detail) };
    } catch (e) { return errPayload(e); }
  }

  // returns a Promise<payload> — polls until ready or the deadline
  function doWait(req) {
    return new Promise(function (resolve) {
      try {
        overlay.hint("waiting for: " + short(req.selector || req.expression, 60));
        var deadline = Date.now() + (req.timeoutMs || 5000);
        var pollMs = Math.max(20, req.pollMs || 100);
        function check() {
          var matched = false, detail = null;
          try {
            if (req.selector) { var el = document.querySelector(req.selector); if (el) { matched = true; detail = describeEl(el, req.detail); } }
            else if (req.expression) { var v = (0, eval)(req.expression); if (v) { matched = true; detail = clamp(serializeArg(v)); } }
            else { resolve({ ok: false, error: "wait requires selector or expression" }); return; }
          } catch (e) { /* expression not ready yet — keep polling */ }
          if (matched) { resolve({ ok: true, action: "wait", matched: true, detail: detail }); return; }
          if (Date.now() >= deadline) { resolve({ ok: false, action: "wait", matched: false, timedOut: true, waitedMs: req.timeoutMs || 5000 }); return; }
          setTimeout(check, pollMs);
        }
        check();
      } catch (e) { resolve(errPayload(e)); }
    });
  }

  function doScroll(req) {
    try {
      overlay.centerLabel("scroll");
      if (req.selector) {
        var el = document.querySelector(req.selector);
        if (!el) return { ok: false, error: "no element matches selector", selector: req.selector };
        if (typeof req.x === "number" || typeof req.y === "number") { try { el.scrollTo({ left: req.x || 0, top: req.y || 0 }); } catch (e) { el.scrollLeft = req.x || 0; el.scrollTop = req.y || 0; } }
        else { try { el.scrollIntoView({ block: "center", inline: "center" }); } catch (e) { el.scrollIntoView(); } }
        return { ok: true, action: "scroll", target: describeEl(el, req.detail) };
      }
      try { window.scrollTo({ left: req.x || 0, top: req.y || 0 }); } catch (e) { window.scrollTo(req.x || 0, req.y || 0); }
      return { ok: true, action: "scroll", scrollX: window.scrollX, scrollY: window.scrollY };
    } catch (e) { return errPayload(e); }
  }

  function doFocus(req) {
    try {
      if (req.blur) {
        var b = req.selector ? document.querySelector(req.selector) : document.activeElement;
        overlay.centerLabel("blur");
        try { if (b && b.blur) b.blur(); } catch (e) {}
        return { ok: true, action: "blur", target: b ? describeEl(b, req.detail) : null };
      }
      var el = document.querySelector(req.selector);
      if (!el) return { ok: false, error: "no element matches selector", selector: req.selector };
      overlay.centerLabel("focus");
      // Same ordering rule as doClick: land the cursor before the focus takes effect, so what the human
      // sees matches what actually happened. Returns a Promise<payload>; postDo/doBatch both handle it.
      var p = null;
      try { var r = el.getBoundingClientRect(); p = { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) }; } catch (e) {}
      return (p ? overlay.cursorGlide(p.x, p.y) : Promise.resolve()).then(function () {
        try { if (el.focus) el.focus(); } catch (e) {}
        return { ok: true, action: "focus", focused: document.activeElement === el, target: describeEl(el, req.detail) };
      });
    } catch (e) { return errPayload(e); }
  }

  var DEFAULT_STYLE_PROPS = ["display", "visibility", "opacity", "color", "background-color", "width", "height", "position", "transform", "stroke", "fill", "cursor", "pointer-events", "z-index"];
  function doStyles(req) {
    try {
      if (!req.selector) return { ok: false, error: "styles requires a selector" };
      overlay.hint("reading styles: " + short(req.selector, 60));
      var el = document.querySelector(req.selector);
      if (!el) return { ok: false, error: "no element matches selector", selector: req.selector };
      var cs = window.getComputedStyle(el);
      var props = (req.props && req.props.length) ? req.props : DEFAULT_STYLE_PROPS;
      var styles = {};
      for (var i = 0; i < props.length; i++) { try { styles[props[i]] = cs.getPropertyValue(props[i]); } catch (e) { styles[props[i]] = ""; } }
      return { ok: true, action: "styles", styles: styles, target: describeEl(el, req.detail) };
    } catch (e) { return errPayload(e); }
  }

  function doOverlay(req) {
    if (req.show) overlay.show(); else overlay.hide();
    return { ok: true, action: "overlay", shown: overlay.shown() };
  }

  function assign(a, b) { if (b) for (var k in b) if (Object.prototype.hasOwnProperty.call(b, k)) a[k] = b[k]; return a; }

  // map one batch step (action + fields) to its handler; returns payload or Promise<payload>
  // sleep: pace a batch — nothing but a delay before the next step. Deliberately silent in the overlay:
  // it touches nothing, so a hint/label would be describing an action that isn't happening; the session
  // badge already says the agent is driving. Clamped so a bad durationMs can't park a batch indefinitely
  // (the tool's own budget accounts for the same numbers). Returns a Promise<payload>.
  function doSleep(req) {
    var ms = typeof req.durationMs === "number" ? req.durationMs : 500;
    ms = Math.max(0, Math.min(30000, ms));
    return delay(ms).then(function () { return { ok: true, action: "sleep", durationMs: ms }; });
  }

  function runStep(step) {
    switch (step.action) {
      case "click": return doClick(step);
      case "move": return doMove(step);
      case "key": return doKey(step);
      case "type": return doType(step);
      case "wait": return doWait(step);
      case "scroll": return doScroll(step);
      case "focus": return doFocus(step);
      case "styles": case "get_styles": return doStyles(step);
      case "query": return doQuery(step);
      case "component": return readComponent(step.selector, step);
      case "eval": return doEval(step);
      case "sleep": return doSleep(step);
      default: return { ok: false, error: "unknown batch action: " + step.action };
    }
  }

  // Run a sequence of steps in this one page visit, in order, stopping at the first failure — one
  // round-trip for the whole flow instead of one per action. Returns a Promise<payload>.
  function doBatch(req) {
    var steps = req.actions || [];
    var results = [];
    return new Promise(function (resolve) {
      var i = 0;
      function next() {
        // The human hit Pause or Stop mid-batch — don't run the remaining steps.
        if (overlay.isKilled()) {
          resolve({ ok: false, action: "batch", killed: true, failedAt: i, count: results.length, results: results });
          return;
        }
        if (overlay.isPaused()) {
          resolve({ ok: false, action: "batch", paused: true, failedAt: i, count: results.length, results: results });
          return;
        }
        if (i >= steps.length) { resolve({ ok: true, action: "batch", count: results.length, results: results }); return; }
        var step = steps[i];
        var p;
        try { p = Promise.resolve(runStep(step)); } catch (e) { p = Promise.resolve(errPayload(e)); }
        p.then(function (payload) {
          results.push(assign({ step: i, action: step.action }, payload));
          var failed = payload && payload.ok === false;
          i++;
          if (failed) { resolve({ ok: false, action: "batch", failedAt: i - 1, count: results.length, results: results }); return; }
          next();
        }, function (e) {
          results.push(assign({ step: i, action: step.action }, errPayload(e)));
          resolve({ ok: false, action: "batch", failedAt: i, count: results.length, results: results });
        });
      }
      next();
    });
  }

  // Resolve a payload-or-promise and post it as the request's result.
  function postDo(req, valueOrPromise) {
    Promise.resolve(valueOrPromise).then(function (p) { postResult(req.id, p); }, function (e) { postResult(req.id, errPayload(e)); });
  }

  // Best-effort locate the Angular Router on a dev build (window.ng). Walks the components on the
  // usual routing anchors and duck-types each instance's own properties for the Router (TS-private
  // fields are still enumerable own props): a value exposing navigateByUrl + createUrlTree. Returns
  // the Router instance or null (production build / not found → caller falls back to the History API).
  function findRouter() {
    var ng = window.ng;
    if (!ng || typeof ng.getComponent !== "function") return null;
    var els = document.querySelectorAll("router-outlet, app-root, [ng-version]");
    for (var i = 0; i < els.length; i++) {
      var cmp = null;
      try { cmp = ng.getComponent(els[i]); } catch (e) {}
      if (!cmp) continue;
      var keys;
      try { keys = Object.keys(cmp); } catch (e) { continue; }
      for (var j = 0; j < keys.length; j++) {
        var v;
        try { v = cmp[keys[j]]; } catch (e) { continue; }
        if (v && typeof v.navigateByUrl === "function" && typeof v.createUrlTree === "function") return v;
      }
    }
    return null;
  }

  // navigate: change the SPA route without a full reload. Prefer the real Router (router.navigateByUrl,
  // strategy-agnostic); fall back to the History API (pushState/replaceState + a synthetic popstate the
  // default PathLocationStrategy picks up, or the hash when the app is already on a hash route). Returns
  // a Promise<payload> with the settled location so the agent can confirm the destination (even a guard
  // redirect). Reports which `method` drove it.
  function doNavigate(req) {
    var path = req && req.path;
    var from = location.href;
    if (!path) return { ok: false, error: "path is required" };
    overlay.centerLabel("navigate to: " + path);

    var router = null;
    try { router = findRouter(); } catch (e) { router = null; }
    if (router) {
      return Promise.resolve()
        .then(function () { return router.navigateByUrl(path); })
        .then(
          function (navigated) { return { ok: true, from: from, to: location.href, navigated: navigated !== false, method: "router" }; },
          function (e) { return { ok: false, from: from, to: location.href, method: "router", error: String((e && e.message) || e) }; }
        );
    }

    // Fallback: History API. Use the hash when the app is already on a hash route (hashchange fires on
    // its own); otherwise pushState/replaceState and dispatch popstate for the router to react to.
    try {
      var hashMode = /^#\/?/.test(location.hash || "");
      if (hashMode) {
        location.hash = path.charAt(0) === "#" ? path : "#" + (path.charAt(0) === "/" ? path : "/" + path);
      } else {
        history[req.replace ? "replaceState" : "pushState"](null, "", path);
        window.dispatchEvent(new PopStateEvent("popstate", { state: history.state }));
      }
    } catch (e) {
      return { ok: false, from: from, to: location.href, method: "history", error: String((e && e.message) || e) };
    }
    return new Promise(function (resolve) {
      setTimeout(function () {
        resolve({ ok: true, from: from, to: location.href, navigated: location.href !== from, method: "history" });
      }, 150);
    });
  }

  function dispatchEval(req) {
    try {
      if (!req || !req.kind) return;
      switch (req.kind) {
        case "reload": setTimeout(function () { try { location.reload(); } catch (e) {} }, 0); return;
        case "navigate": postDo(req, doNavigate(req)); return;
        case "overlay": postResult(req.id, doOverlay(req)); return;
        case "batch": postDo(req, doBatch(req)); return;
        case "query": postDo(req, doQuery(req)); return;
        case "click": postDo(req, doClick(req)); return;
        case "move": postDo(req, doMove(req)); return;
        case "key": postDo(req, doKey(req)); return;
        case "type": postDo(req, doType(req)); return;
        case "wait": postDo(req, doWait(req)); return;
        case "scroll": postDo(req, doScroll(req)); return;
        case "focus": postDo(req, doFocus(req)); return;
        case "styles": postDo(req, doStyles(req)); return;
        case "component": postDo(req, readComponent(req.selector, req)); return;
        default: postDo(req, doEval(req)); return;   // "eval"
      }
    } catch (e) {
      try { if (req && req.id) postResult(req.id, errPayload(e)); } catch (e2) {}
    }
  }

  var lastPollOkAt = Date.now();
  function pollEvalOnce() {
    fetch(EVAL_POLL + "?token=" + encodeURIComponent(TOKEN) + "&pageLoadId=" + encodeURIComponent(pageLoadId), {
      method: "GET",
      credentials: "omit",
      cache: "no-store"
    })
      .then(function (resp) { return resp.json(); })
      .then(function (data) {
        lastPollOkAt = Date.now();
        reconcileOverlay(data && data.interactionActive);  // restore/clear the overlay (e.g. after a reload)
        reconcilePaused(data && data.paused);                // restore/clear the paused pill likewise
        reconcileKilled(data && data.killed);                // restore/clear the killed pill likewise
        // Engage/release the dev-server reload hold. Ordered after the three above so a release that
        // force-reloads sees the overlay state already settled.
        reloadHold.reconcile(data && data.holdReload, data && data.paused, data && data.killed);
        var reqs = (data && data.requests) || [];
        for (var i = 0; i < reqs.length; i++) dispatchEval(reqs[i]);
        setTimeout(pollEvalOnce, 0);     // immediately re-open the long-poll
      })["catch"](function () {
        // ky-ai-browser unreachable for a while → it (or the agent) is gone; don't strand the overlay.
        if (Date.now() - lastPollOkAt > 8000) overlay.hide();
        setTimeout(pollEvalOnce, 2000);  // back off, keep trying
      });
  }

  pollEvalOnce();
})();
