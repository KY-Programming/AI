using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KY.AI.Browser;
using Xunit;

namespace KY.AI.Browser.Tests;

// The per-tab, multi-agent control plane: one EvalChannel per tab, agent ownership leases, and the
// blocking start_interaction handoff (open a new tab / share this tab / auto-grant on free). These drive
// the registry the way Program.cs's /eval + /poll routes do — a start/stop/click is a DispatchAsync, and
// the "page" is simulated by PollAsync (fetch the queued request) + Complete (post its result).
public class TabRegistryTests
{
    private const string Tok = "tok";

    private static EvalRequest Overlay(bool show) => new() { Id = "", Kind = "overlay", Show = show, TimeoutMs = 1000 };
    private static EvalRequest Click() => new() { Id = "", Kind = "click", Selector = "x", TimeoutMs = 1000 };

    // Register a tab and mark it connected (a poll within the freshness window). Returns after the empty
    // long-poll window elapses.
    private static Task Connect(TabRegistry reg, string tab) => reg.PollAsync(tab, null, null, 1, default);

    // Play the page for one queued request on `tab`: fetch it via a poll and post an ok result, which
    // resolves the DispatchAsync that enqueued it.
    private static async Task Pump(TabRegistry reg, string tab)
    {
        var poll = await reg.PollAsync(tab, null, null, 2000, default);
        var req = Assert.Single(poll.Requests);
        Assert.True(reg.Complete(tab, Tok, req.Id, "{\"ok\":true,\"shown\":true}"));
    }

    // Deliver+complete overlay requests on `tab` until the parked start_interaction resolves. Used where a
    // claim/sweep poll may itself pick up the granted overlay (whichever poll wins, the session opens).
    private static async Task Finish(TabRegistry reg, string tab, Task<string> parked)
    {
        for (var i = 0; i < 50 && !parked.IsCompleted; i++)
        {
            var poll = await reg.PollAsync(tab, null, null, 200, default);
            foreach (var r in poll.Requests) reg.Complete(tab, Tok, r.Id, "{\"ok\":true,\"shown\":true}");
        }
    }

    // start_interaction on an explicit tab (already connected), completing the overlay show.
    private static async Task<string> OpenOn(TabRegistry reg, string agent, string tab)
    {
        var task = reg.DispatchAsync(Overlay(true), 2000, agent, tab);
        await Pump(reg, tab);
        return await task;
    }

    // stop_interaction on an explicit tab, completing the overlay hide (releases the lease).
    private static async Task StopOn(TabRegistry reg, string agent, string tab)
    {
        var task = reg.DispatchAsync(Overlay(false), 2000, agent, tab);
        await Pump(reg, tab);
        await task;
    }

    // ── per-tab queue isolation (the old single-queue non-determinism, fixed) ──

    [Fact]
    public async Task A_request_for_one_tab_is_never_delivered_to_another()
    {
        var reg = new TabRegistry(Tok);
        await Connect(reg, "A"); await Connect(reg, "B");
        await OpenOn(reg, "a1", "A"); await OpenOn(reg, "a2", "B");

        var click = reg.DispatchAsync(Click(), 2000, "a1", "A");   // work for tab A

        var pollB = await reg.PollAsync("B", null, null, 150, default);
        Assert.Empty(pollB.Requests);                              // B never sees A's work

        var pollA = await reg.PollAsync("A", null, null, 1000, default);
        Assert.Equal("click", Assert.Single(pollA.Requests).Kind);
        reg.Complete("A", Tok, pollA.Requests[0].Id, "{\"ok\":true}");
        await click;
    }

    [Fact]
    public async Task A_pause_on_one_tab_does_not_touch_another()
    {
        var reg = new TabRegistry(Tok);
        await Connect(reg, "A"); await Connect(reg, "B");
        await OpenOn(reg, "a1", "A"); await OpenOn(reg, "a2", "B");

        reg.SetPaused("A", true);

        Assert.Contains("\"paused\":true", await reg.DispatchAsync(Click(), 500, "a1", "A"));   // A refused

        var onB = reg.DispatchAsync(Click(), 2000, "a2", "B");     // B still drivable
        await Pump(reg, "B");
        Assert.DoesNotContain("paused", await onB);
    }

