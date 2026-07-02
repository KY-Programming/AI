using System.Text.Json;

namespace KY.AI.Browser;

// The half-duplex return channel that lets an agent run code IN the attached page. The page only
// ever POSTs to us (console events), so to push work the other way the capture snippet long-polls
// `/__kyai/eval/poll`; this channel hands it queued requests and parks the MCP call until the page
// POSTs the matching result back to `/__kyai/eval/result` (or the wait times out).
//
//   evaluate_js → kind "eval"   (run an expression, serialize the value)
//   query_dom   → kind "query"  (querySelector(All) + describe the elements)
//   reload_page → kind "reload" (the page navigates away; completed at hand-off, not by a result)
//   navigate    → kind "navigate" (drive the SPA router / History API; stays on the page, posts a result)
//   click       → kind "click"  (synthetic pointer/mouse sequence at a selector or coordinate)
//   move        → kind "move"   (a pointermove path with enter/leave bookkeeping over a duration)
//   send_key    → kind "key"    (synthetic keydown/keypress/keyup)
//   type_text   → kind "type"   (set a field's value + input/change so frameworks observe it)
//   wait_for    → kind "wait"   (poll until a selector/expression is ready, or time out)
//   scroll      → kind "scroll" (window or element scroll / scrollIntoView)
//   focus       → kind "focus"  (focus or blur an element)
//   get_styles  → kind "styles" (read computed style properties)
//   read_component → kind "component" (snapshot the Angular component's bound state, signals resolved)
//   batch       → kind "batch"  (run a sequence of the above in one page round-trip)
//   start/stop_interaction → kind "overlay" (show/hide the supervision overlay)
//
// Interaction is gated: the manipulation tools require InteractionActive (set by start_interaction)
// so the human always sees the supervision overlay while the agent drives the page. The flag is also
// echoed in every poll response so a reloaded page restores the overlay on its own.
//
// Token-guarded like the console ingest (a misroute tag, not a secret — the real boundary is the
// loopback bind). Everything is best-effort: a missing/asleep page surfaces as a timed-out verdict
// with `pageConnected:false`, never an exception.
internal sealed class EvalChannel
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan PageFreshFor = TimeSpan.FromSeconds(40);  // a poll within this window ⇒ connected

    private readonly object _sync = new();
    private readonly Queue<EvalRequest> _pending = new();
    private readonly Dictionary<string, TaskCompletionSource<string>> _waiters = new();
    private TaskCompletionSource<bool> _wake = NewWake();
    private long _nextId;
    private DateTimeOffset _lastPollAt = DateTimeOffset.MinValue;
    private volatile bool _interactionActive;

    private readonly string _token;

    public EvalChannel(string token) => _token = token;

    // Whether supervised interaction is open (start_interaction…stop_interaction). The manipulation
    // tools gate on this; the poll response echoes it so a reloaded page re-shows the overlay.
    public bool InteractionActive => _interactionActive;
    public void SetInteraction(bool active) => _interactionActive = active;

    // True when the capture snippet has long-polled recently — i.e. the app is open and listening.
    public bool PageConnected
    {
        get { lock (_sync) return DateTimeOffset.UtcNow - _lastPollAt < PageFreshFor; }
    }

    private static TaskCompletionSource<bool> NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Enqueue a request (id minted here) and await the page's result JSON. Returns a synthesized
    // verdict on timeout — distinguishing "page never picked it up" from "page ran it but was slow".
    public async Task<string> RequestAsync(Func<string, EvalRequest> build, int timeoutMs, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        var req = build(id);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_sync)
        {
            _pending.Enqueue(req);
            _waiters[id] = tcs;
            WakeNoLock();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Math.Max(250, timeoutMs));
        using var reg = timeoutCts.Token.Register(() =>
        {
            lock (_sync)
            {
                if (_waiters.Remove(id, out var w)) w.TrySetResult(TimeoutJson());
            }
        });

        return await tcs.Task;
    }

    private string TimeoutJson()
    {
        var connected = PageConnected;
        return JsonSerializer.Serialize(new
        {
            ok = false,
            timedOut = true,
            pageConnected = connected,
            error = connected
                ? "the page received the request but did not return a result in time (raise timeoutMs, or the expression may be hung)"
                : "no page is attached — open the app in a browser so the capture script can run (and check ky-ai-browser is still serving)",
        }, Json);
    }

    // Long-poll: hand the snippet whatever is queued, waiting up to waitMs for work to arrive.
    // A `reload` request is completed here (the page reloads and never posts a result); eval/query
    // requests stay parked until their result POST lands.
    public async Task<IReadOnlyList<EvalRequest>> PollAsync(int waitMs, CancellationToken ct)
    {
        lock (_sync) _lastPollAt = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            Task wake;
            lock (_sync)
            {
                if (_pending.Count > 0)
                {
                    var batch = new List<EvalRequest>(_pending);
                    _pending.Clear();
                    foreach (var r in batch)
                        if (string.Equals(r.Kind, "reload", StringComparison.Ordinal) && _waiters.Remove(r.Id, out var w))
                            w.TrySetResult(JsonSerializer.Serialize(new { ok = true, dispatched = true, action = "reload" }, Json));
                    return batch;
                }
                wake = _wake.Task;
            }

            try
            {
                var finished = await Task.WhenAny(wake, Task.Delay(waitMs, ct));
                if (finished != wake) return Array.Empty<EvalRequest>();  // window elapsed → page re-polls
            }
            catch (OperationCanceledException) { break; }
        }
        return Array.Empty<EvalRequest>();
    }

    // The page posts a result for a parked eval/query request. `payloadJson` is the raw result
    // object the agent should see ({ok, type, value} or {ok:false, error, stack}). Returns false on
    // token mismatch or an unknown/already-completed id.
    public bool Complete(string? token, string? id, string? payloadJson)
    {
        if (!string.Equals(token, _token, StringComparison.Ordinal)) return false;
        if (string.IsNullOrEmpty(id)) return false;
        lock (_sync)
        {
            if (!_waiters.Remove(id, out var w)) return false;
            w.TrySetResult(string.IsNullOrEmpty(payloadJson) ? "{\"ok\":false,\"error\":\"empty result\"}" : payloadJson);
            return true;
        }
    }

    // Replace the wake signal and fire the old one so any parked poll drains immediately.
    private void WakeNoLock()
    {
        var old = _wake;
        _wake = NewWake();
        old.TrySetResult(true);
    }
}

