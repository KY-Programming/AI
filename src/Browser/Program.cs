using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KY.AI.Serve;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace KY.AI.Browser;

// ky-ai-browser — read a served app's BROWSER console (and drive it) from an AI agent over MCP.
//
// Like ky-ai-ng and ky-ai-terminal it is a hub + instances:
//   hub  : control plane — one MCP server (/mcp on a fixed port) + a registry of capture instances.
//          The MCP tools (BrowserTools) forward each call to the right instance by name.
//   (default, no subcommand) : ONE capture instance. The dev runs it next to a running `ky-ai-ng
//          serve`; it (with the dev's confirmation) asks ky-ai-ng to reversibly inject a tiny capture
//          <script> into the app's index.html, the page reloads, the snippet patches console.* + error
//          handlers and POSTs events back here, and the agent reads/drives them via MCP. On Ctrl+C it
//          asks ky-ai-ng to remove the script again. Its lifetime IS the on/off switch.
//
// Because instances bind an OS-assigned loopback port (not a fixed one) and register with the hub,
// you can run several at once — one ky-ai-browser per ky-ai-ng frontend — and the agent still talks
// to the one stable hub URL, routing by `project` (the attached frontend's name).
internal static class Program
{
    private const int DefaultHubPort = 5104;     // ky-ai-browser's MCP hub port (ng=5101..terminal=5103)
    private const int DefaultNgHubPort = 5101;   // where ky-ai-ng's hub lives, to discover the frontend
    private const int BufferCapacity = 1000;
    private const int EvalPollWindowMs = 25_000;  // how long an eval long-poll hangs before the page re-polls

    private static readonly HubConfig BrowserHubCfg = new()
    {
        ToolName = "ky-ai-browser",
        Noun = "capture",
        NounPlural = "captures",
        DefaultPort = DefaultHubPort,
    };

