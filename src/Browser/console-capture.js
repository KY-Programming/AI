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
    ".kyai-frame{position:absolute;inset:0;box-sizing:border-box;border:3px solid rgba(229,57,53,.95);}" +
    ".kyai-badge{position:absolute;top:8px;left:50%;transform:translateX(-50%);font:600 11px/1.4 system-ui,sans-serif;color:#fff;background:rgba(229,57,53,.95);padding:3px 10px;border-radius:10px;letter-spacing:.3px;white-space:nowrap;}" +
    ".kyai-cursor{position:absolute;left:0;top:0;width:24px;height:24px;will-change:transform;filter:drop-shadow(0 1px 2px rgba(0,0,0,.5));}" +
    ".kyai-cursor svg{display:block;}" +
    ".kyai-ripple{position:absolute;width:10px;height:10px;margin:-5px 0 0 -5px;border:2px solid rgba(229,57,53,.9);border-radius:50%;animation:kyai-rip .6s ease-out forwards;}" +
    "@keyframes kyai-rip{from{transform:scale(.3);opacity:.9}to{transform:scale(4.5);opacity:0}}" +
    ".kyai-key{position:absolute;transform:translate(14px,-6px);font:600 12px/1 ui-monospace,monospace;color:#222;background:#fafafa;border:1px solid #bbb;border-bottom-width:3px;border-radius:6px;padding:5px 8px;box-shadow:0 2px 4px rgba(0,0,0,.3);white-space:nowrap;animation:kyai-key .8s ease-out forwards;}" +
    "@keyframes kyai-key{0%{opacity:0;transform:translate(14px,2px)}15%{opacity:1;transform:translate(14px,-6px)}80%{opacity:1;transform:translate(14px,-10px)}100%{opacity:0;transform:translate(14px,-18px)}}";
  var CURSOR_SVG = '<svg width="24" height="24" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">' +
    '<path d="M4 2l6 14 2.2-5.6L18 8z" fill="#fff" stroke="#e53935" stroke-width="1.6" stroke-linejoin="round"/></svg>';

  var overlay = (function () {
    var host = null, root = null, cursor = null, shown = false, cx = 0, cy = 0;
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
      var frame = document.createElement("div"); frame.className = "kyai-frame"; root.appendChild(frame);
      var badge = document.createElement("div"); badge.className = "kyai-badge"; badge.textContent = "● ky-ai agent interacting"; root.appendChild(badge);
      cursor = document.createElement("div"); cursor.className = "kyai-cursor"; cursor.innerHTML = CURSOR_SVG; root.appendChild(cursor);
      put(Math.round(vw() / 2), Math.round(vh() / 2), false);
      (document.body || document.documentElement).appendChild(host);
    }
    function put(x, y, animate) {
      cx = x; cy = y;
      if (!cursor) return;
      cursor.style.transition = animate ? "transform .12s linear" : "none";
      cursor.style.transform = "translate(" + x + "px," + y + "px)";
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
    function keycap(label) {
      if (!root) return;
      var k = document.createElement("div");
      k.className = "kyai-key"; k.textContent = label; k.style.left = cx + "px"; k.style.top = cy + "px";
      root.appendChild(k);
      setTimeout(function () { try { k.remove(); } catch (e) {} }, 850);
    }
    return {
      show: function () { try { ensure(); host.style.display = "block"; shown = true; } catch (e) {} },
      hide: function () { try { if (host) host.style.display = "none"; shown = false; } catch (e) {} },
      shown: function () { return shown; },
      cursorTo: function (x, y) { try { if (shown) put(x, y, true); } catch (e) {} },
      clickFx: function (x, y) { try { if (shown) { put(x, y, true); ripple(x, y); } } catch (e) {} },
      keyFx: function (label) { try { if (shown) keycap(label); } catch (e) {} }
    };
  })();

  // Reconcile the overlay with the server's interaction flag (idempotent) — restores it after a reload.
  function reconcileOverlay(active) {
    try {
      if (active && !overlay.shown()) overlay.show();
      else if (!active && overlay.shown()) overlay.hide();
    } catch (e) {}
  }

  // Compact label for a key press, e.g. "Ctrl+S", "⏎", "Esc".
  function keyLabel(req) {
    var mods = (req.ctrl ? "Ctrl+" : "") + (req.alt ? "Alt+" : "") + (req.shift ? "Shift+" : "") + (req.meta ? "Meta+" : "");
    var key = req.key === "Enter" ? "⏎" : req.key === "Escape" ? "Esc" : req.key === " " ? "Space" : req.key;
    return mods + key;
  }

  function doClick(req) {
    try {
      var t = resolveTarget(req);
      if (t.badArgs) return { ok: false, error: "click requires selector, text, or x,y" };
      if (!t.el) return { ok: false, error: "no element matches " + (req.selector ? "selector" : req.text ? "text" : "point"), selector: req.selector, text: req.text };
      var el = t.el, x = t.x, y = t.y;
      overlay.clickFx(x, y);
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
      overlay.keyFx(keyLabel(req));
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
      overlay.keyFx(text.length > 12 ? text.slice(0, 12) + "…" : (text || "⌫"));
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
        try { if (b && b.blur) b.blur(); } catch (e) {}
        return { ok: true, action: "blur", target: b ? describeEl(b, req.detail) : null };
      }
      var el = document.querySelector(req.selector);
      if (!el) return { ok: false, error: "no element matches selector", selector: req.selector };
      try { var r = el.getBoundingClientRect(); overlay.cursorTo(Math.round(r.left + r.width / 2), Math.round(r.top + r.height / 2)); } catch (e) {}
      try { if (el.focus) el.focus(); } catch (e) {}
      return { ok: true, action: "focus", focused: document.activeElement === el, target: describeEl(el, req.detail) };
    } catch (e) { return errPayload(e); }
  }

  var DEFAULT_STYLE_PROPS = ["display", "visibility", "opacity", "color", "background-color", "width", "height", "position", "transform", "stroke", "fill", "cursor", "pointer-events", "z-index"];
  function doStyles(req) {
    try {
      if (!req.selector) return { ok: false, error: "styles requires a selector" };
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

  function dispatchEval(req) {
    try {
      if (!req || !req.kind) return;
      switch (req.kind) {
        case "reload": setTimeout(function () { try { location.reload(); } catch (e) {} }, 0); return;
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
