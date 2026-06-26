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
        var exitWhenIdle = false;
        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--port" && ++i < rest.Length && int.TryParse(rest[i], out var p)) port = p;
            else if (rest[i] == "--exit-when-idle") exitWhenIdle = true;
        }

        Hub.Noun = cfg.Noun;
        Hub.NounPlural = cfg.NounPlural;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly(toolAssembly);

        var app = builder.Build();
        Hub.ShutdownHook = () => app.Lifetime.StopApplication();

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
        if (exitWhenIdle) _ = IdleShutdownAsync(app, cfg.ToolName);

        await app.WaitForShutdownAsync();
        return 0;
    }

    // Auto-started hubs self-exit whenever they sit empty for a short grace window — both after the
    // last supervisor deregisters AND when nobody ever connects (the hub lost a startup race the dev
    // server then won elsewhere, or the serve died before registering). Treating "never had a client"
    // the same as "lost its last client" means an orphaned hub can't linger forever. The window also
    // absorbs the cold-start gap: the supervisor registers within a couple of seconds of the hub
    // binding (its register loop isn't gated on the build), so a real client is always counted before
    // the clock elapses. Kept short so a freed binary can be re-published promptly.
    private static async Task IdleShutdownAsync(WebApplication app, string toolName)
    {
        DateTimeOffset? emptySince = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync())
        {
            await Hub.PruneAsync();
            if (Hub.Count > 0) { emptySince = null; continue; }
            // Empty this tick — start the grace clock on the first empty observation and exit once it
            // elapses without anyone (re)registering.
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