    private static readonly JsonSerializerOptions IngestJson = new() { PropertyNameCaseInsensitive = true };
    // Eval requests go out camelCase explicitly (the snippet reads req.kind/req.expression/…), so the
    // wire shape doesn't depend on the host's ambient JSON config; null fields are dropped so a request
    // only carries the fields its kind uses. Also used for the instance's own JSON responses.
    private static readonly JsonSerializerOptions EvalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected console */ }

        if (args.Any(a => a is "-h" or "--help" or "/?")) { PrintHelp(); return 0; }

        // hub: the MCP control plane. Scan THIS exe's assembly so the hub exposes only the browser
        // tools (BrowserTools), not the ng/net build tools that also live in the Serve assembly.
        if (args.Length > 0 && string.Equals(args[0], "hub", StringComparison.OrdinalIgnoreCase))
            return await HubHost.RunAsync(BrowserHubCfg, args[1..], typeof(Program).Assembly);

        // connect: the stdio bridge Claude Code spawns — proxies to the hub above over HTTP.
        if (args.Length > 0 && string.Equals(args[0], "connect", StringComparison.OrdinalIgnoreCase))
            return await StdioBridge.RunAsync(BrowserHubCfg, args[1..]);

        // init: wire ky-ai-browser into a Claude Code workspace (.mcp.json + allow-list), reflecting
        // BrowserTools off this exe's assembly — same shared command the other tools use.
        if (args.Length > 0 && string.Equals(args[0], "init", StringComparison.OrdinalIgnoreCase))
            return InitCommand.Run("ky-ai-browser", DefaultHubPort, args[1..], typeof(Program).Assembly, runHint: null);

        // shutdown: tear down the whole stack — the hub plus every capture instance it supervises
        // (each detaches and restores its app's index.html).
        if (args.Length > 0 && string.Equals(args[0], "shutdown", StringComparison.OrdinalIgnoreCase))
            return await ShutdownCommand.RunAsync("ky-ai-browser", DefaultHubPort, args[1..]);

        // update: self-update via the shared command. ky-ai-browser is .NET-tool-only (no npm). The
        // hub's /shutdown (on DefaultHubPort) gracefully stops the whole stack before the package
        // manager replaces the locked files.
        if (args.Length > 0 && string.Equals(args[0], "update", StringComparison.OrdinalIgnoreCase))
            return await UpdateCommand.RunAsync("ky-ai-browser", "KY.AI.Browser", npmPackageId: null, DefaultHubPort, args[1..]);

        // Attach mode (no subcommand) takes only flags, so a bare-word first arg is a mistyped
        // command — say so plainly instead of silently falling through to the "no ng" message.
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            Console.Error.WriteLine($"ky-ai-browser: unknown command '{args[0]}'. " +
                "Run `ky-ai-browser --help`, or `ky-ai-browser` (no args) to attach.");
            return 1;
        }

        return await RunInstanceAsync(args);
    }

    // One capture instance: discover the ng frontend, host the page-capture + control API on an
    // OS-assigned loopback port, register with the browser hub by the frontend's name, inject the
    // snippet, and keep it alive until Ctrl+C / shutdown.
    private static async Task<int> RunInstanceAsync(string[] args)
    {
        // Shared serve flags (--rest-port, --hub-port, --name, --no-hub, …) come from the common parser,
        // exactly like ky-ai-ng / ky-ai-terminal; the browser-specific flags are peeled from Extra.
        var o = ServeCommandLine.Parse(args, DefaultHubPort);
        var restPort = o.ControlPort;          // 0 → OS-assigned loopback port
        var hubUrl = o.HubUrl;
        var hubPort = Uri.TryCreate(hubUrl, UriKind.Absolute, out var hu) ? hu.Port : DefaultHubPort;
        var useHub = o.UseHub;

        var ngHubPort = DefaultNgHubPort;
        string? project = null;
        var yes = false;
        string? startedBy = null;
        for (var i = 0; i < o.Extra.Count; i++)
        {
            switch (o.Extra[i])
            {
                case "--ng-hub-port": if (++i < o.Extra.Count && int.TryParse(o.Extra[i], out var nhp)) ngHubPort = nhp; break;
                case "--project": if (++i < o.Extra.Count) project = o.Extra[i]; break;
                case "-y": case "--yes": yes = true; break;
                // Set by a launching tool (ky-ai-ng serve --after-start): we share its console, so we
                // continue its open box instead of opening our own, and skip "Ctrl+C to detach" (Ctrl+C
                // here kills the whole tree). The value is the launcher's name, for the message only.
                case "--started-by": if (++i < o.Extra.Count) startedBy = o.Extra[i]; break;
            }
        }

        // 1) Find the running ky-ai-ng frontend to attach to (returns its name + control URL).
        string ngControlUrl;
        string ngName;
        try
        {
            // When launched via `ky-ai-ng serve --after-start`, our launch (first build settled) and
            // ng's own hub registration (a separate fire-and-forget retry loop) are unsynchronized — ng
            // can still be mid-registration when we ask. Give that race a few seconds to resolve instead
            // of failing on the first miss; a manual invocation (no --started-by) still fails fast.
            var found = startedBy is null
                ? await DiscoverNgAsync(ngHubPort, project)
                : await DiscoverNgWithRetryAsync(ngHubPort, project, TimeSpan.FromSeconds(5));
            if (found is null)
            {
                Console.Error.WriteLine(
                    $"ky-ai-browser: no running ky-ai-ng frontend found (hub :{ngHubPort})." +
                    (project is null ? "" : $" (project '{project}')") +
                    " Start `ky-ai-ng serve` first.");
                return 1;
            }
            (ngName, ngControlUrl) = found.Value;
        }
        catch (Exception ex) { Console.Error.WriteLine($"ky-ai-browser: {ex.Message}"); return 1; }

        // The instance registers under the attached frontend's name (or an explicit --name), so the
        // agent routes `project` to a ky-ai-browser the same way it does for ky-ai-ng.
        var instanceName = o.Name ?? ngName;

        // 2) Confirm the (reversible) manipulation. Default yes; skip with -y or non-interactive stdin.
        // Being launched by another tool (--started-by, e.g. `ky-ai-ng serve --after-start ky-ai-browser`)
        // is itself the opt-in — the dev chose to start us — so we skip the prompt there too.
        if (!yes && startedBy is null && !Console.IsInputRedirected)
        {
            Console.WriteLine("ky-ai-browser will ask ky-ai-ng to inject a capture <script> into your app's index.html");
            Console.WriteLine("so the agent can read the browser console. It is removed automatically on shutdown (Ctrl+C).");
            Console.Write("Proceed? [Y/n] ");
            var ans = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(ans) && !ans.StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Aborted; nothing injected.");
                return 0;
            }
        }

        // 3) Build the capture buffer + the eval channel. The snippet (which carries the absolute URL
        // back to us) is templated AFTER we bind, since the port is OS-assigned.
        var collector = new ConsoleCollector(BufferCapacity, () => Interlocked.Read(ref Capture.BuildSeq));
        Capture.Collector = collector;
        var eval = new TabRegistry(collector.Token);
        Capture.Eval = eval;
        var snippet = "";   // assigned after bind, before inject (no request can arrive until then)

        // 4) Host the page-facing capture endpoints AND a small control API the hub forwards to, on one
        // OS-assigned loopback port.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.MapGet("/health", () => Results.Text("ok"));
        // `ky-ai-browser shutdown` cascades here from the hub. Reply first, then stop — ApplicationStopping
        // runs the uninject so index.html is restored, exactly as a Ctrl+C would. Loopback-only like the rest.
        app.MapPost("/shutdown", () =>
        {
            _ = Task.Run(async () => { try { await Task.Delay(150); } catch { } app.Lifetime.StopApplication(); });
            return Results.Content("{\"ok\":true,\"action\":\"shutdown\"}", "application/json");
        });

        // ── control API: what the hub's BrowserTools forward to (loopback-only) ──
        app.MapGet("/status", () =>
        {
            var snap = eval.StatusSnapshot();
            // Instance identity + the per-tab breakdown (snap carries pageConnected/interaction/paused/
            // killed/holdReload aggregates plus a `tabs` array with per-tab owner/lease/flags).
            var payload = new Dictionary<string, object?>
            {
                ["name"] = instanceName,
                ["attachedTo"] = ngName,
                ["running"] = true,
                ["buildSeq"] = Interlocked.Read(ref Capture.BuildSeq),
            };
            foreach (var pr in JsonSerializer.SerializeToElement(snap, EvalJson).EnumerateObject())
                payload[pr.Name] = pr.Value.Clone();
            return Results.Content(JsonSerializer.Serialize(payload, EvalJson), "application/json");
        });
        app.MapGet("/console/tail", (int? lines, string? level, long? sinceSeq, string? grep, string? pageLoad, bool? compact, bool? appOnly, bool? dropFrameworkNoise, bool? currentPageOnly, string? tab) =>
            Results.Content(collector.TailJson("browser", enabled: true,
                lines is null or <= 0 ? 200 : lines.Value, level, sinceSeq ?? 0, sinceBuildSeq: 0,
                grep, pageLoad, compact ?? false, appOnly ?? false, dropFrameworkNoise ?? false, currentPageOnly ?? false, tab), "application/json"));
        app.MapPost("/console/clear", () =>
        {
            collector.Clear();
            return Results.Content(JsonSerializer.Serialize(new { ok = true, action = "console_clear" }, EvalJson), "application/json");
        });
        // Blocks until the human clicks the paused pill's resume, or times out — same shape as ng's
        // /wait-for-build (a plain poll loop the tool call just waits on), so an agent can wait for the
        // human's go-ahead instead of retrying start_interaction itself. A kill is stronger than a pause
        // and is never worth waiting out (WaitForResumeAsync returns false immediately for one), so the
        // response calls that out explicitly instead of just looking like an ordinary timeout.
        app.MapPost("/wait-for-resume", async (HttpContext ctx, int? timeout, string? tab) =>
        {
            var agent = ctx.Request.Headers[AgentContext.Header].ToString();
            var (cleared, ch) = await eval.WaitForResumeAsync(agent, tab, timeout ?? 60_000, ctx.RequestAborted);
            var killed = ch?.Killed ?? false;
            return Results.Content(JsonSerializer.Serialize(new
            {
                ok = cleared,
                timedOut = !cleared && !killed,
                killed,
                paused = ch?.Paused ?? false,
                interactionActive = ch?.InteractionActive ?? false,
                error = killed
                    ? "the user stopped the session completely (not just paused) — do not call this again; wait for them in chat instead"
                    : cleared ? null : "still paused — call wait_for_resume again to keep waiting",
            }, EvalJson), "application/json");
        });
        // Every page action arrives here as an EvalRequest the hub already built from the MCP args.
        // InstanceEval owns the interaction gate (its flag lives on the channel), then queues + awaits.
        app.MapPost("/eval", async (HttpContext ctx, int? waitMs, string? tab) =>
        {
            EvalRequest? req;
            try { req = await JsonSerializer.DeserializeAsync<EvalRequest>(ctx.Request.Body, IngestJson, ctx.RequestAborted); }
            catch { req = null; }
            if (req is null || string.IsNullOrEmpty(req.Kind))
                return Results.Content(JsonSerializer.Serialize(new { ok = false, error = "eval requires a request body" }, EvalJson), "application/json");

            // agent = who is calling (stamped by their connect bridge, forwarded by the hub); tab = an
            // explicit target when the agent drives several tabs. The registry resolves the tab, enforces
            // ownership, and runs the interaction gate.
            var agent = ctx.Request.Headers[AgentContext.Header].ToString();
            var budget = waitMs ?? (req.TimeoutMs + 1500);
            var result = await eval.DispatchAsync(req, budget, agent, tab);
            return Results.Content(result, "application/json");
        });

        // ── page-facing endpoints (cross-origin from ng's origin; loopback-only) ──
        app.MapGet("/__kyai/console.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            return Results.Text(snippet, "text/javascript; charset=utf-8");
        });
        app.MapMethods("/__kyai/console", new[] { "OPTIONS" }, (HttpContext ctx) =>
        {
            Cors(ctx);
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
        app.MapPost("/__kyai/console", async (HttpContext ctx) =>
        {
            Cors(ctx); // page is on ng's origin → cross-origin; loopback-only, so allow any origin
            try
            {
                var batch = await JsonSerializer.DeserializeAsync<ConsoleIngestBatch>(ctx.Request.Body, IngestJson, ctx.RequestAborted);
                var n = batch is null ? 0 : collector.Ingest(batch);
                return Results.Json(new { ok = true, ingested = n });
            }
            catch { return Results.Json(new { ok = false }); }
        });

        // ── eval return channel: the capture snippet long-polls /poll for work the runtime-inspection
        //    tools queued, runs it, and POSTs the result to /result (both cross-origin, loopback-only). ──
        app.MapGet("/__kyai/eval/poll", async (HttpContext ctx) =>
        {
            Cors(ctx);
            if (!string.Equals(ctx.Request.Query["token"], collector.Token, StringComparison.Ordinal))
                return Results.Json(new { requests = Array.Empty<EvalRequest>(), interactionActive = false, paused = false, killed = false, holdReload = false, userHoldReload = false, tabId = (string?)null, claimed = false, handoff = (object?)null }, EvalJson);   // foreign tab — hand it nothing
            var tabId = ctx.Request.Query["tabId"].ToString();
            var claim = ctx.Request.Query["claim"].ToString();
            var pageLoadId = ctx.Request.Query["pageLoadId"].ToString();
            var poll = await eval.PollAsync(tabId, claim, pageLoadId, EvalPollWindowMs, ctx.RequestAborted);
            // interactionActive/paused/killed/holdReload are THIS TAB's now (same field names as before, so the
            // snippet reconciles unchanged) and let a (re)loaded tab restore its own overlay/paused/killed/held
            // state. claimed acks a presented claim ticket; handoff asks this tab to show the "another agent
            // wants in" prompt.
            return Results.Json(new
            {
                requests = poll.Requests,
                interactionActive = poll.InteractionActive,
                paused = poll.Paused,
                killed = poll.Killed,
                holdReload = poll.HoldReload,
                userHoldReload = poll.UserHoldReload,   // the hold is the human's own ⇒ the menu shows it toggled on
                tabId = poll.TabId,
                claimed = poll.Claimed,
                handoff = poll.Handoff,
                reassignTabId = poll.ReassignTabId,   // non-null ⇒ duplicate tab: snippet re-keys to this id
            }, EvalJson);
        });
        app.MapMethods("/__kyai/eval/result", new[] { "OPTIONS" }, (HttpContext ctx) =>
        {
            Cors(ctx);
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
        app.MapPost("/__kyai/eval/result", async (HttpContext ctx) =>
        {
            Cors(ctx);
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                var root = doc.RootElement;
                var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
                var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
                var payload = root.TryGetProperty("payload", out var p) ? p.GetRawText() : null;
                var tabId = root.TryGetProperty("tabId", out var tb) ? tb.GetString() : null;
                return Results.Json(new { ok = eval.Complete(tabId, token, id, payload) });
            }
            catch { return Results.Json(new { ok = false }); }
        });

        // ── human overrides: the badge's Pause/Stop icons (the paused pill carries its own Stop icon
        //    too, for a direct Pause→Stop escalation) — clicked by the human, not the agent. Same
        //    token-guard pattern as the rest of the page-facing endpoints. Pause is the brief, resumable
        //    one (paired with /resume). Stop/kill is the hard one — deliberately NOT paired with a
        //    "revive" route: resuming after a kill means the human tells the agent in chat, and the
        //    agent's own start_interaction clears it (see EvalChannel.SetInteraction). ──
        app.MapMethods("/__kyai/interaction/pause", new[] { "OPTIONS" }, (HttpContext ctx) =>
        {
            Cors(ctx);
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
        app.MapPost("/__kyai/interaction/pause", async (HttpContext ctx) =>
        {
            Cors(ctx);
            var body = await ReadBodyAsync(ctx);
            if (!TokenOk(body, collector.Token)) return Results.Json(new { ok = false });
            eval.SetPaused(Str(body, "tabId"), true);
            return Results.Json(new { ok = true, paused = true });
        });
        app.MapMethods("/__kyai/interaction/resume", new[] { "OPTIONS" }, (HttpContext ctx) =>
        {
            Cors(ctx);
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
        app.MapPost("/__kyai/interaction/resume", async (HttpContext ctx) =>
        {
            Cors(ctx);
            var body = await ReadBodyAsync(ctx);
            if (!TokenOk(body, collector.Token)) return Results.Json(new { ok = false });
            eval.SetPaused(Str(body, "tabId"), false);
            return Results.Json(new { ok = true, paused = false });
        });
        app.MapMethods("/__kyai/interaction/kill", new[] { "OPTIONS" }, (HttpContext ctx) =>
        {
            Cors(ctx);
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
        app.MapPost("/__kyai/interaction/kill", async (HttpContext ctx) =>
        {
            Cors(ctx);
            var body = await ReadBodyAsync(ctx);
            if (!TokenOk(body, collector.Token)) return Results.Json(new { ok = false });
            // scope:"all" stops every agent's tab at once (the overlay's "stop all" control); otherwise
            // just the tab whose Stop icon was clicked.
            var scopeAll = string.Equals(Str(body, "scope"), "all", StringComparison.Ordinal);
            eval.SetKilled(Str(body, "tabId"), true, scopeAll);
            return Results.Json(new { ok = true, killed = true, scope = scopeAll ? "all" : "tab" });
        });
        // ── handoff: the "another agent wants in" prompt's Share / Deny buttons (Open-a-new-tab needs no
        //    server call — it's window.open in the click handler, and the new tab presents its claim on
        //    its first poll). Token-guarded like the rest. ──
        app.MapMethods("/__kyai/handoff/share", new[] { "OPTIONS" }, (HttpContext ctx) =>
        {
            Cors(ctx);
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
        app.MapPost("/__kyai/handoff/share", async (HttpContext ctx) =>
        {
            Cors(ctx);
            var body = await ReadBodyAsync(ctx);
            if (!TokenOk(body, collector.Token)) return Results.Json(new { ok = false });
            var ticket = Str(body, "ticket");
            var ok = ticket is not null && eval.ShareTab(Str(body, "tabId"), ticket);
            return Results.Json(new { ok });
        });
        app.MapMethods("/__kyai/handoff/deny", new[] { "OPTIONS" }, (HttpContext ctx) =>
        {
            Cors(ctx);
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
        app.MapPost("/__kyai/handoff/deny", async (HttpContext ctx) =>
        {
            Cors(ctx);
            var body = await ReadBodyAsync(ctx);
            if (!TokenOk(body, collector.Token)) return Results.Json(new { ok = false });
            var ticket = Str(body, "ticket");
            var ok = ticket is not null && eval.DenyHandoff(ticket);
            return Results.Json(new { ok });
        });
        // The held-reload pill's click: hand the Angular dev server's live-reload back for THIS session
        // without ending the agent's session (unlike Pause, which does end it). Deliberately one-way and
        // session-scoped — the agent can't re-arm the hold itself, and the next start_interaction does
        // (see EvalChannel.SetInteraction). The page skips its catch-up reload on this path, so clicking
        // never disturbs whatever is currently on screen.
        app.MapMethods("/__kyai/reload/release", new[] { "OPTIONS" }, (HttpContext ctx) =>
        {
            Cors(ctx);
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
        app.MapPost("/__kyai/reload/release", async (HttpContext ctx) =>
        {
            Cors(ctx);
            var body = await ReadBodyAsync(ctx);
            if (!TokenOk(body, collector.Token)) return Results.Json(new { ok = false });
            eval.SetReloadReleased(Str(body, "tabId"));
            return Results.Json(new { ok = true, holdReload = false });
        });
        // The overlay menu's "Stop Angular reloads" toggle — the human's OWN hold, unlike /reload/release
        // above (which only opts out of the current agent session's). Independent of any session: nothing
        // the agent does sets or clears it, so it also works with no session open at all, which is the
        // point — an agent saving files while the human tests by hand.
        app.MapMethods("/__kyai/reload/hold", new[] { "OPTIONS" }, (HttpContext ctx) =>
        {
            Cors(ctx);
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
        app.MapPost("/__kyai/reload/hold", async (HttpContext ctx) =>
        {
            Cors(ctx);
            var body = await ReadBodyAsync(ctx);
            if (!TokenOk(body, collector.Token)) return Results.Json(new { ok = false });
            var hold = Bool(body, "hold") ?? true;
            eval.SetUserHoldReload(Str(body, "tabId"), hold);
            return Results.Json(new { ok = true, userHoldReload = hold });
        });
        app.Urls.Add($"http://127.0.0.1:{restPort}");

        // The heartbeat keeps ky-ai-ng from auto-reverting our inject; cancel it before we uninject so
        // there's no race. On shutdown always revert and deregister — Ctrl+C or a crash mid-run leaves
        // index.html clean and the hub's registry tidy.
        var stopping = new CancellationTokenSource();
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            stopping.Cancel();
            try { if (useHub) DeregisterAsync(hubUrl, instanceName).GetAwaiter().GetResult(); } catch { /* hub gone */ }
            try { UninjectAsync(ngControlUrl).GetAwaiter().GetResult(); } catch { /* ng gone — it self-heals on next start */ }
        });

        try { await app.StartAsync(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ky-ai-browser: cannot bind control port {restPort} ({ex.Message}). Pass --rest-port <N> for another.");
            return 1;
        }

        // Now that we're bound, learn our actual port and template the snippet to point the page back at us.
        var selfUrl = GetBoundUrl(app) ?? $"http://127.0.0.1:{restPort}";
        var ingestUrl = $"{selfUrl}/__kyai/console";
        snippet = LoadSnippet().Replace("__KYAI_TOKEN__", collector.Token).Replace("__KYAI_INGEST__", ingestUrl);
        // The token doubles as a per-instance cache-buster in the src. A fresh instance has a fresh token,
        // so re-injecting yields *different* index.html content — the write isn't skipped as a no-op, ng
        // reloads the page, and it picks up the new snippet. (The console.js endpoint ignores the query
        // string, so it still serves the snippet.)
        var scriptTag = $"<script src=\"{selfUrl}/__kyai/console.js?t={collector.Token}\"></script>";

        // 5) Register with the browser hub (auto-starting it if needed), then inject.
        if (useHub)
        {
            if (!await HubLifecycle.HubReachableAsync(hubUrl))
                HubLifecycle.TryLaunchHub("ky-ai-browser", hubPort);
            _ = RegisterLoopAsync(hubUrl, instanceName, selfUrl, eval, stopping.Token);
        }

        if (!await InjectAsync(ngControlUrl, scriptTag))
        {
            Console.Error.WriteLine($"ky-ai-browser: ky-ai-ng could not inject into the app's index.html (is it present?). Detaching.");
            await app.StopAsync();
            return 1;
        }

        // Keep the inject alive (and pull ng's build seq for correlation) until we shut down.
        _ = HeartbeatLoopAsync(ngControlUrl, stopping.Token);

        var rows = new List<string>
        {
            BannerBox.Row("capture", $"'{instanceName}' → {ngControlUrl}"),
            BannerBox.Row("serving", selfUrl),
            BannerBox.Row("MCP", useHub ? $"{hubUrl}/mcp  ·  project '{instanceName}'" : "standalone (--no-hub) — no agent access"),
            BannerBox.Row("page", "reloads; console now flows to console_tail"),
        };
        if (startedBy is null)
            // Standalone: a complete box; Ctrl+C here detaches just this tool (restores index.html).
            BannerBox.Render("ky-ai-browser", rows.Append("").Append(BannerBox.Row("stop", "Ctrl+C to detach")).ToArray());
        else
            // Launched by another tool — continue its open box; no "Ctrl+C to detach" (Ctrl+C kills the tree).
            BannerBox.RenderContinuation("ky-ai-browser", rows.ToArray());
        await app.WaitForShutdownAsync();
        return 0;
    }

    private static void Cors(HttpContext ctx)
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    }

    // The human-override routes (pause/resume/kill/reload-release/handoff) all POST a small {token, …}
    // body. Read it once (it can only be read once), then pull fields off the parsed element.
    private static async Task<JsonElement?> ReadBodyAsync(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private static bool TokenOk(JsonElement? body, string expected) =>
        body is { } b && b.TryGetProperty("token", out var t) && string.Equals(t.GetString(), expected, StringComparison.Ordinal);

    private static bool? Bool(JsonElement? body, string prop) =>
        body is { } b && b.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static string? Str(JsonElement? body, string prop) =>
        body is { } b && b.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? GetBoundUrl(WebApplication app)
    {
        var addr = app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        return addr?.Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
    }

    // Look up a registered ky-ai-ng frontend via its hub's /registry. Returns the frontend's (name,
    // control URL), or null if none is registered. Throws (with names) if several match and no --project.
    private static async Task<(string Name, string Url)?> DiscoverNgAsync(int hubPort, string? project)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        string body;
        try { body = await http.GetStringAsync($"http://127.0.0.1:{hubPort}/registry"); }
        catch { return null; } // hub not up → no ng serve running

        using var doc = JsonDocument.Parse(body);
        var regs = doc.RootElement.EnumerateArray()
            .Select(e => (
                name: e.TryGetProperty("name", out var n) ? n.GetString() : null,
                url: e.TryGetProperty("controlUrl", out var u) ? u.GetString() : null))
            .Where(r => !string.IsNullOrEmpty(r.url) && !string.IsNullOrEmpty(r.name))
            .ToList();

        if (regs.Count == 0) return null;
        if (project is not null)
        {
            var match = regs.FirstOrDefault(r => string.Equals(r.name, project, StringComparison.OrdinalIgnoreCase));
            return match.url is null ? null : (match.name!, match.url!);
        }
        if (regs.Count == 1) return (regs[0].name!, regs[0].url!);
        throw new InvalidOperationException(
            $"multiple frontends registered ({string.Join(", ", regs.Select(r => r.name))}); pass --project <name>.");
    }

    // Retries DiscoverNgAsync (hub not up yet, or ng not registered yet) until it finds a match or
    // `timeout` elapses. A genuine ambiguity (multiple frontends, no --project) is not transient —
    // propagate it immediately instead of retrying it out.
    private static async Task<(string Name, string Url)?> DiscoverNgWithRetryAsync(int hubPort, string? project, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var found = await DiscoverNgAsync(hubPort, project);
            if (found is not null || DateTime.UtcNow >= deadline) return found;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }

    // ── browser-hub registration (mirrors SupervisorHost/TerminalHost) ──

    private static async Task RegisterLoopAsync(string hubUrl, string name, string controlUrl, TabRegistry eval, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var body = JsonSerializer.Serialize(new { name, controlUrl });
        var url = hubUrl.TrimEnd('/') + "/register";
        var bridgesUrl = hubUrl.TrimEnd('/') + "/bridges";
        while (!ct.IsCancellationRequested)
        {
            var ok = false;
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await http.PostAsync(url, content, ct);
                ok = resp.IsSuccessStatusCode;
            }
            catch { /* hub not up yet */ }
            // Same cadence: pull the hub's live bridge ids so tab leases release on agent DISCONNECT
            // rather than idleness. Best-effort — if the hub can't answer, the registry's liveness data
            // goes stale and it falls back to the sliding-lease rule on its own.
            if (ok)
            {
                try
                {
                    var ids = JsonSerializer.Deserialize<string[]>(await http.GetStringAsync(bridgesUrl, ct));
                    if (ids is not null) eval.SetLiveAgents(ids);
                }
                catch { /* hub answered /register but not /bridges (older hub) — lease rule covers it */ }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(ok ? 15 : 2), ct); }
            catch { break; }
        }
    }

    private static async Task DeregisterAsync(string hubUrl, string name)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var body = JsonSerializer.Serialize(new { name, controlUrl = "" });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        await http.PostAsync(hubUrl.TrimEnd('/') + "/deregister", content);
    }

    private static async Task<bool> InjectAsync(string controlUrl, string scriptTag)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var body = JsonSerializer.Serialize(new { path = "/html/head", content = scriptTag });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            using var resp = await http.PostAsync(controlUrl.TrimEnd('/') + "/inject", content);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch { return false; }
    }

    private static async Task UninjectAsync(string controlUrl)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        try { await http.PostAsync(controlUrl.TrimEnd('/') + "/uninject", content); } catch { /* best-effort */ }
    }

    // Ping ng every 5s so it knows we're alive (else it auto-reverts the inject), and pull the current
    // build seq back for console↔build correlation. Best-effort: if ng is briefly unreachable we keep
    // trying; if we stay gone, ng's watchdog reverts.
    private static async Task HeartbeatLoopAsync(string controlUrl, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var url = controlUrl.TrimEnd('/') + "/inject/heartbeat";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var resp = await http.PostAsync(url, content, ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    if (doc.RootElement.TryGetProperty("buildSeq", out var bs) && bs.TryGetInt64(out var seq))
                        Interlocked.Exchange(ref Capture.BuildSeq, seq);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* ng unreachable — keep trying */ }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { break; }
        }
    }

    // The embedded snippet ("KY.AI.Browser.console-capture.js"); resolved by suffix.
    private static string LoadSnippet()
    {
        var asm = typeof(Program).Assembly;
        var name = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith("console-capture.js", StringComparison.OrdinalIgnoreCase));
        if (name is null) return "/* kyai: console-capture.js resource missing */";
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        ky-ai-browser — read a served app's browser console (and drive it) from an AI agent over MCP.

        Run it next to a running `ky-ai-ng serve`:
          ky-ai-browser [options]
            --project <id>      Which ky-ai-ng frontend to attach to (default: the only one registered).
                                Also the name this capture registers under in the hub.
            --name <id>         Override the hub registry name (default: the attached frontend's name)
            --hub-port <N>      ky-ai-browser hub port to register with (default: 5104; auto-started)
            --ng-hub-port <N>   ky-ai-ng hub port to discover the frontend (default: 5101)
            --rest-port <N>     This instance's own control/ingest port (default: OS-assigned)
            --no-hub            Standalone: host capture locally only; no hub, no agent access
            -y, --yes           Skip the inject confirmation (default answer is yes anyway)
            --started-by <tool> Set automatically when launched via `ky-ai-ng serve --after-start`:
                                continues the launcher's start-up box instead of opening its own, and
                                skips the inject confirmation (being launched is itself the opt-in).

        You can run several at once — one ky-ai-browser per ky-ai-ng frontend. Each binds its own
        OS-assigned port and registers with the shared hub; the agent talks to the one hub URL
        (127.0.0.1:5104/mcp) and routes by `project`. An MCP hub auto-starts on demand and self-exits
        when idle — you never run it yourself.

        On start it asks ky-ai-ng to inject a capture <script> into the app's index.html (you confirm;
        default yes); the page reloads and console.*/errors/rejections flow to the `console_tail` MCP
        tool. On Ctrl+C the script is removed and index.html is restored. If ky-ai-browser dies without
        cleaning up, ky-ai-ng strips the leftover automatically. All HTTP is loopback-only.

        HUB — the MCP control plane (auto-started; rarely run by hand):
          ky-ai-browser hub [--port <N>]

        SHUTDOWN — tear down the whole stack: the hub plus every capture instance (each removes its
        script and restores index.html):
          ky-ai-browser shutdown [--hub-port <N>]

        UPDATE — update ky-ai-browser to the latest version (.NET global tool):
          ky-ai-browser update
          Runs `dotnet tool update --global KY.AI.Browser --no-cache`. Stops the running stack first
          (it locks the files), then on Windows the update runs in a new window that opens once this
          process exits (a running tool can't overwrite its own files).

        INIT — wire ky-ai-browser into an AI agent's workspace (Claude Code, Cursor, or VS Code):
          ky-ai-browser init [--agent <claude|cursor|vscode>] [-y] [--dir <path>]
          Walks up from the current directory for the agent's config folder, then (each step
          confirmed) adds the MCP server (127.0.0.1:5104) and, for Claude, allows its commands in
          .claude/settings.local.json. Without --agent the agent is auto-detected and confirmed
          via a picker; -y takes the detected one and accepts every prompt (non-interactive).
          Merges into existing files; safe to re-run. `init --help` lists the per-agent paths.
          Or wire it by hand — Cursor (.cursor/mcp.json) and VS Code (.vscode/mcp.json) take the
          hub URL, while Claude (.mcp.json) proxies over stdio so it reconnects to an on-demand hub:
            { "mcpServers": { "ky-ai-browser": { "url": "http://127.0.0.1:5104/mcp" } } }
            { "mcpServers": { "ky-ai-browser": { "command": "ky-ai-browser", "args": ["connect"] } } }
        """);
    }
}
