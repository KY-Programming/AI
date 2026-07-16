using System.Text;
using System.Text.Json;
using KY.AI.Serve;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace KY.AI.Terminal;

// Hosts one terminal session: a loopback REST control API the hub forwards agent calls to, plus
// hub registration on a heartbeat and auto-start of a hub if none is up. Mirrors Serve's
// SupervisorHost, but the supervised thing is a ConPTY shell rather than a build. Console writes
// happen only BEFORE the session enters raw passthrough (afterwards the console belongs to the
// shell), so status goes to stderr up front and the loops stay silent.
internal static class TerminalHost
{
    public sealed record Options(string ToolName, int ControlPort, string HubUrl, bool UseHub, bool AutostartHub, int DefaultHubPort);

    public static async Task<int> RunAsync(TerminalSession session, Options opt)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.MapGet("/health", () => Results.Text("ok"));
        app.MapGet("/status", () => Results.Content(session.StatusJson(), "application/json"));
        app.MapGet("/screen", () => Results.Content(session.ScreenJson(), "application/json"));
        app.MapGet("/scrollback", (int? lines) => Results.Content(session.ScrollbackJson(lines ?? 0), "application/json"));
        app.MapGet("/mode", () => Results.Content(session.ModeJson(), "application/json"));
        app.MapPost("/mode", (string mode) => Results.Content(session.SetMode(mode), "application/json"));
        app.MapPost("/send-text", async (string text) => Results.Content(await session.InjectTextAsync(text), "application/json"));
        app.MapPost("/send-keys", (string keys) => Results.Content(session.InjectKeys(keys), "application/json"));
        app.MapPost("/resize", (int cols, int rows) => Results.Content(session.ResizeRequest(cols, rows), "application/json"));
        app.MapPost("/propose", (string text) => Results.Content(session.ProposeJson(text), "application/json"));
        // approve/dismiss are human/local actions — not exposed as MCP tools.
        app.MapPost("/approve", () => Results.Content(session.ApproveJson(), "application/json"));
        app.MapPost("/dismiss", () => Results.Content(session.DismissJson(), "application/json"));
        app.MapGet("/audit", (int? lines) => Results.Content(session.AuditJson(lines ?? 0), "application/json"));
        // End this session (the hub calls this when tearing the whole stack down). Delayed a beat
        // so the response reaches the hub; RequestStop restores the console and unblocks WaitForExit.
        app.MapPost("/shutdown", () =>
        {
            _ = Task.Run(async () => { await Task.Delay(250); session.RequestStop(); });
            return Results.Content("{\"ok\":true,\"action\":\"shutdown\"}", "application/json");
        });
        app.Urls.Add($"http://127.0.0.1:{opt.ControlPort}");

        try
        {
            await app.StartAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{opt.ToolName}: cannot bind control port {opt.ControlPort} ({ex.Message}).");
            return 1;
        }

        var controlUrl = GetBoundUrl(app) ?? $"http://127.0.0.1:{opt.ControlPort}";
        var stopping = new CancellationTokenSource();

        if (opt.UseHub)
        {
            if (opt.AutostartHub && !await HubLifecycle.HubReachableAsync(opt.HubUrl))
            {
                Console.Error.WriteLine($"{opt.ToolName} · hub not reachable — auto-starting it on port {opt.DefaultHubPort}");
                HubLifecycle.TryLaunchHub(opt.ToolName, opt.DefaultHubPort, exitWhenIdle: true);
            }
            Console.Error.WriteLine($"{opt.ToolName} · session '{session.Name}' · {session.ShellDisplay} · mode {session.Mode.Wire()} · control {controlUrl} · hub {opt.HubUrl}");
            _ = RegisterLoopAsync(opt.HubUrl, session.Name, controlUrl, stopping.Token);
        }
        else
        {
            Console.Error.WriteLine($"{opt.ToolName} · session '{session.Name}' · {session.ShellDisplay} · control {controlUrl} · standalone (--no-hub)");
        }

        try
        {
            session.Start();                                  // raw passthrough begins here
            return await Task.Run(session.WaitForExit);
        }
        finally
        {
            stopping.Cancel();
            try { if (opt.UseHub) await DeregisterAsync(opt.HubUrl, session.Name); } catch { /* hub gone */ }
            session.Dispose();                                // restores console + reaps the shell tree
            try { await app.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
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

}
