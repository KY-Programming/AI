using System;
using System.Threading;
using System.Threading.Tasks;
using KY.AI.Browser;
using Xunit;

namespace KY.AI.Browser.Tests;

// The MCP tools run in the HUB and forward each call to a capture instance; the instance (InstanceEval)
// gates supervised interaction and enqueues the EvalRequest on its channel. These tests wire the hub's
// forward seam (BrowserTools.ForwardHook) straight into a real EvalChannel via InstanceEval — modelling
// hub→instance without HTTP — then pull the request the snippet would receive and assert kind + fields.
// BrowserTools.ForwardHook is process static, so these live in one class (xUnit runs a class's tests
// sequentially) and each test restores the hook.
public class BrowserToolsTests
{
    // Route the tool's forward through InstanceEval onto `ch`, invoke it, return the EvalRequest the
    // channel handed the snippet (completing the call so it doesn't dangle). The channel is interaction-
    // open so the gated manipulation tools are allowed through.
    private static async Task<EvalRequest> Enqueued(Func<Task<string>> call)
    {
        var ch = new EvalChannel("t");
        ch.SetInteraction(true);
        BrowserTools.ForwardHook = (_, waitMs, req, _tab) => InstanceEval.DispatchAsync(ch, req, waitMs);
        try
        {
            var task = call();
            var req = Assert.Single(await ch.PollAsync(1000, default));
            ch.Complete("t", req.Id, "{\"ok\":true}");
            await task;
            return req;
        }
        finally { BrowserTools.ForwardHook = null; }
    }

    [Fact]
    public async Task Click_by_selector_builds_a_click_request()
    {
        var r = await Enqueued(() => BrowserTools.Click(selector: "button.go"));
        Assert.Equal("click", r.Kind);
        Assert.Equal("button.go", r.Selector);
    }

    [Fact]
    public async Task Click_by_point_carries_coordinates_and_button()
    {
        var r = await Enqueued(() => BrowserTools.Click(x: 12, y: 34, button: "right"));
        Assert.Equal("click", r.Kind);
        Assert.Equal(12, r.X);
        Assert.Equal(34, r.Y);
        Assert.Equal("right", r.Button);
    }

    [Fact]
    public async Task Move_carries_the_path_and_duration()
    {
        var r = await Enqueued(() => BrowserTools.Move(toX: 100, toY: 200, fromX: 0, fromY: 0, durationMs: 250));
        Assert.Equal("move", r.Kind);
        Assert.Equal(0, r.FromX);
        Assert.Equal(100, r.ToX);
        Assert.Equal(200, r.ToY);
        Assert.Equal(250, r.DurationMs);
    }

    [Fact]
    public async Task Send_key_carries_key_and_modifiers()
    {
        var r = await Enqueued(() => BrowserTools.SendKey(key: "Enter", ctrl: true));
        Assert.Equal("key", r.Kind);
        Assert.Equal("Enter", r.Key);
        Assert.True(r.Ctrl);
    }

    [Fact]
    public async Task Type_text_builds_a_type_request()
    {
        var r = await Enqueued(() => BrowserTools.TypeText(selector: "#name", text: "Ada", append: true));
        Assert.Equal("type", r.Kind);
        Assert.Equal("#name", r.Selector);
        Assert.Equal("Ada", r.Text);
        Assert.True(r.Append);
    }

    [Fact]
    public async Task Wait_for_selector_builds_a_wait_request()
    {
        var r = await Enqueued(() => BrowserTools.WaitFor(selector: "app-wire", timeoutMs: 1500, pollMs: 50));
        Assert.Equal("wait", r.Kind);
        Assert.Equal("app-wire", r.Selector);
        Assert.Equal(1500, r.TimeoutMs);
        Assert.Equal(50, r.PollMs);
    }

    [Fact]
    public async Task Scroll_focus_and_styles_map_to_their_kinds()
    {
        Assert.Equal("scroll", (await Enqueued(() => BrowserTools.Scroll(selector: ".panel"))).Kind);
        Assert.Equal("focus", (await Enqueued(() => BrowserTools.Focus(selector: "#in"))).Kind);

        var styles = await Enqueued(() => BrowserTools.GetStyles(selector: "app-wire", props: new[] { "transform", "stroke" }));
        Assert.Equal("styles", styles.Kind);
        Assert.Equal(new[] { "transform", "stroke" }, styles.Props);
    }

    [Fact]
    public async Task Click_by_text_carries_text_within_and_exact()
    {
        var r = await Enqueued(() => BrowserTools.Click(text: "Zwei", within: "m-dropdown", exact: false));
        Assert.Equal("click", r.Kind);
        Assert.Equal("Zwei", r.Text);
        Assert.Equal("m-dropdown", r.Within);
        Assert.Equal(false, r.Exact);
    }

