using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace KY.AI.Serve;

// Hosts one dev-server supervisor: spawns + tees + tracks the child (via DevServer), exposes a
// loopback REST control API, fails fast on a port clash, auto-starts a hub if none is up, and
// re-registers with the hub on a heartbeat. Shared by both tools; SupervisorOptions carries the
// per-invocation values and SupervisorConfig the tool-specific strategy.
public static class SupervisorHost
{
    private static readonly JsonSerializerOptions InjectJsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(SupervisorOptions opt, SupervisorConfig cfg)
    {
        if (opt.LogPath is not null) Cli.EnsureDir(opt.LogPath);

        using var server = new DevServer(opt, cfg);
        using var afterStart = opt.AfterStart is { Count: > 0 } ? new AfterStartLauncher() : null;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.MapGet("/health", () => Results.Text("ok"));
        app.MapGet("/status", () => Results.Content(server.StatusJson(), "application/json"));
        app.MapPost("/restart", async () => Results.Content(await server.RestartJsonAsync(), "application/json"));
        app.MapPost("/stop", async () => Results.Content(await server.StopJsonAsync(), "application/json"));
        app.MapPost("/start", async () => Results.Content(await server.StartJsonAsync(), "application/json"));
        app.MapGet("/tail", (int? lines, bool? summary, long? sinceSeq, string? grep) =>
            Results.Text(server.TailText(lines ?? 0, summary ?? false, sinceSeq ?? 0, grep)));
        app.MapPost("/wait-for-build", async (int? timeout, int? quiet) =>
            Results.Content(await server.WaitForBuildJsonAsync(timeout ?? cfg.DefaultTimeoutMs, quiet ?? 500), "application/json"));
        app.MapPost("/set-log-lines", (int count) =>
        {
            server.SetLogCapacity(count);
            return Results.Content(server.StatusJson(), "application/json");
        });
        // Generic, reversible HTML injection (driven by ky-ai-browser): add/strip a marked block in
        // the app's index.html. Available only where the tool supplies a target (ng does; dotnet not).
        app.MapPost("/inject", async (HttpContext ctx) =>
        {
            InjectRequest? req;
            try { req = await JsonSerializer.DeserializeAsync<InjectRequest>(ctx.Request.Body, InjectJsonOpts); }
            catch { req = null; }
            if (req is null || string.IsNullOrEmpty(req.Content))
                return Results.BadRequest(new { ok = false, error = "inject requires { path, content }" });
            return Results.Content(server.InjectJson(req.File, req.Path ?? "/html/head", req.Content), "application/json");
        });
        app.MapPost("/uninject", () => Results.Content(server.UninjectJson(), "application/json"));
        app.MapPost("/inject/heartbeat", () => Results.Content(server.InjectHeartbeatJson(), "application/json"));
        // Exit this supervisor process (the hub calls this when tearing the whole stack down).
        // ApplicationStopping deregisters and kills the dev-server tree; delayed a beat so the
        // response reaches the hub before the host tears down.
        app.MapPost("/shutdown", () =>
        {
            _ = Task.Run(async () => { await Task.Delay(250); app.Lifetime.StopApplication(); });
            return Results.Content("{\"ok\":true,\"action\":\"shutdown\"}", "application/json");
        });
        app.Urls.Add($"http://127.0.0.1:{opt.ControlPort}");

        var stopping = new CancellationTokenSource();
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            stopping.Cancel();
            try { afterStart?.Dispose(); } catch { /* reap the --after-start child tree */ }
            try { if (opt.UseHub) DeregisterAsync(opt.HubUrl, opt.Name).GetAwaiter().GetResult(); } catch { /* hub gone */ }
            try { server.RevertInject(); } catch { /* leave index.html clean even if ky-ai-browser died */ }
            try { server.StopAsync().GetAwaiter().GetResult(); } catch { /* shutting down */ }
        });

        // Bind the control port BEFORE spawning the child, so a clash fails fast without orphaning a dev server.
        try
        {
            await app.StartAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{cfg.ToolName}: cannot bind control port {opt.ControlPort} ({ex.Message}).");
            return 1;
        }

        var controlUrl = GetBoundUrl(app) ?? $"http://127.0.0.1:{opt.ControlPort}";
        server.ControlUrl = controlUrl;

        // Frame the start-up status as one box (printed before the child is teed, so the dev server's
        // own "Building…" output lands cleanly below it). Build the lines first, deciding the hub
        // action, then fire the side-effects (launch hub / register / after-start) and start the child.
        var logDesc = opt.LogPath is null ? $"buffer-only ({opt.LogLines} lines, via MCP)" : $"{opt.LogPath} ({opt.LogLines} lines)";
        var lines = new List<string>
        {
            $"{cfg.Noun} '{opt.Name}'",
            BannerBox.Row("control", controlUrl),
            BannerBox.Row("log", logDesc),
        };

        int? launchHubPort = null;
        if (opt.UseHub)
        {
            var hubPort = TryGetLoopbackPort(opt.HubUrl);
            if (opt.AutostartHub && hubPort is int hp && !await HubLifecycle.HubReachableAsync(opt.HubUrl))
            {
                lines.Add(BannerBox.Row("hub", $"not reachable — auto-starting on port {hp}"));
                launchHubPort = hp;
            }
            lines.Add(BannerBox.Row("hub", $"registering with {opt.HubUrl}"));
        }
        else
        {
            lines.Add(BannerBox.Row("hub", "standalone (--no-hub) — not registered"));
        }
        if (afterStart is not null && opt.AfterStart is { Count: > 0 } afterCmd)
            lines.Add(BannerBox.Row("after-start", $"{string.Join(' ', afterCmd)}  ·  runs after first build"));
        lines.Add("");
        lines.Add("Ctrl+C to stop");

        BannerBox.Render(cfg.ToolName, lines);

        if (launchHubPort is int port) HubLifecycle.TryLaunchHub(cfg.ToolName, port);
        if (opt.UseHub) _ = RegisterLoopAsync(opt.HubUrl, opt.Name, controlUrl, stopping.Token);
        if (afterStart is not null && opt.AfterStart is { Count: > 0 } afterCmd2)
            _ = afterStart.LaunchAfterBuildAsync(cfg, server, afterCmd2, opt.Name, stopping.Token);

        server.Start();   // tee the child last, so "Building…" prints below the box

        await app.WaitForShutdownAsync();
        return 0;
    }

    private static string? GetBoundUrl(WebApplication app)
    {
        var addr = app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        return addr?.Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
    }

    private static async Task RegisterLoopAsync(string hubUrl, string name, string controlUrl, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var body = JsonSerializer.Serialize(new { name, controlUrl });
        var url = hubUrl.TrimEnd('/') + "/register";
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

            // Heartbeat slowly once registered; retry quickly while waiting for the hub to come up.
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

    private static int? TryGetLoopbackPort(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            (u.Host == "127.0.0.1" || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            return u.Port;
        return null;
    }

}