// One unit of work the capture snippet pulls and runs, by Kind. Serialized camelCase on the wire
// with null fields omitted; each handler reads only the fields its Kind needs. Coordinates are
// nullable because 0 is a valid viewport position (omission must differ from the origin).
//   eval   — Expression [, AwaitPromise]
//   query  — Selector [, All, Limit]
//   reload — (none)
//   navigate — Path [, Replace]
//   eval   — Expression [, AwaitPromise, AsJson]
//   click  — Selector | Text(+Within,Exact) | (X, Y) [, Button, Ctrl/Shift/Alt/Meta]
//   move   — (ToX, ToY) [, FromX, FromY, DurationMs, Steps]
//   key    — Key [, Code, Selector, Ctrl/Shift/Alt/Meta]
//   type   — Selector, Text [, Append]
//   wait   — Selector | Expression [, PollMs]   (polls up to TimeoutMs)
//   scroll — Selector [, X, Y]  |  (X, Y) for the window
//   focus  — Selector [, Blur]
//   styles — Selector [, Props]
//   component — Selector (read the Angular component's bound state on/above the element)
//   batch  — Actions (a list of steps run in order, in one round-trip)
//   overlay— Show (true ⇒ show the supervision overlay, false ⇒ hide it)
internal sealed record EvalRequest
{
    public required string Id { get; init; }
    public required string Kind { get; init; }

    // eval / wait
    public string? Expression { get; init; }
    public bool AwaitPromise { get; init; }
    public bool AsJson { get; init; }            // eval: return real structured JSON, not a string rendering

