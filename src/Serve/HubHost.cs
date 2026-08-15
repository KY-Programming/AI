namespace KY.AI.Serve;

// Hosts the hub control plane: one MCP server (/mcp) + a supervisor registry exposed over a
// loopback REST API. Shared by both tools; the HubConfig supplies the tool name, the noun
// wording and the default port.
public static class HubHost
{
    // Default overload scans the Serve assembly for [McpServerTool]s — i.e. HubTools (ng/net).
    public static Task<int> RunAsync(HubConfig cfg, string[] rest)
        => RunAsync(cfg, rest, typeof(HubTools).Assembly);

    // A hub hosts exactly one tool's MCP surface. The tool assembly is explicit so a sibling exe
    // (e.g. ky-ai-terminal) can host ONLY its own tools instead of every [McpServerTool] in Serve.
    public static async Task<int> RunAsync(HubConfig cfg, string[] rest, System.Reflection.Assembly toolAssembly)
    {
        var port = cfg.DefaultPort;
        for (var i = 0; i < rest.Length; i++)
            if (rest[i] == "--port" && ++i < rest.Length && int.TryParse(rest[i], out var p)) port = p;

        Hub.Noun = cfg.Noun;
        Hub.NounPlural = cfg.NounPlural;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly(toolAssembly);

        var app = builder.Build();
        Hub.ShutdownHook = () => app.Lifetime.StopApplication();

        // Lift the calling agent's id (stamped by its `connect` bridge) into AgentContext for the life
        // of the request. The streamable-HTTP tool handler runs under this request's ExecutionContext,
        // so the AsyncLocal is visible inside the tool → Hub.ForwardAsync re-attaches it downstream.
        app.Use(async (ctx, next) =>
        {
            var agent = ctx.Request.Headers[AgentContext.Header].ToString();
            AgentContext.Current = string.IsNullOrEmpty(agent) ? null : agent;
            await next();
        });

        app.MapMcp("/mcp");
        app.MapGet("/health", () => Results.Text("ok"));
        app.MapPost("/register", (RegisterRequest req) =>
        {
            Hub.Registry.Upsert(new Registration(req.Name, req.ControlUrl));
            return Results.Ok();
        });
        app.MapPost("/deregister", (RegisterRequest req) =>
        {
            Hub.Registry.Remove(req.Name);
            return Results.Ok();
        });
        app.MapGet("/registry", () => Results.Json(Hub.Registry.All()));
        // Bridges (`<tool> connect`) have no listener, so they check in here instead. The reply is
        // also the only channel a shutdown can reach them on — they exit when it says so.
        app.MapPost("/bridge/heartbeat", (BridgeRequest req) =>
        {
            Hub.Bridges.Heartbeat(req.Id);
            return Results.Json(new { shutdown = Hub.ShutdownRequested });
        });
        app.MapPost("/bridge/deregister", (BridgeRequest req) =>
        {
            Hub.Bridges.Remove(req.Id);
            return Results.Ok();
        });
        // The live bridge ids (= agent ids). Supervisors poll this to tell a DISCONNECTED agent from a
        // merely idle one — ky-ai-browser releases a tab lease only when its owner drops off this list.
        app.MapGet("/bridges", () => Results.Json(Hub.Bridges.LiveIds(Hub.BridgeLiveWindow)));
        // Tear down the whole stack (every supervisor, then the hub). Mapped for both verbs so it's
        // trivial to hit by hand; this is what `<tool> shutdown` calls.
        app.MapMethods("/shutdown", new[] { "GET", "POST" },
            async () => Results.Content(await Hub.ShutdownAllAsync(), "application/json"));
        app.Urls.Add($"http://127.0.0.1:{port}");

        try
        {
            await app.StartAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{cfg.ToolName} hub: cannot bind port {port} ({ex.Message}). Is a hub already running?");
            return 1;
        }

        Console.WriteLine($"{cfg.ToolName} hub · MCP: http://127.0.0.1:{port}/mcp · register: http://127.0.0.1:{port}/register · shutdown: http://127.0.0.1:{port}/shutdown · Ctrl+C to stop");
        _ = IdleShutdownAsync(app, cfg.ToolName);

        await app.WaitForShutdownAsync();
        return 0;
    }

    // The hub self-exits whenever nothing needs it for a short grace window — no supervisor
    // registered AND no bridge attached. Both count: a supervisor means a dev server is running, a
    // bridge means an MCP client still has the hub open, and either is a reason to stay. That covers
    // the case where nobody ever showed up too (the hub lost a startup race the dev server then won
    // elsewhere, or the serve died before registering), so an orphaned hub can't linger forever.
    // The window also absorbs the cold-start gap: a supervisor registers within a couple of seconds
    // of the hub binding (its register loop isn't gated on the build) and a bridge heartbeats within
    // one interval, so a real client is always counted before the clock elapses.
    //
    // This is unconditional — the hub is auto-managed either way, and the two counters make "is
    // anyone still using me?" a question it can always answer for itself.
    private static async Task IdleShutdownAsync(WebApplication app, string toolName)
    {
        DateTimeOffset? emptySince = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync())
        {
            await Hub.PruneAsync();
            if (Hub.Count > 0 || Hub.LiveBridges > 0) { emptySince = null; continue; }
            // Empty this tick — start the grace clock on the first empty observation and exit once it
            // elapses without anyone (re)appearing.
            emptySince ??= DateTimeOffset.UtcNow;
            if (DateTimeOffset.UtcNow - emptySince > TimeSpan.FromSeconds(20))
            {
                Console.WriteLine($"{toolName} hub · idle, shutting down");
                app.Lifetime.StopApplication();
                return;
            }
        }
    }
}