    // ── case 2: silent reuse — a freed tab is claimed by the next agent with no popup ──

    [Fact]
    public async Task A_freed_tab_is_silently_reused_by_the_next_agent()
    {
        var reg = new TabRegistry(Tok);
        await Connect(reg, "A");
        await OpenOn(reg, "a1", "A");
        await StopOn(reg, "a1", "A");                 // a1 done → A is unowned

        var task = reg.DispatchAsync(Overlay(true), 2000, "a2", null);   // no tab, no handoff expected
        await Pump(reg, "A");
        Assert.Contains("\"shown\":true", await task);

        // and a2 now drives A
        var click = reg.DispatchAsync(Click(), 2000, "a2", null);
        await Pump(reg, "A");
        Assert.DoesNotContain("needsInteraction", await click);
    }

    // ── the blocking handoff / waitlist ──

    [Fact]
    public async Task Second_agent_parks_and_the_owner_tab_is_shown_the_handoff_prompt()
    {
        var reg = new TabRegistry(Tok);
        await Connect(reg, "A");
        await OpenOn(reg, "a1", "A");

        var parked = reg.DispatchAsync(Overlay(true), 5000, "a2", null);
        Assert.False(parked.IsCompleted);            // no free tab → parked

        var poll = await reg.PollAsync("A", null, null, 1000, default);
        Assert.NotNull(poll.Handoff);                // a1's tab is asked to host the prompt
        Assert.Equal("agent 2", poll.Handoff!.AgentLabel);

        reg.DenyHandoff(poll.Handoff.Ticket);        // tidy up the parked call
        Assert.Contains("handoffDenied", await parked);
    }

    // case 1: share this tab — the waiter takes the SAME tab once the owner finishes
    [Fact]
    public async Task Share_grants_the_same_tab_to_the_waiter_when_the_owner_stops()
    {
        var reg = new TabRegistry(Tok);
        await Connect(reg, "A");
        await OpenOn(reg, "a1", "A");

        var parked = reg.DispatchAsync(Overlay(true), 5000, "a2", null);
        var ticket = (await reg.PollAsync("A", null, null, 1000, default)).Handoff!.Ticket;
        Assert.True(reg.ShareTab("A", ticket));

        await StopOn(reg, "a1", "A");                // frees A → granted to the pinned waiter a2
        await Pump(reg, "A");                        // a2's session opens on A
        Assert.Contains("\"shown\":true", await parked);
    }

    // case 2 while already parked: a tab freeing auto-grants to the head waiter, no human click
    [Fact]
    public async Task A_parked_agent_is_auto_granted_the_tab_when_the_owner_stops()
    {
        var reg = new TabRegistry(Tok);
        await Connect(reg, "A");
        await OpenOn(reg, "a1", "A");

        var parked = reg.DispatchAsync(Overlay(true), 5000, "a2", null);
        await reg.PollAsync("A", null, null, 200, default);   // let it register as parked

        await StopOn(reg, "a1", "A");                // no share/click — freeing alone promotes a2
        await Pump(reg, "A");
        Assert.Contains("\"shown\":true", await parked);
    }

    // parallel: the human opens a NEW tab, which presents its claim ticket and binds to the waiter
    [Fact]
    public async Task A_new_tab_presenting_the_claim_ticket_satisfies_the_waiter()
    {
        var reg = new TabRegistry(Tok);
        await Connect(reg, "A");
        await OpenOn(reg, "a1", "A");

        var parked = reg.DispatchAsync(Overlay(true), 5000, "a2", null);
        var ticket = (await reg.PollAsync("A", null, null, 1000, default)).Handoff!.Ticket;

        var pollB = await reg.PollAsync("B", ticket, null, 1, default);   // the new tab's first poll
        Assert.True(pollB.Claimed);
        foreach (var r in pollB.Requests) reg.Complete("B", Tok, r.Id, "{\"ok\":true,\"shown\":true}");

        await Finish(reg, "B", parked);              // a2's session opens on the new tab
        Assert.Contains("\"shown\":true", await parked);
    }