    // navigate — the target route/URL, and whether to replaceState (no new history entry) in the fallback
    public string? Path { get; init; }
    public bool Replace { get; init; }

    // query / click / key / type / wait / scroll / focus / styles / component — the target element
    public string? Selector { get; init; }
    public bool All { get; init; }
    public int Limit { get; init; } = 20;

    // describe-element verbosity: false (default) ⇒ minimal target {tag, id?, text}; true ⇒ add
    // classes/attributes/rect/outerHTML. query_dom passes true (inspection); the interaction tools
    // leave it false so confirmations stay cheap on multi-step flows.
    public bool Detail { get; init; }

    // click — target by visible text (reuses Text) instead of selector/coordinate
    public string? Within { get; init; }         // restrict text search to this container
    public bool? Exact { get; init; }            // text match: exact (default) vs contains

    // batch — the steps to run in order
    public IReadOnlyList<BatchStep>? Actions { get; init; }

    // click / scroll — a viewport point (CSS px)
    public int? X { get; init; }
    public int? Y { get; init; }
    public string? Button { get; init; }   // click: left | middle | right

    // move — a path A→B over a duration
    public int? FromX { get; init; }
    public int? FromY { get; init; }
    public int? ToX { get; init; }
    public int? ToY { get; init; }
    public int? DurationMs { get; init; }
    public int? Steps { get; init; }

    // key — a single key plus modifiers
    public string? Key { get; init; }
    public string? Code { get; init; }
    public bool Ctrl { get; init; }
    public bool Shift { get; init; }
    public bool Alt { get; init; }
    public bool Meta { get; init; }

    // type
    public string? Text { get; init; }
    public bool Append { get; init; }

    // wait
    public int? PollMs { get; init; }

    // focus
    public bool Blur { get; init; }

    // overlay — show (true) or hide (false) the supervision overlay
    public bool? Show { get; init; }

    // styles — computed-style property names (kebab-case); empty ⇒ a default set
    public IReadOnlyList<string>? Props { get; init; }

    // component — restrict serialized state to these field names (others list name only); nesting cap
    public IReadOnlyList<string>? Fields { get; init; }
    public int? Depth { get; init; }

    // advisory page-side budget (wait uses it as its poll deadline)
    public int TimeoutMs { get; init; } = 5000;
}

// One step of a `batch`: an Action (click|move|key|type|wait|scroll|focus|styles|query|eval) plus the
// same fields the matching single tool takes. Serialized camelCase to the page, where the snippet runs
// each step in order. Manipulation steps (click/move/key/type/scroll/focus) require open interaction.
public sealed record BatchStep
{
    public required string Action { get; init; }

    public string? Selector { get; init; }
    public string? Text { get; init; }
    public string? Within { get; init; }
    public bool? Exact { get; init; }
    public bool All { get; init; }
    public bool Detail { get; init; }

    public int? X { get; init; }
    public int? Y { get; init; }
    public string? Button { get; init; }

    public int? FromX { get; init; }
    public int? FromY { get; init; }
    public int? ToX { get; init; }
    public int? ToY { get; init; }
    public int? DurationMs { get; init; }
    public int? Steps { get; init; }

    public string? Key { get; init; }
    public string? Code { get; init; }
    public bool Ctrl { get; init; }
    public bool Shift { get; init; }
    public bool Alt { get; init; }
    public bool Meta { get; init; }

    public bool Append { get; init; }
    public string? Expression { get; init; }
    public bool AwaitPromise { get; init; }
    public bool AsJson { get; init; }
    public int? PollMs { get; init; }
    public IReadOnlyList<string>? Props { get; init; }
    public IReadOnlyList<string>? Fields { get; init; }   // component: serialize only these state fields in full
    public int? Depth { get; init; }                      // component: nesting cap for serialized values
    public int? TimeoutMs { get; init; }

    // Manipulation steps are gated behind start_interaction; reads (wait/query/styles/eval) are not.
    public bool IsManipulation => Action is "click" or "move" or "key" or "type" or "scroll" or "focus";
}