    [Fact]
    public async Task Click_target_is_minimal_by_default_and_detailed_on_request()
    {
        Assert.False((await Enqueued(() => BrowserTools.Click(selector: "button.go"))).Detail);
        Assert.True((await Enqueued(() => BrowserTools.Click(selector: "button.go", detail: true))).Detail);
    }

    [Fact]
    public async Task Query_dom_defaults_to_detailed_and_can_be_slimmed()
    {
        Assert.True((await Enqueued(() => BrowserTools.QueryDom(selector: "a"))).Detail);              // inspection → detailed
        Assert.False((await Enqueued(() => BrowserTools.QueryDom(selector: "a", detail: false))).Detail);
    }

    [Fact]
    public async Task Read_component_builds_a_component_request()
    {
        var r = await Enqueued(() => BrowserTools.ReadComponent(selector: "m-dropdown"));
        Assert.Equal("component", r.Kind);
        Assert.Equal("m-dropdown", r.Selector);
    }

    [Fact]
    public async Task Read_component_carries_fields_and_depth()
    {
        var r = await Enqueued(() => BrowserTools.ReadComponent(selector: "m-dropdown", fields: new[] { "value", "text" }, depth: 5));
        Assert.Equal("component", r.Kind);
        Assert.Equal(new[] { "value", "text" }, r.Fields);
        Assert.Equal(5, r.Depth);
    }

    [Fact]
    public async Task Evaluate_js_json_flag_sets_asJson()
    {
        var r = await Enqueued(() => BrowserTools.EvaluateJs("1+1", json: true));
        Assert.Equal("eval", r.Kind);
        Assert.True(r.AsJson);
    }

    // ── batch ──

    [Fact]
    public async Task Batch_builds_a_batch_request_with_ordered_steps()
    {
        var steps = new[]
        {
            new BatchStep { Action = "click", Selector = ".menu" },
            new BatchStep { Action = "wait", Selector = ".item" },
            new BatchStep { Action = "click", Text = "Zwei" },
        };
        var r = await Enqueued(() => BrowserTools.Batch(steps));
        Assert.Equal("batch", r.Kind);
        Assert.Equal(3, r.Actions!.Count);
        Assert.Equal("wait", r.Actions[1].Action);
        Assert.Equal("Zwei", r.Actions[2].Text);
    }

    [Fact]
    public async Task Batch_forwards_a_sleep_step_with_its_duration()
    {
        var steps = new[]
        {
            new BatchStep { Action = "click", Selector = ".menu" },
            new BatchStep { Action = "sleep", DurationMs = 500 },
            new BatchStep { Action = "click", Text = "Zwei" },
        };
        var r = await Enqueued(() => BrowserTools.Batch(steps));
        Assert.Equal("sleep", r.Actions![1].Action);
        Assert.Equal(500, r.Actions[1].DurationMs);
    }

    [Fact]
    public async Task Batch_budget_covers_its_sleeps()
    {
        // A sleep must widen the derived budget, or a long pause would time the batch out mid-flow —
        // the failure it's most likely to cause and the least obvious to diagnose.
        var slept = await Enqueued(() => BrowserTools.Batch(new[]
        {
            new BatchStep { Action = "sleep", DurationMs = 20_000 },
        }));
        Assert.True(slept.TimeoutMs > 20_000, $"budget {slept.TimeoutMs} must exceed the 20s sleep it contains");
    }

    [Fact]
    public async Task A_batch_of_sleeps_alone_needs_no_interaction()
    {
        // sleep touches nothing, so it must not be gated like a manipulation step.
        var ch = new EvalChannel("t");   // interaction NOT opened
        BrowserTools.ForwardHook = (_, waitMs, req, _tab) => InstanceEval.DispatchAsync(ch, req, waitMs);
        try
        {
            var task = BrowserTools.Batch(new[] { new BatchStep { Action = "sleep", DurationMs = 10 } });
            var req = Assert.Single(await ch.PollAsync(1000, default));
            Assert.Equal("batch", req.Kind);
            ch.Complete("t", req.Id, "{\"ok\":true}");
            await task;
        }
        finally { BrowserTools.ForwardHook = null; }
    }

    [Fact]
    public async Task Batch_with_a_manipulation_step_is_gated()
    {
        var ch = new EvalChannel("t");   // interaction NOT opened
        BrowserTools.ForwardHook = (_, waitMs, req, _tab) => InstanceEval.DispatchAsync(ch, req, waitMs);
        try
        {
            var blocked = await BrowserTools.Batch(new[] { new BatchStep { Action = "click", Selector = "x" } });
            Assert.Contains("needsInteraction", blocked);
            Assert.Empty(await ch.PollAsync(50, default));
        }
        finally { BrowserTools.ForwardHook = null; }
    }

