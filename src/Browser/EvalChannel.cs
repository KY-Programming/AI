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
    private volatile bool _paused;
    private volatile bool _killed;
    private volatile bool _reloadReleased;

    // Duplicate-tab (fork) detection — see AdmitPoll. tabId lives in sessionStorage, which the browser
    // COPIES into a duplicated tab (right-click → Duplicate) and into window.open children, so two
    // physical tabs can boot sharing one tabId and both long-poll this one channel — letting two agents
    // think they hold separate tabs while driving the same page. These three track the current primary
    // page load so a fork can be told apart from a reload (succession) and split off.
    private string? _primaryPage;             // pageLoadId currently owning this channel
    private bool _primaryInFlight;            // a poll from _primaryPage is parked right now
    private DateTimeOffset _primaryReturnAt;  // when a _primaryPage poll last returned by window-elapse
    private static readonly TimeSpan ForkGrace = TimeSpan.FromSeconds(40);

    private readonly string _token;

    // One EvalChannel is one browser TAB's session (its own queue, waiters and interaction flags), so
    // N tabs of the same app never share a queue — PollAsync on this tab drains only this tab's work.
    // TabId is the stable per-tab key (sessionStorage on the page, survives reload/navigation); "" is the
    // legacy/single-tab sentinel used when a caller or an older snippet supplies no tab. Ownership +
    // lease are managed by the TabRegistry (mutated under its lock); they live here so /status can report
    // them per tab. CurrentPageLoadId tracks this tab's latest page load for console segmentation.
    public string TabId { get; }
    public string? OwnerAgentId { get; private set; }
    public DateTimeOffset LeaseExpiresAt { get; private set; }
    public bool LeaseValid => OwnerAgentId is not null && DateTimeOffset.UtcNow < LeaseExpiresAt;
    public string? CurrentPageLoadId { get; set; }

    public EvalChannel(string token, string? tabId = null)
    {
        _token = token;
        TabId = tabId ?? "";
    }

    // Bind this tab to an agent with a fresh lease. Called by the registry under its lock when an agent
    // claims a tab (start_interaction on a free tab, a granted waitlist tab, or a window.open claim).
    internal void Assign(string agentId, TimeSpan lease)
    {
        OwnerAgentId = agentId;
        LeaseExpiresAt = DateTimeOffset.UtcNow + lease;
    }

    // Slide the lease while the owning agent is still driving (renewed on each dispatch it makes and on
    // wait_for_resume). A crashed agent stops renewing, so the lease lapses and the tab frees itself —
    // fixing the old "InteractionActive sticks forever" wedge.
    internal void Renew(TimeSpan lease)
    {
        if (OwnerAgentId is not null) LeaseExpiresAt = DateTimeOffset.UtcNow + lease;
    }

    // Release ownership and close the session — the overlay auto-hides on the next reconcile. Called on
    // stop_interaction, lease expiry, or when a tab is handed to a waiting agent.
    internal void Unassign()
    {
        OwnerAgentId = null;
        LeaseExpiresAt = default;
        SetInteraction(false);
    }

    // Whether supervised interaction is open (start_interaction…stop_interaction). The manipulation
    // tools gate on this; the poll response echoes it so a reloaded page re-shows the overlay.
    public bool InteractionActive => _interactionActive;
    public void SetInteraction(bool active)
    {
        _interactionActive = active;
        // Opening a session is by definition a CLEAN one: a fresh start_interaction is how the human's
        // "ok, go ahead" (said in chat after a hard Stop, not clicked in the browser — there is no revive
        // control) turns into a new session, so it clears any lingering kill. It does NOT clear a pause —
        // that still requires the human's own paused-pill click (InstanceEval refuses a fresh
        // start_interaction outright while Paused, so this branch is never reached in that state).
        // It also re-arms the reload hold: a release is scoped to the session it was clicked in, so the
        // next session starts holding again rather than inheriting the last one's opt-out.
        if (active) { _killed = false; _reloadReleased = false; }
    }

    // While a session is open the page suppresses the Angular dev server's live-reload/HMR traffic, so a
    // rebuild (or a colleague's save) can't yank the page out from under the agent mid-test. This is
    // page-side interception of vite's HMR socket — the dev server still builds and ky-ai-ng still reports
    // the verdict; only the page's reaction is deferred.
    //
    // Derived, not stored: it follows the session automatically (start_interaction ⇒ hold,
    // stop_interaction ⇒ release), which also means a human Pause/Stop — both of which clear
    // _interactionActive — hands live-reload straight back while they're driving the tab themselves.
    // The human can also opt out for the CURRENT session alone by clicking the held-reload pill
    // (SetReloadReleased) without ending the agent's session; the next one re-arms (see SetInteraction).
    public bool HoldReload => _interactionActive && !_reloadReleased;

    // Whether the human clicked "continue reloading" for this session. Kept distinct from HoldReload
    // because the page reads it to decide whether releasing the hold should force a catch-up reload:
    // an automatic release (the agent finished) resyncs the page, but a human's explicit click means
    // "carry on as if it was never paused" — so it deliberately leaves the page as-is.
    public bool ReloadReleased => _reloadReleased;
    public void SetReloadReleased(bool released) => _reloadReleased = released;

    // Set when the human clicks the badge's Pause icon — a manual, resumable override for "I'm testing
    // this tab myself right now, just for a moment". Closes the gate immediately and, while true, also
    // refuses a fresh start_interaction: the agent must wait for the human to click the paused pill's
    // "resume" before it can drive the page again. Cleared only by that resume click (InstanceEval never
    // clears it). Reads (evaluate_js/query_dom/etc.) are NOT affected — only manipulation/batch/overlay.
    public bool Paused => _paused;
    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused) _interactionActive = false;
    }

    // Set when the human clicks the (harder) Stop icon on the badge or the paused pill — kills the whole
    // interaction session and removes all overlay UI: EVERY /eval kind is refused (including reads —
    // evaluate_js, query_dom, wait_for, get_styles, read_component — not just manipulation), and the
    // agent is told to stop entirely, not to wait or retry. There is deliberately no page-side "revive" —
    // resuming means the human tells the agent in chat, which then calls start_interaction for a clean
    // new session; THAT clears it (see SetInteraction), not this setter. Also drops any lingering pause
    // so the two states can't overlap.
    public bool Killed => _killed;
    public void SetKilled(bool killed)
    {
        _killed = killed;
        if (killed) { _interactionActive = false; _paused = false; }
    }

    // Block until the human clicks the paused pill's resume (Paused goes false) or timeoutMs elapses —
    // the same plain poll-loop shape as BuildTracker.WaitForSettleAsync, so wait_for_resume is an
    // ordinary blocking tool call instead of needing a separate notification channel. Returns
    // immediately if it wasn't paused to begin with, OR if the human has since killed the session
    // outright — a kill is stronger than a pause and is never worth waiting out (see Killed above).
    public async Task<bool> WaitForResumeAsync(int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (_paused && !_killed && DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(80, ct); } catch (OperationCanceledException) { break; }
        }
        return !_paused && !_killed;
    }

    // True when the capture snippet has long-polled recently — i.e. the app is open and listening.
    public bool PageConnected
    {
        get { lock (_sync) return DateTimeOffset.UtcNow - _lastPollAt < PageFreshFor; }
    }

    // When this tab last long-polled (MinValue if never) — the TabRegistry reads it to reap tabs whose
    // page went silent (closed) so a stale tab can't hold a lease forever.
    public DateTimeOffset LastPollAt { get { lock (_sync) return _lastPollAt; } }

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

    // Decide whether an arriving poll may run on this channel, or is a duplicate tab that must be split
    // off to its own tabId. The discriminator between a duplicate (fork) and a reload (succession) is
    // temporal OVERLAP: a reloaded page's old load unloads — its long-poll's socket ABORTS (reported via
    // NotePollEnded) — before the new load polls, so the two never overlap and the newcomer cleanly
    // succeeds the primary. A duplicate's original keeps polling (its polls return by window-elapse, never
    // abort) while the copy also polls, so a poll from a different pageLoadId arrives while the primary is
    // still alive ⇒ Fork. A legacy snippet that sends no pageLoadId opts out (always Proceed).
    public PollAdmit AdmitPoll(string? pageLoadId)
    {
        if (string.IsNullOrEmpty(pageLoadId)) return PollAdmit.Proceed;
        lock (_sync)
        {
            if (_primaryPage is null || string.Equals(_primaryPage, pageLoadId, StringComparison.Ordinal))
            {
                _primaryPage = pageLoadId;
                _primaryInFlight = true;
                return PollAdmit.Proceed;
            }
            var incumbentAlive = _primaryInFlight || DateTimeOffset.UtcNow - _primaryReturnAt < ForkGrace;
            if (incumbentAlive) return PollAdmit.Fork;
            // Incumbent's poll aborted (unloaded) or has been silent past the grace ⇒ this is a reload /
            // takeover of a gone page, not a second live tab. Succeed it as the new primary.
            _primaryPage = pageLoadId;
            _primaryInFlight = true;
            return PollAdmit.Proceed;
        }
    }

    // Report how a proceeding poll ended so fork detection knows whether the primary is still alive.
    // aborted ⇒ the client socket dropped (page unloaded/closed), ending the primary's lineage so the next
    // page load succeeds it; otherwise the window merely elapsed and the same page will re-poll.
    public void NotePollEnded(string? pageLoadId, bool aborted)
    {
        if (string.IsNullOrEmpty(pageLoadId)) return;
        lock (_sync)
        {
            if (!string.Equals(_primaryPage, pageLoadId, StringComparison.Ordinal)) return;
            _primaryInFlight = false;
            if (aborted) _primaryPage = null;
            else _primaryReturnAt = DateTimeOffset.UtcNow;
        }
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

// AdmitPoll's verdict: run this poll on the channel, or tell the page it's a duplicate that must adopt a
// fresh server-minted tabId (Fork).
internal enum PollAdmit { Proceed, Fork }

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

// One step of a `batch`: an Action (click|move|key|type|wait|scroll|focus|styles|query|eval|sleep) plus
// the same fields the matching single tool takes. Serialized camelCase to the page, where the snippet runs
// each step in order. Manipulation steps (click/move/key/type/scroll/focus) require open interaction.
// `sleep` (DurationMs, shared with move) is batch-only — it exists to pace a flow between steps, which
// has no meaning as a standalone tool call.
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
