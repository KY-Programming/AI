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
}
