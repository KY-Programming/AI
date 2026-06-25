using System.Text;
using System.Text.Json;
using KY.AI.Serve;

namespace KY.AI.Browser;

// ky-ai-browser — read a served app's BROWSER console from an AI agent over MCP.
//
// The dev runs it next to a running `ky-ai-ng serve`. On start it (with the dev's confirmation)
// asks ky-ai-ng to reversibly inject a tiny capture <script> into the app's index.html; the page
// reloads, the snippet patches console.* + error handlers and POSTs events back here; the agent
// reads them via the console_tail MCP tool. On Ctrl+C it asks ky-ai-ng to remove the script again.
// Its lifetime IS the on/off switch — nothing is left behind.
internal static class Program
{
    private const int DefaultPort = 5104;        // ky-ai-browser's own MCP + ingest port (ng=5101..terminal=5103)
    private const int DefaultNgHubPort = 5101;   // where ky-ai-ng's hub lives, to discover the frontend
    private const int BufferCapacity = 1000;

    private static readonly JsonSerializerOptions IngestJson = new() { PropertyNameCaseInsensitive = true };

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected console */ }

        if (args.Any(a => a is "-h" or "--help" or "/?")) { PrintHelp(); return 0; }

        // init: wire ky-ai-browser into a Claude Code workspace (.mcp.json + allow-list), reflecting
        // BrowserTools off this exe's assembly — same shared command the other tools use.
        if (args.Length > 0 && string.Equals(args[0], "init", StringComparison.OrdinalIgnoreCase))
            return InitCommand.Run("ky-ai-browser", DefaultPort, args[1..], typeof(Program).Assembly, runHint: null);

        var port = DefaultPort;
        var ngHubPort = DefaultNgHubPort;
        string? project = null;
        var yes = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port": if (++i < args.Length && int.TryParse(args[i], out var p)) port = p; break;
                case "--ng-hub-port": if (++i < args.Length && int.TryParse(args[i], out var hp)) ngHubPort = hp; break;
                case "--project": if (++i < args.Length) project = args[i]; break;
                case "-y": case "--yes": yes = true; break;
            }
        }

        // 1) Find the running ky-ai-ng frontend to attach to.
        string controlUrl;
        try
        {
            var found = await DiscoverNgAsync(ngHubPort, project);
            if (found is null)
            {
                Console.Error.WriteLine(
                    $"ky-ai-browser: no running ky-ai-ng frontend found (hub :{ngHubPort})." +
                    (project is null ? "" : $" (project '{project}')") +
                    " Start `ky-ai-ng serve` first.");
                return 1;
            }
            controlUrl = found;
        }
        catch (Exception ex) { Console.Error.WriteLine($"ky-ai-browser: {ex.Message}"); return 1; }

        // 2) Confirm the (reversible) manipulation. Default yes; skip with -y or non-interactive stdin.
        if (!yes && !Console.IsInputRedirected)
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

        // 3) Build the capture buffer + the snippet that will post to us (cross-origin → absolute URL).
        var collector = new ConsoleCollector(BufferCapacity, () => Interlocked.Read(ref Capture.BuildSeq));
        Capture.Collector = collector;
        var ingestUrl = $"http://127.0.0.1:{port}/__kyai/console";
        var snippet = LoadSnippet().Replace("__KYAI_TOKEN__", collector.Token).Replace("__KYAI_INGEST__", ingestUrl);
        var scriptTag = $"<script src=\"http://127.0.0.1:{port}/__kyai/console.js\"></script>";

        // 4) Host MCP (console_tail/console_clear) + the snippet + ingest on one loopback port.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly(typeof(Program).Assembly);
        var app = builder.Build();

        app.MapMcp("/mcp");
        app.MapGet("/health", () => Results.Text("ok"));
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
        app.Urls.Add($"http://127.0.0.1:{port}");

        // The heartbeat keeps ky-ai-ng from auto-reverting our inject; cancel it before we uninject so
        // there's no race. On shutdown always revert — Ctrl+C or a crash mid-run leaves index.html clean.
        var hbCts = new CancellationTokenSource();
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            hbCts.Cancel();
            try { UninjectAsync(controlUrl).GetAwaiter().GetResult(); } catch { /* ng gone — it self-heals on next start */ }
        });

        try { await app.StartAsync(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ky-ai-browser: cannot bind port {port} ({ex.Message}). Pass --port <N> for another.");
            return 1;
        }

        // 5) Inject (after our port is bound, so the script URL resolves).
        if (!await InjectAsync(controlUrl, scriptTag))
        {
            Console.Error.WriteLine($"ky-ai-browser: ky-ai-ng could not inject into the app's index.html (is it present?). Detaching.");
            await app.StopAsync();
            return 1;
        }

        // Keep the inject alive (and pull ng's build seq for correlation) until we shut down.
        _ = HeartbeatLoopAsync(controlUrl, hbCts.Token);

        Console.WriteLine($"ky-ai-browser · attached to {controlUrl} · MCP http://127.0.0.1:{port}/mcp · Ctrl+C to detach");
        Console.WriteLine("The app's page will reload to load the capture script; console output now flows to console_tail.");
        await app.WaitForShutdownAsync();
        return 0;
    }

    private static void Cors(HttpContext ctx)
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
    }

    // Look up a registered ky-ai-ng frontend via its hub's /registry. Returns the control URL to
    // inject into, or null if none is registered. Throws (with names) if several match and no --project.
    private static async Task<string?> DiscoverNgAsync(int hubPort, string? project)
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
            .Where(r => !string.IsNullOrEmpty(r.url))
            .ToList();

        if (regs.Count == 0) return null;
        if (project is not null)
            return regs.FirstOrDefault(r => string.Equals(r.name, project, StringComparison.OrdinalIgnoreCase)).url;
        if (regs.Count == 1) return regs[0].url;
        throw new InvalidOperationException(
            $"multiple frontends registered ({string.Join(", ", regs.Select(r => r.name))}); pass --project <name>.");
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
        ky-ai-browser — read a served app's browser console from an AI agent over MCP.

        Run it next to a running `ky-ai-ng serve`:
          ky-ai-browser [options]
            --project <id>      Which ky-ai-ng frontend to attach to (default: the only one registered)
            --port <N>          ky-ai-browser's own MCP + ingest port (default: 5104)
            --ng-hub-port <N>   ky-ai-ng hub port to discover the frontend (default: 5101)
            -y, --yes           Skip the inject confirmation (default answer is yes anyway)

        On start it asks ky-ai-ng to inject a capture <script> into the app's index.html (you confirm;
        default yes); the page reloads and console.*/errors/rejections flow to the `console_tail` MCP
        tool. On Ctrl+C the script is removed and index.html is restored. If ky-ai-browser dies without
        cleaning up, ky-ai-ng strips the leftover automatically. All HTTP is loopback-only.

        INIT — wire ky-ai-browser into a Claude Code workspace (.mcp.json + allow-list):
          ky-ai-browser init [-y] [--dir <path>]
          Finds the nearest .mcp.json and .claude/, then (each step confirmed) adds the MCP server
          (127.0.0.1:5104) and allows console_tail / console_clear. Merges into existing files; safe
          to re-run. Or wire it by hand:
            { "mcpServers": { "ky-ai-browser": { "type": "http", "url": "http://127.0.0.1:5104/mcp" } } }
        """);
    }
}
