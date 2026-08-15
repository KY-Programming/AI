using System.Text.Json;

namespace KY.AI.Browser;

// The capture instance's per-tab control plane: it owns one EvalChannel per browser TAB (keyed by the
// snippet's stable tabId) and the machinery that lets several agents drive the same app at once, each in
// its own tab. `Capture.Eval` points at one of these.
//
// Why this exists: an EvalChannel is a single tab's session (queue + interaction flags). With one shared
// channel, two tabs long-polling the same app raced for each other's work and shared one set of flags —
// so agents couldn't run in parallel. This registry gives each tab its own channel (isolating the queue
// and the overlay state) and adds AGENT OWNERSHIP on top: a tab is leased to the agent that opened it, a
// second agent that wants in is offered a new tab (or a share) via the on-page overlay, and a crashed
// agent's lease expires so its tab frees itself.
//
// The server can't open a tab (there is no browser automation here — only an injected snippet that
// long-polls), so a new tab is only ever opened by a human clicking the overlay's "open a new tab"
// button inside a real user gesture. start_interaction therefore BLOCKS (parks the call) until the human
// acts or a tab frees; see DispatchAsync/OpenSessionAsync.
internal sealed class TabRegistry
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // A poll within this window ⇒ the tab is open and listening. A tab silent longer than this is treated
    // as closed and reaped (its lease released). Mirrors EvalChannel.PageFreshFor.
    private static readonly TimeSpan PageFreshFor = TimeSpan.FromSeconds(40);
    public static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(90);

    // Legacy/single-tab sentinel: a caller (older snippet, a direct test) that supplies no tabId lands
    // on one shared tab, so single-agent flows behave exactly as they did before tabs existed.
    public const string LegacyTab = "";
    // A plain MCP client that talks straight to the hub sends no agent header; it's one implicit agent.
    private const string SoloAgent = "(solo)";

    private readonly string _token;
    private readonly TimeSpan _lease;
    private readonly object _sync = new();
    private readonly Dictionary<string, EvalChannel> _tabs = new(StringComparer.Ordinal);
    private readonly List<Waiter> _waiters = new();                 // FIFO: agents parked in start_interaction
    private readonly Dictionary<string, int> _agentOrdinal = new(StringComparer.Ordinal);
    private int _nextAgentOrdinal = 1;
    private long _nextTicket;

    // The hub's live bridge ids (= agent ids), pushed by the instance's register loop every ~15s, plus the
    // set of ids we've EVER seen live. Together these tell a DISCONNECTED agent apart from a merely idle
    // one without latching anything at claim time (an early smoke bug: a tab claimed before the first
    // liveness sync latched "no bridge" forever and got stolen at the 90s lease despite a live session).
    // Membership is derived purely from the stream: once an id appears live it is disconnect-governed from
    // then on; an id that never appears (a header-only/curl caller) stays lease-governed.
    private HashSet<string> _liveAgents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _everLive = new(StringComparer.Ordinal);
    private DateTimeOffset _liveAgentsAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan LivenessFreshFor = TimeSpan.FromSeconds(60);

    public void SetLiveAgents(IEnumerable<string> ids)
    {
        lock (_sync)
        {
            _liveAgents = new HashSet<string>(ids, StringComparer.Ordinal);
            _liveAgentsAt = DateTimeOffset.UtcNow;
            foreach (var id in _liveAgents) _everLive.Add(id);
        }
    }

    private bool LivenessFresh => DateTimeOffset.UtcNow - _liveAgentsAt < LivenessFreshFor;   // callers hold _sync

    // THE ownership test — every "is this tab taken?" decision goes through here so the release rule in
    // Sweep and the claim/resolve/guard paths can never disagree. While liveness is fresh, a known bridge
    // agent (ever seen live) is owned iff still connected — idleness is irrelevant; a never-seen agent, or
    // any owner when the liveness feed is stale (hub unreachable), falls back to the sliding lease.
    private bool OwnerAlive(EvalChannel t)
    {
        if (t.OwnerAgentId is null) return false;
        if (LivenessFresh && _everLive.Contains(t.OwnerAgentId))
            return _liveAgents.Contains(t.OwnerAgentId);
        return t.LeaseValid;
    }

    private bool OwnerAliveLocked(EvalChannel t) { lock (_sync) return OwnerAlive(t); }   // for call sites outside _sync

    // lease overrides the sliding ownership lease (tests pass a short one to exercise expiry without waiting).
    public TabRegistry(string token, TimeSpan? lease = null)
    {
        _token = token;
        _lease = lease ?? DefaultLease;
    }

    // A parked start_interaction: an agent waiting for a tab because every connected tab is owned by
    // someone else. Resolved when a tab frees (auto-grant), when the human opens a new tab that presents
    // this Ticket, or when the human shares a specific tab. Denied/timed out → Grant.TabId is null.
    private sealed class Waiter
    {
        public required string Ticket { get; init; }
        public required string AgentId { get; init; }
        public string? PinnedTabId { get; set; }        // set by "share this tab": only that tab satisfies it
        public required TaskCompletionSource<Grant> Tcs { get; init; }
    }

    private sealed record Grant(string? TabId, string Reason);   // Reason: granted | denied | timeout

    // ── page-facing: poll / result (per tab) ──

    // The long-poll for one tab. Creates the tab's channel on first sight, binds a presented claim ticket
    // to its waiting agent, and returns that tab's queued work + its own interaction flags + any handoff
    // prompt this tab should show. `claim` is sent only on a freshly-opened tab's first poll.
    public async Task<TabPoll> PollAsync(string? tabId, string? claim, string? pageLoadId, int waitMs, CancellationToken ct)
    {
        var ch = GetOrCreate(tabId);

        // Duplicate-tab guard: a tab that booted with a COPIED sessionStorage id (right-click → Duplicate,
        // or an old-snippet window.open) polls this channel with a distinct pageLoadId while the real tab
        // is still alive. Detect that fork and hand the newcomer a fresh tabId to adopt, so the two stop
        // colliding on one channel. A ticketed handoff tab already got a fresh id at boot, so it won't fork.
        if (ch.AdmitPoll(pageLoadId) == PollAdmit.Fork)
        {
            string reassign;
            lock (_sync) reassign = MintTabId();
            return new TabPoll(Array.Empty<EvalRequest>(), false, false, false, false, ch.TabId, false, null, reassign);
        }

        if (!string.IsNullOrEmpty(pageLoadId)) ch.CurrentPageLoadId = pageLoadId;

        var claimed = false;
        if (!string.IsNullOrEmpty(claim))
            claimed = TryBindClaim(claim!, ch);

        Sweep();

        try
        {
            var reqs = await ch.PollAsync(waitMs, ct);
            var handoff = HandoffFor(ch);
            return new TabPoll(reqs, ch.InteractionActive, ch.Paused, ch.Killed, ch.HoldReload,
                ch.TabId, claimed, handoff, null);
        }
        finally
        {
            // ct is the request-abort token: cancelled ⇒ the page's socket dropped (unload/close), which
            // ends this page load's lineage so a reload can succeed it; otherwise the window just elapsed
            // and the same page re-polls. This is the reload-vs-duplicate signal AdmitPoll relies on.
            ch.NotePollEnded(pageLoadId, ct.IsCancellationRequested);
        }
    }

    // Mint a fresh, server-side tabId for a duplicate tab to adopt. Distinct prefix so it's obvious in
    // logs/status that the server re-keyed a collided tab rather than the page choosing the id.
    private string MintTabId() => "t-split-" + Guid.NewGuid().ToString("N").Substring(0, 12);

    public bool Complete(string? tabId, string? token, string? id, string? payload)
    {
        EvalChannel? ch;
        lock (_sync) _tabs.TryGetValue(tabId ?? LegacyTab, out ch);
        return ch is not null && ch.Complete(token, id, payload);
    }

    // ── agent-facing: dispatch an EvalRequest the hub forwarded (from /eval) ──

    public Task<string> DispatchAsync(EvalRequest req, int waitMs, string? agentId, string? tabId)
    {
        agentId = string.IsNullOrEmpty(agentId) ? SoloAgent : agentId!;
        Sweep();

        var isOverlay = string.Equals(req.Kind, "overlay", StringComparison.Ordinal);

        // start_interaction: claim a tab (own/reuse), else park for a handoff. Its own path.
        if (isOverlay && req.Show == true)
            return OpenSessionAsync(req, waitMs, agentId, string.IsNullOrEmpty(tabId) ? null : tabId);

        // Everything else acts on an already-resolved tab.
        var ch = ResolveForAction(agentId, string.IsNullOrEmpty(tabId) ? null : tabId, req, out var error);
        if (ch is null) return Task.FromResult(error!);

        // A manipulation (or a stop) may only touch a tab the caller owns — naming another agent's tab with
        // the tab argument must not let you drive or close their session. Reads on another tab are allowed.
        var mutates = isOverlay || InstanceEval.IsManipulationKind(req.Kind) || IsManipulationBatch(req);
        if (mutates && OwnerAliveLocked(ch) && !string.Equals(ch.OwnerAgentId, agentId, StringComparison.Ordinal))
            return Task.FromResult(TabBusy(ch.TabId));

        if (isOverlay && req.Show == false)
            return StopSessionAsync(ch, req, waitMs, agentId);

        // A manipulation/read from the owning agent slides the lease so an active session doesn't lapse.
        lock (_sync) if (string.Equals(ch.OwnerAgentId, agentId, StringComparison.Ordinal)) ch.Renew(_lease);
        return InstanceEval.DispatchAsync(ch, req, waitMs);
    }

    // start_interaction. First tries to satisfy the agent immediately (it already owns a tab, or an
    // unowned connected tab exists — the human's just-opened app, or a tab a prior agent finished with),
    // else parks on the waitlist until a tab frees / the human opens or shares one / it times out.
    private async Task<string> OpenSessionAsync(EvalRequest req, int waitMs, string agentId, string? explicitTabId)
    {
        Waiter? waiter = null;
        EvalChannel? target = null;

        lock (_sync)
        {
            Label(agentId);   // register the agent's ordinal on first interaction so "agent N" is arrival order

            // Explicit tab: honour it if it's the agent's own or is free; refuse if another agent holds it.
            if (explicitTabId is not null)
            {
                if (!_tabs.TryGetValue(explicitTabId, out var ex)) return UnknownTab(explicitTabId);
                if (OwnerAlive(ex) && !string.Equals(ex.OwnerAgentId, agentId, StringComparison.Ordinal))
                    return TabBusy(explicitTabId);
                if (ex.Paused) return InstanceEval.Paused();
                ex.Assign(agentId, _lease);
                target = ex;
            }
            else if (FindOwned(agentId) is { } mine)   // already driving a tab → reopen/renew it
            {
                if (mine.Paused) return InstanceEval.Paused();
                mine.Assign(agentId, _lease);
                target = mine;
            }
            else if (FindFreeConnected() is { } free)  // an unowned live tab → claim it, no popup (reuse)
            {
                if (free.Paused) return InstanceEval.Paused();
                free.Assign(agentId, _lease);
                target = free;
            }
            else if (!AnyConnectedLocked())             // no page at all → nothing can host the overlay
            {
                return NoPage();
            }
            else                                        // all tabs busy → park for a handoff
            {
                waiter = new Waiter
                {
                    Ticket = "c" + Interlocked.Increment(ref _nextTicket).ToString(),
                    AgentId = agentId,
                    Tcs = new TaskCompletionSource<Grant>(TaskCreationOptions.RunContinuationsAsynchronously),
                };
                _waiters.Add(waiter);
            }
        }

        if (target is not null)
            return WithTabId(await InstanceEval.DispatchAsync(target, req, waitMs), target.TabId);

        // Parked: wait for a grant, a new/ shared tab, a deny, or the budget to elapse.
        Grant grant;
        using (var timeout = new CancellationTokenSource())
        {
            timeout.CancelAfter(Math.Max(1000, waitMs));
            using var reg = timeout.Token.Register(() =>
            {
                lock (_sync) _waiters.Remove(waiter!);
                waiter!.Tcs.TrySetResult(new Grant(null, "timeout"));
            });
            grant = await waiter!.Tcs.Task;
        }

        if (grant.TabId is null)
            return grant.Reason == "denied" ? HandoffDenied() : HandoffTimedOut();

        EvalChannel granted;
        lock (_sync) _tabs.TryGetValue(grant.TabId, out granted!);
        if (granted is null) return HandoffTimedOut();
        return WithTabId(await InstanceEval.DispatchAsync(granted, req, waitMs), granted.TabId);
    }

    // Stamp the tab an overlay-show landed on into its result JSON ({ok, shown} → {ok, shown, tabId}) so
    // start_interaction tells the agent which tab it was given. The payload comes verbatim from the page;
    // on anything unparseable, pass it through untouched rather than risk mangling an error.
    private static string WithTabId(string resultJson, string tabId)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return resultJson;
            var map = new Dictionary<string, JsonElement>();
            foreach (var p in doc.RootElement.EnumerateObject()) map[p.Name] = p.Value.Clone();
            map["tabId"] = JsonSerializer.SerializeToElement(tabId);
            return JsonSerializer.Serialize(map, Json);
        }
        catch { return resultJson; }
    }

    // stop_interaction: hide the overlay, then release the lease so a waiting agent (or the next reuse)
    // can take the tab.
    private async Task<string> StopSessionAsync(EvalChannel ch, EvalRequest req, int waitMs, string agentId)
    {
        var result = await InstanceEval.DispatchAsync(ch, req, waitMs);
        // Only the owner releases it (a stray stop from a non-owner just hides its view).
        if (string.Equals(ch.OwnerAgentId, agentId, StringComparison.Ordinal))
            ReleaseAndPromote(ch);
        return result;
    }

    // ── human overrides (from the overlay), routed to one tab; kill can go instance-wide ──

    public bool SetPaused(string? tabId, bool paused)
    {
        var ch = Get(tabId);
        if (ch is null) return false;
        ch.SetPaused(paused);
        if (paused) lock (_sync) ch.Unassign();   // the human took the tab; drop the agent's lease
        return true;
    }

    public bool SetKilled(string? tabId, bool killed, bool scopeAll)
    {
        if (scopeAll)
        {
            lock (_sync)
                foreach (var t in _tabs.Values) { t.SetKilled(killed); if (killed) t.Unassign(); }
            return true;
        }
        var ch = Get(tabId);
        if (ch is null) return false;
        ch.SetKilled(killed);
        if (killed) lock (_sync) ch.Unassign();
        return true;
    }

    public bool SetReloadReleased(string? tabId)
    {
        var ch = Get(tabId);
        if (ch is null) return false;
        ch.SetReloadReleased(true);
        return true;
    }

    // Human clicked "share this tab" on the handoff prompt: pin the ticket's waiter to this tab so it is
    // granted the moment this tab's owner releases (rather than opening a new tab).
    public bool ShareTab(string? tabId, string ticket)
    {
        lock (_sync)
        {
            var w = _waiters.FirstOrDefault(x => x.Ticket == ticket);
            if (w is null) return false;
            w.PinnedTabId = tabId ?? LegacyTab;
            return true;
        }
    }

    public bool DenyHandoff(string ticket)
    {
        Waiter? w;
        lock (_sync)
        {
            w = _waiters.FirstOrDefault(x => x.Ticket == ticket);
            if (w is null) return false;
            _waiters.Remove(w);
        }
        w.Tcs.TrySetResult(new Grant(null, "denied"));
        return true;
    }

    // ── wait_for_resume (per the agent's tab) ──

    public async Task<(bool Cleared, EvalChannel? Ch)> WaitForResumeAsync(string? agentId, string? tabId, int timeoutMs, CancellationToken ct)
    {
        agentId = string.IsNullOrEmpty(agentId) ? SoloAgent : agentId!;
        var ch = ResolveForAction(agentId, string.IsNullOrEmpty(tabId) ? null : tabId, null, out _);
        if (ch is null) return (true, null);   // nothing to wait on
        lock (_sync) if (string.Equals(ch.OwnerAgentId, agentId, StringComparison.Ordinal)) ch.Renew(_lease);
        var cleared = await ch.WaitForResumeAsync(timeoutMs, ct);
        return (cleared, ch);
    }

    // ── status ──

    public bool AnyPageConnected { get { lock (_sync) return _tabs.Values.Any(t => t.PageConnected); } }

    public object StatusSnapshot()
    {
        lock (_sync)
        {
            var tabs = _tabs.Values.Where(t => t.TabId.Length > 0 || t.PageConnected || t.OwnerAgentId is not null)
                .Select(t => new
                {
                    tabId = t.TabId,
                    owner = t.OwnerAgentId is null ? null : Label(t.OwnerAgentId),
                    leaseExpiresInMs = t.LeaseValid ? (int)Math.Max(0, (t.LeaseExpiresAt - DateTimeOffset.UtcNow).TotalMilliseconds) : (int?)null,
                    pageConnected = t.PageConnected,
                    interactionActive = t.InteractionActive,
                    paused = t.Paused,
                    killed = t.Killed,
                    holdReload = t.HoldReload,
                    currentPageLoadId = t.CurrentPageLoadId,
                }).ToArray();
            return new
            {
                // Aggregates keep single-tab /status readers working; `tabs` is the per-tab breakdown.
                pageConnected = _tabs.Values.Any(t => t.PageConnected),
                interactionActive = _tabs.Values.Any(t => t.InteractionActive),
                paused = _tabs.Values.Any(t => t.Paused),
                killed = _tabs.Values.Any(t => t.Killed),
                holdReload = _tabs.Values.Any(t => t.HoldReload),
                tabCount = tabs.Length,
                tabs,
            };
        }
    }

    // ── internals ──

    private EvalChannel GetOrCreate(string? tabId)
    {
        var key = tabId ?? LegacyTab;
        lock (_sync)
        {
            if (!_tabs.TryGetValue(key, out var ch))
            {
                ch = new EvalChannel(_token, key);
                _tabs[key] = ch;
            }
            return ch;
        }
    }

    private EvalChannel? Get(string? tabId)
    {
        lock (_sync) { _tabs.TryGetValue(tabId ?? LegacyTab, out var ch); return ch; }
    }

    private bool TryBindClaim(string ticket, EvalChannel newTab)
    {
        Waiter? w;
        lock (_sync)
        {
            w = _waiters.FirstOrDefault(x => x.Ticket == ticket);
            if (w is null) return false;
            _waiters.Remove(w);
            newTab.Assign(w.AgentId, _lease);
        }
        w.Tcs.TrySetResult(new Grant(newTab.TabId, "granted"));   // wakes the parked start_interaction
        return true;
    }

    // Resolve the tab a manipulation/read acts on: an explicit tab, else the agent's own tab, else — for
    // reads only — the sole connected tab (so single-agent reads work before any start_interaction).
    private EvalChannel? ResolveForAction(string agentId, string? explicitTabId, EvalRequest? req, out string? error)
    {
        error = null;
        lock (_sync)
        {
            if (explicitTabId is not null)
            {
                if (_tabs.TryGetValue(explicitTabId, out var ex)) return ex;
                error = UnknownTab(explicitTabId);
                return null;
            }

            var owned = _tabs.Values.Where(t => OwnerAlive(t) && string.Equals(t.OwnerAgentId, agentId, StringComparison.Ordinal)).ToList();
            if (owned.Count == 1) return owned[0];
            if (owned.Count > 1) { error = AmbiguousTab(owned.Select(t => t.TabId)); return null; }

            var manipulation = req is not null && (InstanceEval.IsManipulationKind(req.Kind) || IsManipulationBatch(req));
            if (manipulation) { error = InstanceEval.NeedsInteraction(); return null; }

            var connected = _tabs.Values.Where(t => t.PageConnected).ToList();
            if (connected.Count == 1) return connected[0];
            if (connected.Count == 0)
            {
                // No live page: fall back to (or create) the legacy tab so a read still returns the
                // channel's not-connected timeout rather than a resolution error.
                if (!_tabs.TryGetValue(LegacyTab, out var legacy)) { legacy = new EvalChannel(_token, LegacyTab); _tabs[LegacyTab] = legacy; }
                return legacy;
            }
            error = AmbiguousTab(connected.Select(t => t.TabId));
            return null;
        }
    }

    private static bool IsManipulationBatch(EvalRequest req) =>
        string.Equals(req.Kind, "batch", StringComparison.Ordinal) && req.Actions is { } a && a.Any(s => s.IsManipulation);

    private EvalChannel? FindOwned(string agentId)   // caller holds _sync (or best-effort for status)
    {
        foreach (var t in _tabs.Values)
            if (OwnerAlive(t) && string.Equals(t.OwnerAgentId, agentId, StringComparison.Ordinal)) return t;
        return null;
    }

    private EvalChannel? FindFreeConnected()   // caller holds _sync
    {
        foreach (var t in _tabs.Values)
            if (t.PageConnected && !OwnerAlive(t) && !t.Paused) return t;
        return null;
    }

    private bool AnyConnectedLocked() => _tabs.Values.Any(t => t.PageConnected);

    // Release a tab and hand it straight to the first compatible waiter (FIFO; a share-pinned waiter only
    // takes its own tab). Only promotes to a LIVE tab — a closed/gone tab is no use to a waiter.
    private void ReleaseAndPromote(EvalChannel ch)
    {
        Waiter? promoted = null;
        lock (_sync)
        {
            ch.Unassign();
            if (ch.PageConnected)
            {
                promoted = _waiters.FirstOrDefault(w => w.PinnedTabId is null || w.PinnedTabId == ch.TabId);
                if (promoted is not null)
                {
                    _waiters.Remove(promoted);
                    ch.Assign(promoted.AgentId, _lease);
                }
            }
        }
        promoted?.Tcs.TrySetResult(new Grant(ch.TabId, "granted"));
    }

    // Release tabs whose owner is GONE and drop tabs whose page went silent (closed). Cheap; called
    // opportunistically from every poll and dispatch, so no background timer is needed.
    //
    // "Gone" depends on what we know about the owner:
    //  - a bridge-backed owner (a Claude session's `connect` process) is gone exactly when its id drops
    //    off the hub's live-bridge list — being QUIET (thinking, waiting for its human) never loses the
    //    tab, which the first live smoke proved matters: a 90s idle window robbed an active session.
    //  - a non-bridge owner (raw MCP client, header-only caller) has no liveness signal, so the sliding
    //    lease stays its only guard — same as before.
    //  - if the liveness list itself is stale (hub unreachable), everyone falls back to the lease rule
    //    rather than treating "no data" as "everyone disconnected".
    private void Sweep()
    {
        List<(Waiter, EvalChannel)> grants = new();
        lock (_sync)
        {
            foreach (var t in _tabs.Values.ToList())
            {
                var stale = DateTimeOffset.UtcNow - t.LastPollAt > PageFreshFor && t.LastPollAt != DateTimeOffset.MinValue;
                var ownerGone = t.OwnerAgentId is not null && !OwnerAlive(t);

                if (ownerGone && t.PageConnected)
                {
                    // Owner's session ended but the page is alive → free it, then hand it to a waiter (case 2).
                    t.Unassign();
                    var w = _waiters.FirstOrDefault(x => x.PinnedTabId is null || x.PinnedTabId == t.TabId);
                    if (w is not null) { _waiters.Remove(w); t.Assign(w.AgentId, _lease); grants.Add((w, t)); }
                }
                else if (ownerGone && !t.PageConnected)
                {
                    t.Unassign();
                }

                // Drop a long-silent, unowned tab so the registry doesn't accumulate dead tabs. Never drop
                // the legacy sentinel (it's the single-tab home) or a tab an agent still holds.
                if (stale && t.OwnerAgentId is null && t.TabId.Length > 0)
                    _tabs.Remove(t.TabId);
            }
        }
        foreach (var (w, ch) in grants) w.Tcs.TrySetResult(new Grant(ch.TabId, "granted"));
    }

    // The handoff prompt a given connected tab should show: the oldest still-parked waiter (any tab may
    // host the gesture). Null when this tab isn't a candidate or nobody is waiting.
    private HandoffInfo? HandoffFor(EvalChannel ch)
    {
        lock (_sync)
        {
            if (_waiters.Count == 0 || !ch.PageConnected) return null;
            // Don't nag the very tab that a share already pinned to this waiter — it's already committed.
            var w = _waiters.FirstOrDefault(x => x.PinnedTabId is null || x.PinnedTabId != ch.TabId);
            if (w is null) return null;
            return new HandoffInfo(w.Ticket, Label(w.AgentId));
        }
    }

    // A stable "agent N" label per agentId, numbered in arrival order. Callers hold _sync.
    private string Label(string agentId)
    {
        if (!_agentOrdinal.TryGetValue(agentId, out var n)) { n = _nextAgentOrdinal++; _agentOrdinal[agentId] = n; }
        return $"agent {n}";
    }

    // ── refusal payloads ──

    private static string UnknownTab(string tabId) => JsonSerializer.Serialize(new
    {
        ok = false,
        error = $"unknown tab '{tabId}' — it may have been closed. Call list to see live tabs, or omit tab to use your own.",
    }, Json);

    private static string TabBusy(string tabId) => JsonSerializer.Serialize(new
    {
        ok = false,
        error = $"tab '{tabId}' is being driven by another agent. Call start_interaction with no tab to get your own.",
    }, Json);

    private static string AmbiguousTab(IEnumerable<string> tabs) => JsonSerializer.Serialize(new
    {
        ok = false,
        ambiguousTab = true,
        error = "you are driving more than one tab — pass tab to say which one.",
        tabs = tabs.ToArray(),
    }, Json);

    private static string NoPage() => JsonSerializer.Serialize(new
    {
        ok = false,
        pageConnected = false,
        error = "no page is open — open the app in a browser so a tab exists to interact with.",
    }, Json);

    private static string HandoffTimedOut() => JsonSerializer.Serialize(new
    {
        ok = false,
        handoffTimedOut = true,
        error = "another agent is already driving this app and the user did not open a new tab in time. " +
                "Ask the user to open a tab for you (a prompt is waiting in their browser), or target an " +
                "existing tab with the tab argument — do not blindly retry.",
    }, Json);

    private static string HandoffDenied() => JsonSerializer.Serialize(new
    {
        ok = false,
        handoffDenied = true,
        error = "the user declined to open a new tab for you. Stop trying to drive this app in a separate " +
                "tab; ask them how they'd like to proceed.",
    }, Json);
}

// The per-tab poll payload the instance serializes back to the snippet. Field names for the four flags
// are unchanged from the old single-tab shape (the snippet reconciles on them verbatim); they now mean
// "this tab's". `claimed` acks a presented claim ticket; `handoff` asks this tab to show the prompt.
// ReassignTabId is set only when this poll came from a DUPLICATE tab colliding on a shared tabId: the
// snippet must overwrite its sessionStorage tabId with this value and re-poll under it (see the snippet's
// pollEvalOnce). Null on every normal poll.
internal sealed record TabPoll(
    IReadOnlyList<EvalRequest> Requests,
    bool InteractionActive,
    bool Paused,
    bool Killed,
    bool HoldReload,
    string TabId,
    bool Claimed,
    HandoffInfo? Handoff,
    string? ReassignTabId);

// The "another agent wants in" prompt payload (serialized camelCase → {ticket, agentLabel}, which the
// snippet reads). Typed rather than anonymous so the instance tests can assert on it.
internal sealed record HandoffInfo(string Ticket, string AgentLabel);