    [Fact]
    public async Task Handoff_times_out_when_nobody_acts()
    {
        var reg = new TabRegistry(Tok);
        await Connect(reg, "A");
        await OpenOn(reg, "a1", "A");

        var r = await reg.DispatchAsync(Overlay(true), 1000, "a2", null);   // short budget, no action
        Assert.Contains("handoffTimedOut", r);
    }

    // ── crashed-agent recovery: a lapsed lease frees the tab (and promotes a waiter) ──

    [Fact]
    public async Task A_lapsed_lease_frees_the_tab_and_promotes_a_waiter()
    {
        var reg = new TabRegistry(Tok, TimeSpan.FromMilliseconds(150));
        await Connect(reg, "A");
        await OpenOn(reg, "a1", "A");                // a1 owns A, but never renews (as if it crashed)

        var parked = reg.DispatchAsync(Overlay(true), 5000, "a2", null);
        await reg.PollAsync("A", null, null, 100, default);

        await Task.Delay(250);                       // a1's lease lapses
        var sweep = await reg.PollAsync("A", null, null, 1, default);   // a poll sweeps: lapsed + live ⇒ promote a2
        foreach (var r in sweep.Requests) reg.Complete("A", Tok, r.Id, "{\"ok\":true,\"shown\":true}");

        await Finish(reg, "A", parked);
        Assert.Contains("\"shown\":true", await parked);
    }

    // ── tab resolution ──

    [Fact]
    public async Task An_agent_holding_two_tabs_must_say_which_one()
    {
        var reg = new TabRegistry(Tok);
        await Connect(reg, "A"); await Connect(reg, "B");
        await OpenOn(reg, "a1", "A"); await OpenOn(reg, "a1", "B");   // one agent, two tabs

        Assert.Contains("ambiguousTab", await reg.DispatchAsync(Click(), 500, "a1", null));
        // naming the tab resolves it
        var onA = reg.DispatchAsync(Click(), 2000, "a1", "A");
        await Pump(reg, "A");
        Assert.DoesNotContain("ambiguousTab", await onA);
    }

    [Fact]
    public async Task Manipulation_without_an_owned_tab_asks_for_start_interaction()
    {
        var reg = new TabRegistry(Tok);
        await Connect(reg, "A");                      // a page is open, but this agent owns no tab
        Assert.Contains("needsInteraction", await reg.DispatchAsync(Click(), 500, "a2", null));
    }

    [Fact]
    public async Task Targeting_a_tab_another_agent_holds_is_refused()
    {
        var reg = new TabRegistry(Tok);
        await Connect(reg, "A");
        await OpenOn(reg, "a1", "A");
        // a2 explicitly targets a1's tab
        Assert.Contains("another agent", await reg.DispatchAsync(Overlay(true), 500, "a2", "A"));
    }

    // ── bridge liveness: idleness never loses a tab; disconnecting does (the first live smoke's finding) ──

    [Fact]
    public async Task An_idle_but_connected_agent_keeps_its_tab_past_the_lease()
    {
        var reg = new TabRegistry(Tok, TimeSpan.FromMilliseconds(150));
        reg.SetLiveAgents(new[] { "a1" });            // a1 has a live bridge (a real open session)
        await Connect(reg, "A");
        await OpenOn(reg, "a1", "A");

        await Task.Delay(300);                        // lease long lapsed — but a1 is idle, not gone
        await reg.PollAsync("A", null, null, 1, default);   // sweep runs

        // a2 must NOT get the tab: it parks (all tabs busy) instead of silently stealing.
        Assert.Contains("handoffTimedOut", await reg.DispatchAsync(Overlay(true), 1000, "a2", null));

        // and a1 still drives it
        var click = reg.DispatchAsync(Click(), 2000, "a1", null);
        await Pump(reg, "A");
        Assert.DoesNotContain("needsInteraction", await click);
    }

