using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace KY.AI.Browser;

// MCP tools ky-ai-browser exposes (allow-list as mcp__ky-ai-browser__<name>). The read tools
// (console_tail / console_clear) read the in-process capture buffer directly; the rest push work to
// the page over the eval channel and await the result. Inspection: evaluate_js / query_dom /
// get_styles / read_component. Interaction: click / move / send_key / type_text / scroll / focus /
// wait_for / reload_page. If capture isn't attached they return enabled:false rather than erroring,
// so the agent can tell "not running" from "no events / no page".
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
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [McpServerTool(Name = "console_tail"), Description(
        "Return recent BROWSER/runtime console events from the app ky-ai-browser is attached to " +
        "(console.log/info/warn/error, uncaught exceptions, unhandled promise rejections). Each event is " +
        "{seq, level, args, text, source, line, col, stack, timestamp, pageLoadId}; the response also " +
        "carries `dropped` (events lost to a flood) and `enabled` (false when ky-ai-browser isn't running " +
        "— start it next to your `ky-ai-ng serve`). Filters compose: level keeps that severity and above " +
        "(debug<log<info<warn<error<exception); sinceSeq returns events after a prior tail's max seq; grep " +
        "is a case-insensitive substring over text+stack; pageLoad isolates one page load (reload boundary). " +
        "compact=true drops `args` when `text` already carries them and truncates each stack to a few frames " +
        "(far smaller payloads — prefer it). appOnly=true drops transport churn (SignalR/WebSocket " +
        "negotiation, [vite] HMR socket noise) so app-level logs and errors stand out.")]
    public static string ConsoleTail(
        [Description("Trailing events; 0 = whole buffer (default 200)")] int lines = 0,
        [Description("Min severity: debug|log|info|warn|error|exception")] string? level = null,
        [Description("Only events with seq at/after this; 0 = all")] long sinceSeq = 0,
        [Description("Keep only events whose text/stack contains this substring (case-insensitive)")] string? grep = null,
        [Description("Only events from this pageLoadId (one reload boundary)")] string? pageLoad = null,
        [Description("Slim payload: drop args when text exists, truncate stacks to a few frames")] bool compact = false,
        [Description("Drop transport churn (SignalR/WebSocket negotiation, [vite] HMR socket noise)")] bool appOnly = false)
        => Capture.Collector is { } c
            ? c.TailJson("browser", enabled: true, lines <= 0 ? 200 : lines, level, sinceSeq, sinceBuildSeq: 0, grep, pageLoad, compact, appOnly)
            : JsonSerializer.Serialize(new { enabled = false, error = "ky-ai-browser capture not running" });

    [McpServerTool(Name = "console_clear"), Description(
        "Clear the browser-console buffer (e.g. to start a clean run before reproducing an issue).")]
    public static string ConsoleClear()
    {
        Capture.Collector?.Clear();
        return JsonSerializer.Serialize(new { ok = true, action = "console_clear" });
    }

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
        "inline here) does this probing for you across signals/FormControls.")]
    public static Task<string> EvaluateJs(
        [Description("JavaScript expression to evaluate in the page (global scope)")] string expression,
        [Description("Await a returned promise/thenable before serializing (default false)")] bool awaitPromise = false,
        [Description("Return the result as structured JSON (in `json`) instead of a string in `value` (default false)")] bool json = false,
        [Description("Max ms to wait for the page to return a result (default 5000)")] int timeoutMs = 5000)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        if (string.IsNullOrWhiteSpace(expression))
            return Task.FromResult(Bad("expression is required"));
        var budget = Clamp(timeoutMs);
        return ch.RequestAsync(
            id => new EvalRequest { Id = id, Kind = "eval", Expression = expression, AwaitPromise = awaitPromise, AsJson = json, TimeoutMs = budget },
            budget + 1500, CancellationToken.None);
    }

    [McpServerTool(Name = "query_dom"), Description(
        "Query the attached page's live DOM by CSS selector and return a description of the matched " +
        "element(s): {tag, id, classes, attributes, text (trimmed/clipped), rect:{x,y,w,h}, html (outerHTML, " +
        "clipped)}. Use it to confirm what actually rendered (e.g. an SVG's attributes, a class toggled by " +
        "state) without dumping the whole page. all=false (default) returns just the first match; all=true " +
        "returns up to `limit` matches. `count` is the total number matched. Returns pageConnected:false on " +
        "timeout when the app isn't open. For computed values or method calls, use evaluate_js instead. " +
        "detail=true (default) returns the full description; detail=false slims each element to {tag, id?, " +
        "text} for a cheap listing.")]
    public static Task<string> QueryDom(
        [Description("CSS selector to match in the page")] string selector,
        [Description("Return all matches (capped by limit) instead of just the first (default false)")] bool all = false,
        [Description("Max elements to describe when all=true (default 20)")] int limit = 20,
        [Description("Full description (default true); false slims each match to {tag, id?, text}")] bool detail = true,
        [Description("Max ms to wait for the page to return a result (default 5000)")] int timeoutMs = 5000)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        if (string.IsNullOrWhiteSpace(selector)) return Task.FromResult(Bad("selector is required"));
        var budget = Clamp(timeoutMs);
        return ch.RequestAsync(
            id => new EvalRequest { Id = id, Kind = "query", Selector = selector, All = all, Limit = Math.Clamp(limit, 1, 200), Detail = detail, TimeoutMs = budget },
            budget + 1500, CancellationToken.None);
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
        "logic is callable inline as __kyai.readComponent(elOrSelector, {fields, depth}) from evaluate_js.")]
    public static Task<string> ReadComponent(
        [Description("CSS selector of an element on/under the Angular component to read")] string selector,
        [Description("Only serialize these state fields (by name) in full; omit for all (large values summarized)")] string[]? fields = null,
        [Description("Max nesting depth for serialized values (default 3, max 6)")] int depth = 3,
        [Description("Max ms to wait for the page to return a result (default 5000)")] int timeoutMs = 5000)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        if (string.IsNullOrWhiteSpace(selector)) return Task.FromResult(Bad("selector is required"));
        var budget = Clamp(timeoutMs);
        return ch.RequestAsync(
            id => new EvalRequest { Id = id, Kind = "component", Selector = selector, Fields = fields, Depth = depth, TimeoutMs = budget },
            budget + 1500, CancellationToken.None);
    }

    [McpServerTool(Name = "get_styles"), Description(
        "Read computed CSS of the first element matching `selector`: {ok, styles:{prop:value,…}, target}. " +
        "Pass `props` (kebab-case names, e.g. [\"transform\",\"stroke\",\"opacity\"]) to pick properties; omit " +
        "for a useful default set (display/visibility/opacity/color/background-color/size/position/transform/" +
        "stroke/fill/cursor/pointer-events/z-index). Use it to confirm what a state change actually rendered " +
        "(e.g. a flow-direction transform or a hover style applied in JS). Returns pageConnected:false on timeout.")]
    public static Task<string> GetStyles(
        [Description("CSS selector of the element to read")] string selector,
        [Description("Computed-style property names (kebab-case); omit for a default set")] string[]? props = null,
        [Description("Max ms to wait for the page (default 5000)")] int timeoutMs = 5000)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        if (string.IsNullOrWhiteSpace(selector)) return Task.FromResult(Bad("selector is required"));
        var budget = Clamp(timeoutMs);
        return ch.RequestAsync(
            id => new EvalRequest { Id = id, Kind = "styles", Selector = selector, Props = props, TimeoutMs = budget },
            budget + 1500, CancellationToken.None);
    }

    // ── interaction (synthetic; see the type header) ──
    //
    // GATED: click/move/send_key/type_text/scroll/focus require start_interaction first, which shows
    // the user a fixed red overlay with an animated cursor so they can see the agent driving the page.
    // Call stop_interaction when done.

    [McpServerTool(Name = "start_interaction"), Description(
        "Open supervised interaction — REQUIRED before click/move/send_key/type_text/scroll/focus. It draws " +
        "a fixed, non-interactable red frame over the app with a cursor icon, so the user can plainly see the " +
        "agent is driving the page; each action then animates that cursor (ripple on click, key cap on a key " +
        "press, the cursor gliding on move). Call stop_interaction when you're finished. Returns {ok, " +
        "shown}. The overlay restores itself if the page reloads while interaction is open.")]
    public static Task<string> StartInteraction(
        [Description("Max ms to wait for the page (default 3000)")] int timeoutMs = 3000)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        ch.SetInteraction(true);
        var budget = Math.Clamp(timeoutMs, 250, 30_000);
        return ch.RequestAsync(id => new EvalRequest { Id = id, Kind = "overlay", Show = true, TimeoutMs = budget }, budget, CancellationToken.None);
    }

    [McpServerTool(Name = "stop_interaction"), Description(
        "Close supervised interaction and remove the overlay. Call this when you're done driving the page; " +
        "afterwards click/move/send_key/type_text/scroll/focus are blocked again until the next " +
        "start_interaction. Returns {ok, shown:false}.")]
    public static Task<string> StopInteraction(
        [Description("Max ms to wait for the page (default 3000)")] int timeoutMs = 3000)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        ch.SetInteraction(false);
        var budget = Math.Clamp(timeoutMs, 250, 30_000);
        return ch.RequestAsync(id => new EvalRequest { Id = id, Kind = "overlay", Show = false, TimeoutMs = budget }, budget, CancellationToken.None);
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
        "(and read_component to verify the model changed).")]
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
        [Description("Max ms to wait for the page (default 5000)")] int timeoutMs = 5000)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        if (!ch.InteractionActive) return Task.FromResult(NeedsInteraction());
        if (string.IsNullOrWhiteSpace(selector) && string.IsNullOrWhiteSpace(text) && (x is null || y is null))
            return Task.FromResult(Bad("click requires a selector, text, or both x and y"));
        var budget = Clamp(timeoutMs);
        return ch.RequestAsync(
            id => new EvalRequest { Id = id, Kind = "click", Selector = selector, Text = text, Within = within, Exact = exact, X = x, Y = y, Button = button, Ctrl = ctrl, Shift = shift, Alt = alt, Meta = meta, Detail = detail, TimeoutMs = budget },
            budget + 1500, CancellationToken.None);
    }

    [McpServerTool(Name = "move"), Description(
        "Move the synthetic pointer along a path from (fromX,fromY) to (toX,toY) over `durationMs`, " +
        "dispatching pointermove/mousemove at each step and, when the element under the point changes, the " +
        "pointerout/leave + pointerover/enter bookkeeping a real browser does. This drives JS hover/dwell/" +
        "nearest-element logic (e.g. a diagram highlighting the wire under the cursor) — but NOT CSS :hover " +
        "(synthetic events can't). Omit from* to start at the destination (a hover-in-place). Returns {ok, " +
        "from, to, steps, finalTarget, traversed}.")]
    public static Task<string> Move(
        [Description("Destination viewport X (CSS px)")] int toX,
        [Description("Destination viewport Y (CSS px)")] int toY,
        [Description("Start X (default: same as toX — hover in place)")] int? fromX = null,
        [Description("Start Y (default: same as toY)")] int? fromY = null,
        [Description("Path duration in ms (default 300)")] int durationMs = 300,
        [Description("Number of move steps (default: ~1 per 16ms, capped)")] int? steps = null,
        [Description("Return the full finalTarget element instead of {tag, id?, text}")] bool detail = false,
        [Description("Max ms to wait for the page (default: durationMs + headroom)")] int timeoutMs = 0)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        if (!ch.InteractionActive) return Task.FromResult(NeedsInteraction());
        var dur = Math.Clamp(durationMs, 0, 120_000);
        var effective = Math.Max(timeoutMs > 0 ? Clamp(timeoutMs) : 0, dur);
        var budget = effective + 2000;
        return ch.RequestAsync(
            id => new EvalRequest { Id = id, Kind = "move", FromX = fromX, FromY = fromY, ToX = toX, ToY = toY, DurationMs = dur, Steps = steps is null ? null : Math.Clamp(steps.Value, 1, 500), Detail = detail, TimeoutMs = budget },
            budget, CancellationToken.None);
    }

    [McpServerTool(Name = "send_key"), Description(
        "Dispatch a synthetic keydown/(keypress for printable)/keyup for one key to a target (`selector`, " +
        "else the focused element). Use for keyboard handlers and shortcuts — Enter, Escape, Arrow keys, " +
        "Ctrl+S, etc. `key` is the KeyboardEvent.key value (e.g. \"Enter\", \"a\", \"ArrowLeft\"); `code` is " +
        "optional (e.g. \"KeyA\"). Modifiers are flags. NOTE this does NOT change an input's value — to fill " +
        "a field use type_text. Returns {ok, action, key, target}.")]
    public static Task<string> SendKey(
        [Description("KeyboardEvent.key value, e.g. \"Enter\", \"Escape\", \"ArrowLeft\", \"a\"")] string key,
        [Description("Optional KeyboardEvent.code, e.g. \"KeyA\", \"Enter\"")] string? code = null,
        [Description("Target selector; omit to send to the focused element")] string? selector = null,
        [Description("Hold Ctrl")] bool ctrl = false,
        [Description("Hold Shift")] bool shift = false,
        [Description("Hold Alt")] bool alt = false,
        [Description("Hold Meta/Cmd/Win")] bool meta = false,
        [Description("Return the full target element instead of {tag, id?, text}")] bool detail = false,
        [Description("Max ms to wait for the page (default 5000)")] int timeoutMs = 5000)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        if (!ch.InteractionActive) return Task.FromResult(NeedsInteraction());
        if (string.IsNullOrEmpty(key)) return Task.FromResult(Bad("key is required"));
        var budget = Clamp(timeoutMs);
        return ch.RequestAsync(
            id => new EvalRequest { Id = id, Kind = "key", Key = key, Code = code, Selector = selector, Ctrl = ctrl, Shift = shift, Alt = alt, Meta = meta, Detail = detail, TimeoutMs = budget },
            budget + 1500, CancellationToken.None);
    }

    [McpServerTool(Name = "type_text"), Description(
        "Type text into a form field or contenteditable matched by `selector`: focuses it, sets the value via " +
        "the native setter, then fires input + change so frameworks observe it (Angular reactive forms / " +
        "ngModel update on the input event — a bare key dispatch would not). append=true keeps the existing " +
        "value, otherwise it replaces. Returns {ok, action, value, target}. For shortcuts/navigation keys use " +
        "send_key instead.")]
    public static Task<string> TypeText(
        [Description("CSS selector of the input/textarea/select/contenteditable")] string selector,
        [Description("Text to type")] string text,
        [Description("Append to the current value instead of replacing it (default false)")] bool append = false,
        [Description("Return the full target element instead of {tag, id?, text}")] bool detail = false,
        [Description("Max ms to wait for the page (default 5000)")] int timeoutMs = 5000)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        if (!ch.InteractionActive) return Task.FromResult(NeedsInteraction());
        if (string.IsNullOrWhiteSpace(selector)) return Task.FromResult(Bad("selector is required"));
        var budget = Clamp(timeoutMs);
        return ch.RequestAsync(
            id => new EvalRequest { Id = id, Kind = "type", Selector = selector, Text = text ?? "", Append = append, Detail = detail, TimeoutMs = budget },
            budget + 1500, CancellationToken.None);
    }

    [McpServerTool(Name = "scroll"), Description(
        "Scroll the page or an element. With `selector` and no x/y: scrollIntoView (centered) — use before a " +
        "coordinate click to bring a target into the viewport. With `selector` and x/y: scroll within that " +
        "element. With no selector: window.scrollTo(x,y). Returns {ok, action, …, target?}.")]
    public static Task<string> Scroll(
        [Description("Element to scroll into view (or scroll within, if x/y given). Omit to scroll the window.")] string? selector = null,
        [Description("Target scroll X (CSS px)")] int? x = null,
        [Description("Target scroll Y (CSS px)")] int? y = null,
        [Description("Return the full target element instead of {tag, id?, text}")] bool detail = false,
        [Description("Max ms to wait for the page (default 5000)")] int timeoutMs = 5000)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        if (!ch.InteractionActive) return Task.FromResult(NeedsInteraction());
        var budget = Clamp(timeoutMs);
        return ch.RequestAsync(
            id => new EvalRequest { Id = id, Kind = "scroll", Selector = selector, X = x, Y = y, Detail = detail, TimeoutMs = budget },
            budget + 1500, CancellationToken.None);
    }

    [McpServerTool(Name = "focus"), Description(
        "Focus the element matching `selector` (synthetic dispatch doesn't auto-focus inputs the way a real " +
        "click does, so this is sometimes needed first). blur=true blurs it instead (or the active element " +
        "when selector is omitted). Returns {ok, action, focused?, target}.")]
    public static Task<string> Focus(
        [Description("CSS selector to focus (or blur)")] string? selector = null,
        [Description("Blur instead of focus (selector optional → the active element)")] bool blur = false,
        [Description("Return the full target element instead of {tag, id?, text}")] bool detail = false,
        [Description("Max ms to wait for the page (default 5000)")] int timeoutMs = 5000)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        if (!ch.InteractionActive) return Task.FromResult(NeedsInteraction());
        if (string.IsNullOrWhiteSpace(selector) && !blur) return Task.FromResult(Bad("focus requires a selector (or set blur=true)"));
        var budget = Clamp(timeoutMs);
        return ch.RequestAsync(
            id => new EvalRequest { Id = id, Kind = "focus", Selector = selector, Blur = blur, Detail = detail, TimeoutMs = budget },
            budget + 1500, CancellationToken.None);
    }

    [McpServerTool(Name = "wait_for"), Description(
        "Wait in-page until a `selector` matches an element OR an `expression` evaluates truthy, polling until " +
        "ready or `timeoutMs` elapses — the way to avoid acting before an async SPA has rendered. Returns " +
        "{ok:true, matched:true, detail} on success (detail describes the element or the value), or " +
        "{ok:false, matched:false, timedOut:true} on timeout. The expression form keeps polling even if it " +
        "throws (e.g. a global not defined yet). Provide exactly one of selector/expression.")]
    public static Task<string> WaitFor(
        [Description("CSS selector to wait for")] string? selector = null,
        [Description("JS expression to wait for (truthy); evaluated in global scope")] string? expression = null,
        [Description("Max ms to wait before giving up (default 5000)")] int timeoutMs = 5000,
        [Description("Poll interval in ms (default 100)")] int pollMs = 100)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        if (string.IsNullOrWhiteSpace(selector) && string.IsNullOrWhiteSpace(expression))
            return Task.FromResult(Bad("wait_for requires a selector or an expression"));
        var wait = Clamp(timeoutMs);
        return ch.RequestAsync(
            id => new EvalRequest { Id = id, Kind = "wait", Selector = selector, Expression = expression, PollMs = Math.Clamp(pollMs, 20, 5000), TimeoutMs = wait },
            wait + 2000, CancellationToken.None);
    }

    [McpServerTool(Name = "reload_page"), Description(
        "Reload the attached page (location.reload()). Use after a build that changed code rather than just " +
        "templates/styles: a hot reload may keep already-created objects (services, singletons, model " +
        "instances) on the previous version, so a green build can still be running stale logic — a reload " +
        "re-instantiates everything. This is a FULL reload, not navigation: there is deliberately no navigate " +
        "tool — to change an SPA route, click a nav link (synthetic `click` on the <a>/routerLink) or drive the " +
        "router via evaluate_js, then `wait_for` location.pathname to settle. Returns {ok, dispatched} once the " +
        "page picks up the reload; capture re-attaches automatically on the fresh load (a new pageLoadId). " +
        "Returns pageConnected:false if no page is open.")]
    public static Task<string> ReloadPage(
        [Description("Max ms to wait for the page to pick up the reload (default 3000)")] int timeoutMs = 3000)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        var budget = Math.Clamp(timeoutMs, 250, 30_000);
        return ch.RequestAsync(id => new EvalRequest { Id = id, Kind = "reload", TimeoutMs = budget }, budget, CancellationToken.None);
    }

    [McpServerTool(Name = "batch"), Description(
        "Run a sequence of actions in ONE page round-trip — much faster than separate calls for a multi-step " +
        "flow. `steps` is an ordered list; each step is { action, …the same fields that action's own tool takes }. " +
        "action ∈ click | move | key | type | wait | scroll | focus | styles | query | component | eval. Steps run in order " +
        "and STOP at the first failure; the result is { ok, count, results:[{step, action, …payload}], failedAt? }. " +
        "Any manipulation step (click/move/key/type/scroll/focus) requires start_interaction first. Example — open " +
        "a menu then pick an item in one call: steps=[{action:'click',selector:'.menu'},{action:'wait',selector:" +
        "'.item'},{action:'click',text:'Zwei'}].")]
    public static Task<string> Batch(
        [Description("Ordered steps; each is { action, plus that action's fields }")] BatchStep[] steps,
        [Description("Max ms for the whole sequence (default: derived from the steps' own waits/durations)")] int timeoutMs = 0)
    {
        if (Capture.Eval is not { } ch) return Task.FromResult(NotRunning());
        if (steps is null || steps.Length == 0) return Task.FromResult(Bad("batch requires at least one step"));
        if (steps.Any(s => s.IsManipulation) && !ch.InteractionActive) return Task.FromResult(NeedsInteraction());

        var derived = 2000 + steps.Sum(s =>
            s.Action == "wait" ? (s.TimeoutMs ?? 5000) :
            s.Action == "move" ? (s.DurationMs ?? 300) : 500);
        var budget = Math.Clamp(timeoutMs > 0 ? timeoutMs : derived, 1000, 300_000);
        return ch.RequestAsync(id => new EvalRequest { Id = id, Kind = "batch", Actions = steps, TimeoutMs = budget }, budget + 1500, CancellationToken.None);
    }

    private static int Clamp(int timeoutMs) => Math.Clamp(timeoutMs, 250, 120_000);

    private static string Bad(string error) => JsonSerializer.Serialize(new { ok = false, error }, Json);

    private static string NeedsInteraction() => JsonSerializer.Serialize(new
    {
        ok = false,
        needsInteraction = true,
        error = "call start_interaction first — it shows the user a visible overlay while you drive the page; call stop_interaction when done",
    }, Json);

    private static string NotRunning() =>
        JsonSerializer.Serialize(new { enabled = false, error = "ky-ai-browser capture not running" }, Json);
}
