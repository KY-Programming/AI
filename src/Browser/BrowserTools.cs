using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using KY.AI.Serve;
using ModelContextProtocol.Server;

namespace KY.AI.Browser;

// MCP tools ky-ai-browser exposes (allow-list as mcp__ky-ai-browser__<name>). These run in the HUB
// process: each (except list/shutdown) takes an optional `project` and is forwarded to the matching
// capture instance's loopback control API — console_tail/console_clear hit /console/*, every page
// action is packaged as an EvalRequest and POSTed to /eval. `project` may be omitted when exactly one
// capture is registered (the common case: one ky-ai-browser next to one ky-ai-ng serve). The instance
// holds the real state (the console buffer + the page eval channel) and enforces interaction gating, so
// "capture not running" surfaces as "no captures registered" from the hub, and "no page" / "needs
// interaction" come back from the instance unchanged.
//
// The interaction tools return a MINIMAL target ({tag, id?, text}) by default — enough to confirm the
// right element was hit; pass detail:true for the full element (classes, attributes, rect, outerHTML).
//
// Interaction events are SYNTHETIC (isTrusted:false): they fire JS handlers but do not drive CSS
// :hover or user-activation-gated APIs (window.open, clipboard, fullscreen). Good for poking your
// own component logic; a JS-state hover reproduces, a pure CSS :hover does not.
[McpServerToolType]
internal static class BrowserTools
{
    // EvalRequest goes over the wire to the instance camelCase with null fields dropped — the same
    // shape the instance then hands the page snippet, so an omitted coordinate stays omitted (0 is a
    // valid position). The Id is assigned by the instance's channel; "" is a placeholder.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ── hub-level tools (no project) ──

    [McpServerTool(Name = "list"), Description(
        "List the capture instances currently registered with the hub — each ky-ai-browser is attached " +
        "to one ky-ai-ng frontend and registered under that frontend's name. Call this first to learn " +
        "the project names the other tools expect; each entry carries the instance's status (attached " +
        "frontend, whether a page is connected, whether supervised interaction is open, buffered events).")]
    public static Task<string> List() => Hub.ListAsync(detail: true);

    [McpServerTool(Name = "console_tail"), Description(
        "Return recent BROWSER/runtime console events from the app ky-ai-browser is attached to " +
        "(console.log/info/warn/error, uncaught exceptions, unhandled promise rejections). Each event is " +
        "{seq, level, args, text, source, line, col, stack, timestamp, pageLoadId}; the response also " +
        "carries `dropped` (events lost to a flood), `enabled` (false when ky-ai-browser isn't running " +
        "— start it next to your `ky-ai-ng serve`) and `currentPageLoadId` (the live page load, so you can " +
        "tell fresh from stale without a second call). Filters compose: level keeps that severity and above " +
        "(debug<log<info<warn<error<exception); sinceSeq returns events after a prior tail's max seq; grep " +
        "is a case-insensitive substring over text+stack; pageLoad isolates one page load (reload boundary); " +
        "currentPageOnly=true scopes to the current/most-recent page load — the one-call 'did my reload clear " +
        "it?' check (an explicit pageLoad wins). compact=true drops `args` when `text` already carries them " +
        "and truncates each stack to a few frames (far smaller payloads — prefer it). appOnly=true drops " +
        "transport churn (SignalR/WebSocket negotiation, [vite] HMR socket noise). dropFrameworkNoise=true " +
        "SEPARATELY drops known-benign framework banners (DevExtreme/Inferno production-build notice, the " +
        "Angular dev-mode banner, a dev-only router 'Transition was aborted') — set both for a fully clean " +
        "channel so app-level logs and errors stand out. Omit project when only one capture is registered.")]
    public static Task<string> ConsoleTail(
        [Description("Trailing events; 0 = whole buffer (default 200)")] int lines = 0,
        [Description("Min severity: debug|log|info|warn|error|exception")] string? level = null,
        [Description("Only events with seq at/after this; 0 = all")] long sinceSeq = 0,
        [Description("Keep only events whose text/stack contains this substring (case-insensitive)")] string? grep = null,
        [Description("Only events from this pageLoadId (one reload boundary)")] string? pageLoad = null,
        [Description("Scope to the current/most-recent page load (one-call 'did my reload clear it?'); explicit pageLoad wins")] bool currentPageOnly = false,
        [Description("Slim payload: drop args when text exists, truncate stacks to a few frames")] bool compact = false,
        [Description("Drop transport churn (SignalR/WebSocket negotiation, [vite] HMR socket noise)")] bool appOnly = false,
        [Description("Drop known-benign framework banners (Inferno/Angular/router noise); separate from appOnly")] bool dropFrameworkNoise = false,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        var q = $"/console/tail?lines={(lines <= 0 ? 200 : lines)}";
        if (!string.IsNullOrEmpty(level)) q += $"&level={Uri.EscapeDataString(level)}";
        if (sinceSeq > 0) q += $"&sinceSeq={sinceSeq}";
        if (!string.IsNullOrEmpty(grep)) q += $"&grep={Uri.EscapeDataString(grep)}";
        if (!string.IsNullOrEmpty(pageLoad)) q += $"&pageLoad={Uri.EscapeDataString(pageLoad)}";
        if (currentPageOnly) q += "&currentPageOnly=true";
        if (compact) q += "&compact=true";
        if (appOnly) q += "&appOnly=true";
        if (dropFrameworkNoise) q += "&dropFrameworkNoise=true";
        return Hub.ForwardAsync(project, HttpMethod.Get, q, 5);
    }