    [Fact]
    public async Task Batch_of_reads_only_runs_without_interaction()
    {
        var ch = new EvalChannel("t");   // interaction NOT opened
        BrowserTools.ForwardHook = (_, waitMs, req, _tab) => InstanceEval.DispatchAsync(ch, req, waitMs);
        try
        {
            var task = BrowserTools.Batch(new[] { new BatchStep { Action = "query", Selector = "a" } });
            var req = Assert.Single(await ch.PollAsync(1000, default));
            Assert.Equal("batch", req.Kind);
            ch.Complete("t", req.Id, "{\"ok\":true}");
            await task;
        }
        finally { BrowserTools.ForwardHook = null; }
    }

    [Fact]
    public async Task Click_without_a_target_is_rejected_without_forwarding()
    {
        var ch = new EvalChannel("t");
        ch.SetInteraction(true);
        BrowserTools.ForwardHook = (_, waitMs, req, _tab) => InstanceEval.DispatchAsync(ch, req, waitMs);
        try
        {
            var json = await BrowserTools.Click();   // no selector, text, or coordinates
            Assert.Contains("requires a selector", json);
            Assert.Empty(await ch.PollAsync(50, default));   // nothing was forwarded/queued
        }
        finally { BrowserTools.ForwardHook = null; }
    }

    [Fact]
    public async Task Tools_report_not_running_when_capture_is_off()
    {
        // No capture instance behind the hub → the instance dispatcher reports capture off.
        BrowserTools.ForwardHook = (_, waitMs, req, _tab) => InstanceEval.DispatchAsync(null, req, waitMs);
        try
        {
            Assert.Contains("\"enabled\":false", await BrowserTools.Click(selector: "x"));
            Assert.Contains("\"enabled\":false", await BrowserTools.WaitFor(selector: "x"));
        }
        finally { BrowserTools.ForwardHook = null; }
    }

    // ── supervised-interaction gate (enforced instance-side by InstanceEval) ──

    [Fact]
    public async Task Manipulation_is_blocked_until_start_interaction()
    {
        var ch = new EvalChannel("t");   // interaction NOT opened
        BrowserTools.ForwardHook = (_, waitMs, req, _tab) => InstanceEval.DispatchAsync(ch, req, waitMs);
        try
        {
            var blocked = await BrowserTools.Click(selector: "button.go");
            Assert.Contains("needsInteraction", blocked);
            Assert.Contains("start_interaction", blocked);
            Assert.Empty(await ch.PollAsync(50, default));   // nothing was queued
        }
        finally { BrowserTools.ForwardHook = null; }
    }

    [Fact]
    public async Task Start_interaction_opens_the_gate_and_shows_the_overlay()
    {
        var ch = new EvalChannel("t");
        BrowserTools.ForwardHook = (_, waitMs, req, _tab) => InstanceEval.DispatchAsync(ch, req, waitMs);
        try
        {
            var task = BrowserTools.StartInteraction();
            var req = Assert.Single(await ch.PollAsync(1000, default));
            Assert.Equal("overlay", req.Kind);
            Assert.True(req.Show);
            ch.Complete("t", req.Id, "{\"ok\":true,\"shown\":true}");
            await task;

            Assert.True(ch.InteractionActive);   // gate is now open

            // and a click now goes through
            var clickTask = BrowserTools.Click(selector: "button.go");
            var click = Assert.Single(await ch.PollAsync(1000, default));
            Assert.Equal("click", click.Kind);
            ch.Complete("t", click.Id, "{\"ok\":true}");
            await clickTask;
        }
        finally { BrowserTools.ForwardHook = null; }
    }

    [Fact]
    public async Task Stop_interaction_closes_the_gate_and_hides_the_overlay()
    {
        var ch = new EvalChannel("t");
        ch.SetInteraction(true);
        BrowserTools.ForwardHook = (_, waitMs, req, _tab) => InstanceEval.DispatchAsync(ch, req, waitMs);
        try
        {
            var task = BrowserTools.StopInteraction();
            var req = Assert.Single(await ch.PollAsync(1000, default));
            Assert.Equal("overlay", req.Kind);
            Assert.False(req.Show);
            ch.Complete("t", req.Id, "{\"ok\":true,\"shown\":false}");
            await task;

            Assert.False(ch.InteractionActive);
            Assert.Contains("needsInteraction", await BrowserTools.Click(selector: "x"));   // blocked again
        }
        finally { BrowserTools.ForwardHook = null; }
    }