    [Fact]
    public async Task A_tab_claimed_before_the_first_liveness_sync_is_not_stolen_once_it_arrives()
    {
        // The exact live-smoke bug: on a freshly restarted instance the agent claims a tab BEFORE the
        // hub's bridge list has synced, then goes idle past the lease. When liveness arrives showing the
        // owner still connected, the tab must be recognized as held — not handed to a waiting agent.
        var reg = new TabRegistry(Tok, TimeSpan.FromMilliseconds(150));
        await Connect(reg, "A");
        await OpenOn(reg, "a1", "A");                  // claimed with NO liveness data yet
        await Task.Delay(300);                         // lease lapsed while liveness was still empty
        reg.SetLiveAgents(new[] { "a1" });             // first sync arrives: a1 is connected

        await reg.PollAsync("A", null, null, 1, default);   // sweep
        Assert.Contains("handoffTimedOut", await reg.DispatchAsync(Overlay(true), 800, "a2", null));
    }

    // ── duplicate-tab (fork) split: a copied sessionStorage tabId must not collide two live pages ──

    [Fact]
    public async Task A_duplicate_tab_polling_a_shared_id_is_split_to_a_new_tab()
    {
        var reg = new TabRegistry(Tok);
        var primary = reg.PollAsync("A", null, "p1", 400, default);   // original page, parked in-flight
        await Task.Delay(60);                                         // let it register as primary
        // A duplicate booted with A's copied sessionStorage id polls the same channel with its own load.
        var dup = await reg.PollAsync("A", null, "p2", 200, default);
        Assert.NotNull(dup.ReassignTabId);
        Assert.StartsWith("t-split-", dup.ReassignTabId);
        Assert.Empty(dup.Requests);
        await primary;                                               // original poll undisturbed
    }

    [Fact]
    public async Task A_reload_same_tab_with_a_new_pageload_is_not_split()
    {
        var reg = new TabRegistry(Tok);
        using var cts = new CancellationTokenSource();
        var p1 = reg.PollAsync("A", null, "p1", 5000, cts.Token);    // primary, parked
        await Task.Delay(60);
        cts.Cancel();                                               // page unloads → poll socket aborts
        await p1;                                                    // NotePollEnded(aborted) clears primary
        // The reloaded page keeps tabId "A" (sessionStorage) but a fresh pageLoadId — succession, not fork.
        var p2 = await reg.PollAsync("A", null, "p2", 100, default);
        Assert.Null(p2.ReassignTabId);
    }

    [Fact]
    public async Task A_legacy_poll_without_a_pageload_is_never_split()
    {
        var reg = new TabRegistry(Tok);
        var p1 = reg.PollAsync("A", null, null, 400, default);
        await Task.Delay(60);
        var p2 = await reg.PollAsync("A", null, null, 100, default);  // old snippet: no pageLoadId → opt out
        Assert.Null(p2.ReassignTabId);
        await p1;
    }

    [Fact]
    public async Task A_disconnected_agents_tab_frees_immediately_regardless_of_lease()
    {
        var reg = new TabRegistry(Tok, TimeSpan.FromMinutes(5));   // long lease — liveness must not wait for it
        reg.SetLiveAgents(new[] { "a1" });
        await Connect(reg, "A");
        await OpenOn(reg, "a1", "A");

        reg.SetLiveAgents(Array.Empty<string>());     // a1's session closed/crashed (bridge gone)

        var task = reg.DispatchAsync(Overlay(true), 2000, "a2", null);   // silent reuse, no prompt
        await Pump(reg, "A");
        Assert.Contains("\"shown\":true", await task);
    }

    [Fact]
    public async Task Start_interaction_reports_the_granted_tabId()
    {
        var reg = new TabRegistry(Tok);
        await Connect(reg, "A");
        var result = await OpenOn(reg, "a1", "A");
        Assert.Contains("\"tabId\":\"A\"", result);
    }

    [Fact]
    public async Task An_agent_cannot_drive_a_tab_another_agent_owns_by_naming_it()
    {
        var reg = new TabRegistry(Tok);
        await Connect(reg, "A");
        await OpenOn(reg, "a1", "A");
        // Naming a1's tab must not let a2 click in it (ownership beats an explicit tab argument).
        Assert.Contains("another agent", await reg.DispatchAsync(Click(), 500, "a2", "A"));
    }
}