    [McpServerTool(Name = "console_clear"), Description(
        "Clear the browser-console buffer (e.g. to start a clean run before reproducing an issue). " +
        "Omit project when only one capture is registered.")]
    public static Task<string> ConsoleClear(
        [Description("Project name; omit when only one capture is registered")] string? project = null)
        => Hub.ForwardAsync(project, HttpMethod.Post, "/console/clear", 5);

    // ── inspection ──

    [McpServerTool(Name = "evaluate_js"), Description(
        "Evaluate a JavaScript expression IN the attached page and return the result — the way to read " +
        "live runtime state instead of guessing from source. Runs in global scope, so `window`, `document`, " +
        "framework globals (e.g. Angular's `ng.getComponent($0)`), and your app's globals all resolve. " +
        "Returns {ok, type, value} where value is a string rendering of the result (objects are JSON-stringified, " +
        "DOM nodes/functions/Errors are tagged); on a thrown error returns {ok:false, error, stack}. Set " +
        "awaitPromise=true to await a returned promise before serializing. If the app isn't open in a browser " +
        "the call times out with pageConnected:false. Example: \"ng.getComponent(document.querySelector('app-wire')).energized()\". " +
        "Set json=true to get the result back as real structured JSON (in a `json` field) instead of the default " +
        "string rendering in `value` — cleaner when you return objects/arrays (caps depth/breadth, breaks cycles). " +
        "Reading component state: Angular signals are getter FUNCTIONS, so CALL them — `ng.getComponent($0).value()`, " +
        "not `.value`, which returns the function and looks empty. The read_component tool (or `__kyai.readComponent(el)` " +
        "inline here) does this probing for you across signals/FormControls. Omit project when only one capture is registered.")]
    public static Task<string> EvaluateJs(
        [Description("JavaScript expression to evaluate in the page (global scope)")] string expression,
        [Description("Await a returned promise/thenable before serializing (default false)")] bool awaitPromise = false,
        [Description("Return the result as structured JSON (in `json`) instead of a string in `value` (default false)")] bool json = false,
        [Description("Max ms to wait for the page to return a result (default 5000)")] int timeoutMs = 5000,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(expression)) return Task.FromResult(Bad("expression is required"));
        var budget = Clamp(timeoutMs);
        return Eval(project, budget + 1500, new EvalRequest
        { Id = "", Kind = "eval", Expression = expression, AwaitPromise = awaitPromise, AsJson = json, TimeoutMs = budget });
    }

    [McpServerTool(Name = "query_dom"), Description(
        "Query the attached page's live DOM by CSS selector and return a description of the matched " +
        "element(s): {tag, id, classes, attributes, text (trimmed/clipped), rect:{x,y,w,h}, html (outerHTML, " +
        "clipped)}. Use it to confirm what actually rendered (e.g. an SVG's attributes, a class toggled by " +
        "state) without dumping the whole page. all=false (default) returns just the first match; all=true " +
        "returns up to `limit` matches. `count` is the total number matched. Returns pageConnected:false on " +
        "timeout when the app isn't open. For computed values or method calls, use evaluate_js instead. " +
        "detail=true (default) returns the full description; detail=false slims each element to {tag, id?, " +
        "text} for a cheap listing. Omit project when only one capture is registered.")]
    public static Task<string> QueryDom(
        [Description("CSS selector to match in the page")] string selector,
        [Description("Return all matches (capped by limit) instead of just the first (default false)")] bool all = false,
        [Description("Max elements to describe when all=true (default 20)")] int limit = 20,
        [Description("Full description (default true); false slims each match to {tag, id?, text}")] bool detail = true,
        [Description("Max ms to wait for the page to return a result (default 5000)")] int timeoutMs = 5000,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(selector)) return Task.FromResult(Bad("selector is required"));
        var budget = Clamp(timeoutMs);
        return Eval(project, budget + 1500, new EvalRequest
        { Id = "", Kind = "query", Selector = selector, All = all, Limit = Math.Clamp(limit, 1, 200), Detail = detail, TimeoutMs = budget });
    }

    [McpServerTool(Name = "read_component"), Description(
        "Read the bound STATE of the Angular component on (or above) the element matching `selector` — the " +
        "data behind what rendered, not just its text. Use this to verify an interaction actually changed the " +
        "model (e.g. after picking a dropdown item), a stronger check than the rendered label. It resolves the " +
        "trap that bites a hand-written ng.getComponent(el).value: modern Angular values are SIGNALS (cmp.value " +
        "is a getter FUNCTION — you must CALL it), so reading the field comes back empty. This walks the " +
        "component, CALLS signal getters, unwraps FormControls to their value, and lists drivable methods. " +
        "Returns {ok, component, state, signals, formControls?, methods, objects?, note?} — `methods` are members " +
        "you can drive via evaluate_js (e.g. selectIndex, setValue) when a synthetic click won't commit. Generic " +
        "Angular (not specific to any component library). " +
        "The default `state` is LEAN BY DESIGN so a component isn't a token landmine: it expands only what you " +
        "usually want — signals (resolved/called), FormControls (unwrapped) and plain scalars — and COLLAPSES every " +
        "complex/framework object (injected services, RxJS Subjects, ElementRef/DestroyRef, errorHandler, internal " +
        "view graphs) to a one-line type tag, listing its name in `objects`. To expand specific collapsed fields, " +
        "pass `fields` with just the names you want (e.g. fields:[\"options\",\"value\"]); those are returned in full " +
        "(only depth-limited). Expanded values are still size-capped (depth 3; a value over the budget is summarized) " +
        "and `note` flags when anything was collapsed or trimmed. Raise `depth` to nest deeper. " +
        "signals/formControls/methods/objects always list ALL names, so the discovery surface stays complete. " +
        "Needs a dev / non-production build (window.ng); returns ok:false explaining so otherwise. The same " +
        "logic is callable inline as __kyai.readComponent(elOrSelector, {fields, depth}) from evaluate_js. " +
        "Omit project when only one capture is registered.")]
    public static Task<string> ReadComponent(
        [Description("CSS selector of an element on/under the Angular component to read")] string selector,
        [Description("Only serialize these state fields (by name) in full; omit for all (large values summarized)")] string[]? fields = null,
        [Description("Max nesting depth for serialized values (default 3, max 6)")] int depth = 3,
        [Description("Max ms to wait for the page to return a result (default 5000)")] int timeoutMs = 5000,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(selector)) return Task.FromResult(Bad("selector is required"));
        var budget = Clamp(timeoutMs);
        return Eval(project, budget + 1500, new EvalRequest
        { Id = "", Kind = "component", Selector = selector, Fields = fields, Depth = depth, TimeoutMs = budget });
    }

    [McpServerTool(Name = "get_styles"), Description(
        "Read computed CSS of the first element matching `selector`: {ok, styles:{prop:value,…}, target}. " +
        "Pass `props` (kebab-case names, e.g. [\"transform\",\"stroke\",\"opacity\"]) to pick properties; omit " +
        "for a useful default set (display/visibility/opacity/color/background-color/size/position/transform/" +
        "stroke/fill/cursor/pointer-events/z-index). Use it to confirm what a state change actually rendered " +
        "(e.g. a flow-direction transform or a hover style applied in JS). Returns pageConnected:false on timeout. " +
        "Omit project when only one capture is registered.")]
    public static Task<string> GetStyles(
        [Description("CSS selector of the element to read")] string selector,
        [Description("Computed-style property names (kebab-case); omit for a default set")] string[]? props = null,
        [Description("Max ms to wait for the page (default 5000)")] int timeoutMs = 5000,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(selector)) return Task.FromResult(Bad("selector is required"));
        var budget = Clamp(timeoutMs);
        return Eval(project, budget + 1500, new EvalRequest
        { Id = "", Kind = "styles", Selector = selector, Props = props, TimeoutMs = budget });
    }

    // ── interaction (synthetic; see the type header) ──
    //
    // GATED: click/move/send_key/type_text/scroll/focus/navigate require start_interaction first, which shows
    // the user a fixed red overlay with an animated cursor so they can see the agent driving the page.
    // Call stop_interaction when done. The gate is enforced by the capture instance (it owns the flag).

    [McpServerTool(Name = "start_interaction"), Description(
        "Open supervised interaction — REQUIRED before click/move/send_key/type_text/scroll/focus/navigate. It draws " +
        "a fixed, non-interactable red frame over the app with a cursor icon, so the user can plainly see the " +
        "agent is driving the page; each action then animates that cursor (ripple on click, key cap on a key " +
        "press, the cursor gliding on move). Call stop_interaction when you're finished. Returns {ok, " +
        "shown}. The overlay restores itself if the page reloads while interaction is open. " +
        "Omit project when only one capture is registered.")]
    public static Task<string> StartInteraction(
        [Description("Max ms to wait for the page (default 3000)")] int timeoutMs = 3000,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        var budget = Math.Clamp(timeoutMs, 250, 30_000);
        return Eval(project, budget, new EvalRequest { Id = "", Kind = "overlay", Show = true, TimeoutMs = budget });
    }

    [McpServerTool(Name = "stop_interaction"), Description(
        "Close supervised interaction and remove the overlay. Call this when you're done driving the page; " +
        "afterwards click/move/send_key/type_text/scroll/focus/navigate are blocked again until the next " +
        "start_interaction. Returns {ok, shown:false}. Omit project when only one capture is registered.")]
    public static Task<string> StopInteraction(
        [Description("Max ms to wait for the page (default 3000)")] int timeoutMs = 3000,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        var budget = Math.Clamp(timeoutMs, 250, 30_000);
        return Eval(project, budget, new EvalRequest { Id = "", Kind = "overlay", Show = false, TimeoutMs = budget });
    }

    [McpServerTool(Name = "wait_for_resume"), Description(
        "Block until the user clicks \"resume\" after pausing the badge's Pause icon — use this instead of " +
        "retrying start_interaction yourself after a `paused` refusal. Returns immediately if the user " +
        "never paused it. Returns {ok, paused, killed, interactionActive}; ok:false with timedOut:true if " +
        "the timeout elapses first — call it again to keep waiting. Do NOT call this after a `killed` " +
        "refusal (the harder Stop icon, not Pause) — it returns immediately with killed:true and does not " +
        "wait, because a kill means stop entirely, not \"wait a bit\". Omit project when only one capture " +
        "is registered.")]
    public static Task<string> WaitForResume(
        [Description("Max ms to wait (default 60000)")] int timeoutMs = 60_000,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        var sec = Math.Clamp(timeoutMs / 1000 + 5, 5, 124);
        return Hub.ForwardAsync(project, HttpMethod.Post, $"/wait-for-resume?timeout={timeoutMs}", sec);
    }

    [McpServerTool(Name = "click"), Description(
        "Synthetically click an element — a full pointer/mouse sequence (pointerover/enter, pointerdown, " +
        "mousedown, focus, pointerup, mouseup) then the element's click() so default actions (toggle, submit, " +
        "navigate) fire, not just listeners. Target THREE ways: `selector` (CSS); `text` (the deepest visible " +
        "element whose label equals it — set exact=false for substring, within=<css> to scope the search); or " +
        "viewport `x`,`y` (the topmost element there, descending into open shadow roots). Targeting by text is " +
        "usually the easiest for buttons/menu items/links. button=right fires contextmenu. Returns {ok, action, " +
        "point, target} where target is minimal ({tag, id?, text}) — enough to confirm the hit; set detail=true " +
        "for the full element. Synthetic (isTrusted:false): drives JS handlers, not CSS :hover or " +
        "user-activation-gated APIs; if a custom widget ignores it, fall back to evaluate_js + ng.getComponent " +
        "(and read_component to verify the model changed). Omit project when only one capture is registered.")]
    public static Task<string> Click(
        [Description("CSS selector of the element to click (its center is used)")] string? selector = null,
        [Description("Click the element with this visible text (deepest/most-specific match)")] string? text = null,
        [Description("With text: restrict the search to this container (CSS selector)")] string? within = null,
        [Description("With text: exact match (default) vs substring when false")] bool exact = true,
        [Description("Viewport X in CSS px (requires y; used when no selector/text)")] int? x = null,
        [Description("Viewport Y in CSS px (requires x)")] int? y = null,
        [Description("Mouse button: left|middle|right (default left; right fires contextmenu)")] string button = "left",
        [Description("Hold Ctrl")] bool ctrl = false,
        [Description("Hold Shift")] bool shift = false,
        [Description("Hold Alt")] bool alt = false,
        [Description("Hold Meta/Cmd/Win")] bool meta = false,
        [Description("Return the full target element (classes/attributes/rect/outerHTML) instead of {tag, id?, text}")] bool detail = false,
        [Description("Max ms to wait for the page (default 5000)")] int timeoutMs = 5000,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(selector) && string.IsNullOrWhiteSpace(text) && (x is null || y is null))
            return Task.FromResult(Bad("click requires a selector, text, or both x and y"));
        var budget = Clamp(timeoutMs);
        return Eval(project, budget + 1500, new EvalRequest
        { Id = "", Kind = "click", Selector = selector, Text = text, Within = within, Exact = exact, X = x, Y = y, Button = button, Ctrl = ctrl, Shift = shift, Alt = alt, Meta = meta, Detail = detail, TimeoutMs = budget });
    }

    [McpServerTool(Name = "move"), Description(
        "Move the synthetic pointer along a path from (fromX,fromY) to (toX,toY) over `durationMs`, " +
        "dispatching pointermove/mousemove at each step and, when the element under the point changes, the " +
        "pointerout/leave + pointerover/enter bookkeeping a real browser does. This drives JS hover/dwell/" +
        "nearest-element logic (e.g. a diagram highlighting the wire under the cursor) — but NOT CSS :hover " +
        "(synthetic events can't). Omit from* to start at the destination (a hover-in-place). Returns {ok, " +
        "from, to, steps, finalTarget, traversed}. Omit project when only one capture is registered.")]
    public static Task<string> Move(
        [Description("Destination viewport X (CSS px)")] int toX,
        [Description("Destination viewport Y (CSS px)")] int toY,
        [Description("Start X (default: same as toX — hover in place)")] int? fromX = null,
        [Description("Start Y (default: same as toY)")] int? fromY = null,
        [Description("Path duration in ms (default 300)")] int durationMs = 300,
        [Description("Number of move steps (default: ~1 per 16ms, capped)")] int? steps = null,
        [Description("Return the full finalTarget element instead of {tag, id?, text}")] bool detail = false,
        [Description("Max ms to wait for the page (default: durationMs + headroom)")] int timeoutMs = 0,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        var dur = Math.Clamp(durationMs, 0, 120_000);
        var effective = Math.Max(timeoutMs > 0 ? Clamp(timeoutMs) : 0, dur);
        var budget = effective + 2000;
        return Eval(project, budget, new EvalRequest
        { Id = "", Kind = "move", FromX = fromX, FromY = fromY, ToX = toX, ToY = toY, DurationMs = dur, Steps = steps is null ? null : Math.Clamp(steps.Value, 1, 500), Detail = detail, TimeoutMs = budget });
    }

    [McpServerTool(Name = "send_key"), Description(
        "Dispatch a synthetic keydown/(keypress for printable)/keyup for one key to a target (`selector`, " +
        "else the focused element). Use for keyboard handlers and shortcuts — Enter, Escape, Arrow keys, " +
        "Ctrl+S, etc. `key` is the KeyboardEvent.key value (e.g. \"Enter\", \"a\", \"ArrowLeft\"); `code` is " +
        "optional (e.g. \"KeyA\"). Modifiers are flags. NOTE this does NOT change an input's value — to fill " +
        "a field use type_text. Returns {ok, action, key, target}. Omit project when only one capture is registered.")]
    public static Task<string> SendKey(
        [Description("KeyboardEvent.key value, e.g. \"Enter\", \"Escape\", \"ArrowLeft\", \"a\"")] string key,
        [Description("Optional KeyboardEvent.code, e.g. \"KeyA\", \"Enter\"")] string? code = null,
        [Description("Target selector; omit to send to the focused element")] string? selector = null,
        [Description("Hold Ctrl")] bool ctrl = false,
        [Description("Hold Shift")] bool shift = false,
        [Description("Hold Alt")] bool alt = false,
        [Description("Hold Meta/Cmd/Win")] bool meta = false,
        [Description("Return the full target element instead of {tag, id?, text}")] bool detail = false,
        [Description("Max ms to wait for the page (default 5000)")] int timeoutMs = 5000,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        if (string.IsNullOrEmpty(key)) return Task.FromResult(Bad("key is required"));
        var budget = Clamp(timeoutMs);
        return Eval(project, budget + 1500, new EvalRequest
        { Id = "", Kind = "key", Key = key, Code = code, Selector = selector, Ctrl = ctrl, Shift = shift, Alt = alt, Meta = meta, Detail = detail, TimeoutMs = budget });
    }

    [McpServerTool(Name = "type_text"), Description(
        "Type text into a form field or contenteditable matched by `selector`: focuses it, sets the value via " +
        "the native setter, then fires input + change so frameworks observe it (Angular reactive forms / " +
        "ngModel update on the input event — a bare key dispatch would not). append=true keeps the existing " +
        "value, otherwise it replaces. Returns {ok, action, value, target}. For shortcuts/navigation keys use " +
        "send_key instead. Omit project when only one capture is registered.")]
    public static Task<string> TypeText(
        [Description("CSS selector of the input/textarea/select/contenteditable")] string selector,
        [Description("Text to type")] string text,
        [Description("Append to the current value instead of replacing it (default false)")] bool append = false,
        [Description("Return the full target element instead of {tag, id?, text}")] bool detail = false,
        [Description("Max ms to wait for the page (default 5000)")] int timeoutMs = 5000,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(selector)) return Task.FromResult(Bad("selector is required"));
        var budget = Clamp(timeoutMs);
        return Eval(project, budget + 1500, new EvalRequest
        { Id = "", Kind = "type", Selector = selector, Text = text ?? "", Append = append, Detail = detail, TimeoutMs = budget });
    }

    [McpServerTool(Name = "scroll"), Description(
        "Scroll the page or an element. With `selector` and no x/y: scrollIntoView (centered) — use before a " +
        "coordinate click to bring a target into the viewport. With `selector` and x/y: scroll within that " +
        "element. With no selector: window.scrollTo(x,y). Returns {ok, action, …, target?}. " +
        "Omit project when only one capture is registered.")]
    public static Task<string> Scroll(
        [Description("Element to scroll into view (or scroll within, if x/y given). Omit to scroll the window.")] string? selector = null,
        [Description("Target scroll X (CSS px)")] int? x = null,
        [Description("Target scroll Y (CSS px)")] int? y = null,
        [Description("Return the full target element instead of {tag, id?, text}")] bool detail = false,
        [Description("Max ms to wait for the page (default 5000)")] int timeoutMs = 5000,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        var budget = Clamp(timeoutMs);
        return Eval(project, budget + 1500, new EvalRequest
        { Id = "", Kind = "scroll", Selector = selector, X = x, Y = y, Detail = detail, TimeoutMs = budget });
    }

    [McpServerTool(Name = "focus"), Description(
        "Focus the element matching `selector` (synthetic dispatch doesn't auto-focus inputs the way a real " +
        "click does, so this is sometimes needed first). blur=true blurs it instead (or the active element " +
        "when selector is omitted). Returns {ok, action, focused?, target}. Omit project when only one capture is registered.")]
    public static Task<string> Focus(
        [Description("CSS selector to focus (or blur)")] string? selector = null,
        [Description("Blur instead of focus (selector optional → the active element)")] bool blur = false,
        [Description("Return the full target element instead of {tag, id?, text}")] bool detail = false,
        [Description("Max ms to wait for the page (default 5000)")] int timeoutMs = 5000,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(selector) && !blur) return Task.FromResult(Bad("focus requires a selector (or set blur=true)"));
        var budget = Clamp(timeoutMs);
        return Eval(project, budget + 1500, new EvalRequest
        { Id = "", Kind = "focus", Selector = selector, Blur = blur, Detail = detail, TimeoutMs = budget });
    }

    [McpServerTool(Name = "wait_for"), Description(
        "Wait in-page until a `selector` matches an element OR an `expression` evaluates truthy, polling until " +
        "ready or `timeoutMs` elapses — the way to avoid acting before an async SPA has rendered. Returns " +
        "{ok:true, matched:true, detail} on success (detail describes the element or the value), or " +
        "{ok:false, matched:false, timedOut:true} on timeout. The expression form keeps polling even if it " +
        "throws (e.g. a global not defined yet). Provide exactly one of selector/expression. " +
        "Omit project when only one capture is registered.")]
    public static Task<string> WaitFor(
        [Description("CSS selector to wait for")] string? selector = null,
        [Description("JS expression to wait for (truthy); evaluated in global scope")] string? expression = null,
        [Description("Max ms to wait before giving up (default 5000)")] int timeoutMs = 5000,
        [Description("Poll interval in ms (default 100)")] int pollMs = 100,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(selector) && string.IsNullOrWhiteSpace(expression))
            return Task.FromResult(Bad("wait_for requires a selector or an expression"));
        var wait = Clamp(timeoutMs);
        return Eval(project, wait + 2000, new EvalRequest
        { Id = "", Kind = "wait", Selector = selector, Expression = expression, PollMs = Math.Clamp(pollMs, 20, 5000), TimeoutMs = wait });
    }

    [McpServerTool(Name = "reload_page"), Description(
        "Reload the attached page (location.reload()). Use after a build that changed code rather than just " +
        "templates/styles: a hot reload may keep already-created objects (services, singletons, model " +
        "instances) on the previous version, so a green build can still be running stale logic — a reload " +
        "re-instantiates everything. This is a FULL reload, not navigation — to change an SPA route WITHOUT " +
        "re-instantiating everything, use `navigate` instead. Returns {ok, dispatched} once the " +
        "page picks up the reload; capture re-attaches automatically on the fresh load (a new pageLoadId). " +
        "Returns pageConnected:false if no page is open. Omit project when only one capture is registered.")]
    public static Task<string> ReloadPage(
        [Description("Max ms to wait for the page to pick up the reload (default 3000)")] int timeoutMs = 3000,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        var budget = Math.Clamp(timeoutMs, 250, 30_000);
        return Eval(project, budget, new EvalRequest { Id = "", Kind = "reload", TimeoutMs = budget });
    }

    [McpServerTool(Name = "navigate"), Description(
        "Change the SPA route WITHOUT a hard reload — the router-aware alternative to reload_page and to the " +
        "click-a-nav-link / evaluate_js-the-router dance. On a dev build it finds the Angular Router and calls " +
        "router.navigateByUrl(path); if the Router can't be resolved (production build, etc.) it falls back to " +
        "the History API (pushState + a synthetic popstate the default PathLocationStrategy picks up). Because " +
        "it does not reload, already-created services/singletons stay live (use reload_page when you need those " +
        "re-instantiated). Returns {ok, from, to, navigated, method:'router'|'history'}; `to` is the settled " +
        "location.href so you can confirm the destination even if a route guard redirected. REQUIRES " +
        "start_interaction first, like click/move/send_key/type_text/scroll/focus — it changes what's on screen, " +
        "so the user needs the same visible overlay. If the route resolves async, follow with `wait_for` on a " +
        "destination selector or location.pathname. Omit project when only one capture is registered.")]
    public static Task<string> Navigate(
        [Description("Target route/URL to navigate to, e.g. \"/orders/42\" (router path or same-origin URL)")] string path,
        [Description("Use replaceState instead of pushState in the History fallback (no new history entry)")] bool replace = false,
        [Description("Max ms to wait for the navigation to settle (default 5000)")] int timeoutMs = 5000,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult(Bad("path is required"));
        var budget = Clamp(timeoutMs);
        return Eval(project, budget + 1500, new EvalRequest
        { Id = "", Kind = "navigate", Path = path, Replace = replace, TimeoutMs = budget });
    }

    [McpServerTool(Name = "batch"), Description(
        "Run a sequence of actions in ONE page round-trip — much faster than separate calls for a multi-step " +
        "flow. `steps` is an ordered list; each step is { action, …the same fields that action's own tool takes }. " +
        "action ∈ click | move | key | type | wait | scroll | focus | styles | query | component | eval. Steps run in order " +
        "and STOP at the first failure; the result is { ok, count, results:[{step, action, …payload}], failedAt? }. " +
        "Any manipulation step (click/move/key/type/scroll/focus) requires start_interaction first. Example — open " +
        "a menu then pick an item in one call: steps=[{action:'click',selector:'.menu'},{action:'wait',selector:" +
        "'.item'},{action:'click',text:'Zwei'}]. Omit project when only one capture is registered.")]
    public static Task<string> Batch(
        [Description("Ordered steps; each is { action, plus that action's fields }")] BatchStep[] steps,
        [Description("Max ms for the whole sequence (default: derived from the steps' own waits/durations)")] int timeoutMs = 0,
        [Description("Project name; omit when only one capture is registered")] string? project = null)
    {
        if (steps is null || steps.Length == 0) return Task.FromResult(Bad("batch requires at least one step"));
        var derived = 2000 + steps.Sum(s =>
            s.Action == "wait" ? (s.TimeoutMs ?? 5000) :
            s.Action == "move" ? (s.DurationMs ?? 300) : 500);
        var budget = Math.Clamp(timeoutMs > 0 ? timeoutMs : derived, 1000, 300_000);
        return Eval(project, budget + 1500, new EvalRequest { Id = "", Kind = "batch", Actions = steps, TimeoutMs = budget });
    }

    [McpServerTool(Name = "shutdown"), Description(
        "Tear down the whole stack: stop every registered capture instance (each detaches and restores its " +
        "app's index.html) and then the hub process itself. The `ky-ai-browser shutdown` CLI command and " +
        "POST/GET /shutdown do the same.")]
    public static Task<string> Shutdown() => Hub.ShutdownAllAsync();

    // Test seam: when set, eval forwards are routed here (capturing the EvalRequest + waitMs) instead
    // of going out over HTTP — letting tests assert the request a tool builds, and run it through the
    // real instance dispatcher, without a live hub. Null in production.
    internal static Func<string?, int, EvalRequest, Task<string>>? ForwardHook;

    // Package an EvalRequest and forward it to the resolved capture instance's /eval. waitMs is how long
    // the instance parks the call waiting on the page; the HTTP timeout sits a few seconds above it.
    private static Task<string> Eval(string? project, int waitMs, EvalRequest req)
    {
        if (ForwardHook is { } hook) return hook(project, waitMs, req);
        var body = JsonSerializer.Serialize(req, Wire);
        var sec = Math.Clamp(waitMs / 1000 + 5, 5, 320);
        return Hub.ForwardAsync(project, HttpMethod.Post, $"/eval?waitMs={waitMs}", sec, body);
    }

    private static int Clamp(int timeoutMs) => Math.Clamp(timeoutMs, 250, 120_000);

    private static string Bad(string error) => JsonSerializer.Serialize(new { ok = false, error }, Json);
}