    // ── user Pause override (the badge's own Pause/resume, not start/stop_interaction) ──

    [Fact]
    public async Task User_pause_closes_the_gate_and_refuses_even_a_fresh_start_interaction()
    {
        var ch = new EvalChannel("t");
        ch.SetInteraction(true);
        ch.SetPaused(true);   // the human clicked Pause on the badge
        BrowserTools.ForwardHook = (_, waitMs, req, _tab) => InstanceEval.DispatchAsync(ch, req, waitMs);
        try
        {
            Assert.False(ch.InteractionActive);   // pausing closed the gate immediately

            var clickBlocked = await BrowserTools.Click(selector: "x");
            Assert.Contains("\"paused\":true", clickBlocked);

            var startBlocked = await BrowserTools.StartInteraction();
            Assert.Contains("\"paused\":true", startBlocked);
            Assert.False(ch.InteractionActive);   // start_interaction did NOT reopen the gate

            Assert.Empty(await ch.PollAsync(50, default));   // nothing was ever queued to the page
        }
        finally { BrowserTools.ForwardHook = null; }
    }

    [Fact]
    public async Task Resuming_after_a_user_pause_lets_start_interaction_succeed_again()
    {
        var ch = new EvalChannel("t");
        ch.SetPaused(true);
        ch.SetPaused(false);   // the human clicked "resume" on the paused pill
        BrowserTools.ForwardHook = (_, waitMs, req, _tab) => InstanceEval.DispatchAsync(ch, req, waitMs);
        try
        {
            var task = BrowserTools.StartInteraction();
            var req = Assert.Single(await ch.PollAsync(1000, default));
            Assert.Equal("overlay", req.Kind);
            Assert.True(req.Show);
            ch.Complete("t", req.Id, "{\"ok\":true,\"shown\":true}");
            await task;

            Assert.True(ch.InteractionActive);
        }
        finally { BrowserTools.ForwardHook = null; }
    }

    // ── user Kill override (a Stop icon, badge or paused pill) — blocks EVERYTHING, reads included,
    //    EXCEPT a fresh start_interaction: that's the one path back, and it clears the kill itself. ──

    [Fact]
    public async Task User_kill_refuses_reads_and_manipulation_but_not_a_fresh_start_interaction()
    {
        var ch = new EvalChannel("t");
        ch.SetInteraction(true);
        ch.SetKilled(true);   // the human clicked a Stop icon
        BrowserTools.ForwardHook = (_, waitMs, req, _tab) => InstanceEval.DispatchAsync(ch, req, waitMs);
        try
        {
            Assert.False(ch.InteractionActive);

            var readBlocked = await BrowserTools.EvaluateJs("1+1");
            Assert.Contains("\"killed\":true", readBlocked);

            var clickBlocked = await BrowserTools.Click(selector: "x");
            Assert.Contains("\"killed\":true", clickBlocked);

            Assert.Empty(await ch.PollAsync(50, default));   // neither of those reached the page

            // start_interaction is the one exception — it's how the human's "ok, continue" (given in
            // chat, not clicked anywhere) becomes a clean new session, so it must go through even
            // while killed, and succeeding is what clears the kill (see EvalChannel.SetInteraction).
            var task = BrowserTools.StartInteraction();
            var req = Assert.Single(await ch.PollAsync(1000, default));
            Assert.Equal("overlay", req.Kind);
            Assert.True(req.Show);
            ch.Complete("t", req.Id, "{\"ok\":true,\"shown\":true}");
            await task;

            Assert.False(ch.Killed);          // the fresh session cleared it
            Assert.True(ch.InteractionActive);
        }
        finally { BrowserTools.ForwardHook = null; }
    }

    [Fact]
    public async Task Stop_interaction_while_killed_does_not_clear_the_kill()
    {
        var ch = new EvalChannel("t");
        ch.SetKilled(true);
        BrowserTools.ForwardHook = (_, waitMs, req, _tab) => InstanceEval.DispatchAsync(ch, req, waitMs);
        try
        {
            // Only a fresh (show:true) start_interaction clears a kill — a stray stop_interaction
            // (show:false) is not the "clean new session" signal and must not clear it.
            var task = BrowserTools.StopInteraction();
            var req = Assert.Single(await ch.PollAsync(1000, default));
            Assert.Equal("overlay", req.Kind);
            Assert.False(req.Show);
            ch.Complete("t", req.Id, "{\"ok\":true,\"shown\":false}");
            await task;

            Assert.True(ch.Killed);            // still killed — show:false never clears it
            Assert.False(ch.InteractionActive);
        }
        finally { BrowserTools.ForwardHook = null; }
    }
}
