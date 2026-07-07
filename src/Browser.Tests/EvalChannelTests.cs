using System;
using System.Threading;
using System.Threading.Tasks;
using KY.AI.Browser;
using Xunit;

namespace KY.AI.Browser.Tests;

// The half-duplex eval channel that backs evaluate_js / query_dom / reload_page: a request enqueued
// by an MCP tool is handed to the page via PollAsync and parked until the page POSTs its result back
// (Complete) — or the wait times out. Reload is special: it's completed at poll hand-off because the
// page navigates away and never posts a result.
public class EvalChannelTests
{
    [Fact]
    public async Task Request_is_delivered_by_poll_then_completed_by_result()
    {
        var ch = new EvalChannel("tok");
        var call = ch.RequestAsync(id => new EvalRequest { Id = id, Kind = "eval", Expression = "1+1" }, 5000, default);

        var req = Assert.Single(await ch.PollAsync(2000, default));
        Assert.Equal("eval", req.Kind);
        Assert.Equal("1+1", req.Expression);

        Assert.True(ch.Complete("tok", req.Id, "{\"ok\":true,\"value\":\"2\"}"));
        Assert.Equal("{\"ok\":true,\"value\":\"2\"}", await call);
    }

    [Fact]
    public async Task Poll_parks_until_a_request_is_enqueued()
    {
        var ch = new EvalChannel("tok");
        var poll = ch.PollAsync(2000, default);
        Assert.False(poll.IsCompleted);             // nothing queued yet → still waiting

        var call = ch.RequestAsync(id => new EvalRequest { Id = id, Kind = "eval", Expression = "x" }, 5000, default);
        var req = Assert.Single(await poll);        // enqueue wakes the parked poll
        ch.Complete("tok", req.Id, "{\"ok\":true}");
        await call;
    }

    [Fact]
    public async Task Reload_request_is_completed_at_poll_handoff()
    {
        var ch = new EvalChannel("tok");
        var call = ch.RequestAsync(id => new EvalRequest { Id = id, Kind = "reload" }, 5000, default);

        Assert.Equal("reload", Assert.Single(await ch.PollAsync(2000, default)).Kind);

        var result = await call;                    // completed by the poll, no result POST needed
        Assert.Contains("dispatched", result);
    }

    [Fact]
    public async Task Request_times_out_as_not_connected_when_nothing_polls()
    {
        var ch = new EvalChannel("tok");
        var result = await ch.RequestAsync(id => new EvalRequest { Id = id, Kind = "eval", Expression = "x" }, 300, default);

        Assert.Contains("\"timedOut\":true", result);
        Assert.Contains("\"pageConnected\":false", result);
    }

    [Fact]
    public async Task Complete_rejects_a_bad_token_or_unknown_id()
    {
        var ch = new EvalChannel("tok");
        var call = ch.RequestAsync(id => new EvalRequest { Id = id, Kind = "eval", Expression = "x" }, 2000, default);
        var id = Assert.Single(await ch.PollAsync(1000, default)).Id;

        Assert.False(ch.Complete("wrong-token", id, "{}"));   // foreign poster
        Assert.False(ch.Complete("tok", "does-not-exist", "{}"));
        Assert.True(ch.Complete("tok", id, "{\"ok\":true}"));
        await call;
    }

    [Fact]
    public async Task PageConnected_reflects_a_recent_poll()
    {
        var ch = new EvalChannel("tok");
        Assert.False(ch.PageConnected);
        await ch.PollAsync(1, default);             // a poll (even an empty one) marks the page connected
        Assert.True(ch.PageConnected);
    }

    [Fact]
    public void SetPaused_true_also_closes_the_interaction_gate()
    {
        var ch = new EvalChannel("tok");
        ch.SetInteraction(true);

        ch.SetPaused(true);
        Assert.True(ch.Paused);
        Assert.False(ch.InteractionActive);   // the human's Pause click always closes the gate too

        ch.SetPaused(false);
        Assert.False(ch.Paused);
        Assert.False(ch.InteractionActive);   // resuming clears the pause but doesn't reopen the gate itself
    }

    [Fact]
    public void SetKilled_true_also_closes_the_gate_and_clears_any_pause()
    {
        var ch = new EvalChannel("tok");
        ch.SetInteraction(true);
        ch.SetPaused(true);

        ch.SetKilled(true);
        Assert.True(ch.Killed);
        Assert.False(ch.InteractionActive);
        Assert.False(ch.Paused);   // a kill supersedes a pause — the two states never overlap
    }

    [Fact]
    public void SetInteraction_true_clears_a_kill_but_SetInteraction_false_does_not()
    {
        // There is no page-side "revive" for a kill — a fresh start_interaction (SetInteraction(true))
        // is what starts the clean new session that clears it, per the human telling the agent in chat.
        var ch = new EvalChannel("tok");
        ch.SetKilled(true);

        ch.SetInteraction(false);   // e.g. a stray stop_interaction — must NOT clear the kill
        Assert.True(ch.Killed);

        ch.SetInteraction(true);    // a fresh start_interaction — clears it
        Assert.False(ch.Killed);
        Assert.True(ch.InteractionActive);
    }

    [Fact]
    public async Task WaitForResumeAsync_returns_immediately_when_never_paused()
    {
        var ch = new EvalChannel("tok");
        var resumed = await ch.WaitForResumeAsync(5000, default);
        Assert.True(resumed);
    }

    [Fact]
    public async Task WaitForResumeAsync_unblocks_the_moment_the_user_resumes()
    {
        var ch = new EvalChannel("tok");
        ch.SetPaused(true);
        var wait = ch.WaitForResumeAsync(5000, default);
        Assert.False(wait.IsCompleted);   // still paused → still parked

        await Task.Delay(150);
        ch.SetPaused(false);       // the human clicks the paused pill

        Assert.True(await wait);
    }

    [Fact]
    public async Task WaitForResumeAsync_times_out_while_still_paused()
    {
        var ch = new EvalChannel("tok");
        ch.SetPaused(true);
        var resumed = await ch.WaitForResumeAsync(200, default);
        Assert.False(resumed);
        Assert.True(ch.Paused);    // still paused — only the timeout elapsed, nothing was cleared
    }

    [Fact]
    public async Task WaitForResumeAsync_never_blocks_on_a_kill_even_if_paused_too()
    {
        var ch = new EvalChannel("tok");
        ch.SetPaused(true);
        ch.SetKilled(true);   // a kill is stronger than a pause — never worth waiting out

        var resumed = await ch.WaitForResumeAsync(5000, default);   // must return fast, not park for 5s

        Assert.False(resumed);
    }

    [Fact]
    public async Task WaitForResumeAsync_unblocks_immediately_if_killed_mid_wait()
    {
        var ch = new EvalChannel("tok");
        ch.SetPaused(true);
        var wait = ch.WaitForResumeAsync(5000, default);
        Assert.False(wait.IsCompleted);

        await Task.Delay(150);
        ch.SetKilled(true);   // the human escalates from Pause to Stop mid-wait

        Assert.False(await wait);   // unblocks right away as false, not "resumed"
    }
}
