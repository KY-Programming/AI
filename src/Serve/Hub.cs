using System.Text.Json;

namespace KY.AI.Serve;

// Hub-side state shared by the MCP tools: the registry plus a forwarder that proxies a tool
// call to the right supervisor's REST control API. A hub process hosts exactly one tool, so
// the per-process static state (noun wording + shutdown hook) is configured once at startup
// by HubHost.
internal static class Hub
{
    public static HubRegistry Registry { get; } = new();

    public static int Count => Registry.All().Count;

    // Configured by HubHost.RunAsync from the HubConfig.
    public static string Noun = "server";        // singular, e.g. "frontend"
    public static string NounPlural = "servers"; // plural list-payload key, e.g. "frontends"
    public static Action? ShutdownHook;          // stops the hosting WebApplication

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(125) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Forward a request to a named supervisor and return its raw response body. A transient
    // blip is retried; persistent failure is reported as a soft error (left registered so a
    // heartbeat can re-confirm a live app and list/prune removes a genuinely dead one).
    public static async Task<string> ForwardAsync(string? project, HttpMethod method, string path, int timeoutSec)
    {
        var reg = Resolve(project, out var resolveError);
        if (reg is null) return resolveError!;

        var url = reg.ControlUrl.TrimEnd('/') + path;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                using var req = new HttpRequestMessage(method, url);
                using var resp = await Http.SendAsync(req, cts.Token);
                return await resp.Content.ReadAsStringAsync();
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                // Transient control-plane blip — retry a couple of times before giving up.
                await Task.Delay(250);
            }
            catch (HttpRequestException ex)
            {
                return Soft($"{Noun} temporarily unreachable; retry shortly", project, ex.Message);
            }
            catch (OperationCanceledException)
            {
                return Soft($"timed out after {timeoutSec}s waiting for the {Noun}", project, null);
            }
        }
    }

    // Resolve the target supervisor: an explicit name, or — when the name is omitted and exactly
    // one supervisor is registered — that sole supervisor (so single-app workflows skip the name
    // on every call). Otherwise `error` is set to a helpful payload and null is returned.
    private static Registration? Resolve(string? project, out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(project))
        {
            var reg = Registry.Get(project!);
            if (reg is not null) return reg;
            error = UnknownProject(project!);
            return null;
        }

        var all = Registry.All();
        if (all.Count == 1) return all.First();

        error = JsonSerializer.Serialize(new
        {
            error = all.Count == 0 ? $"no {NounPlural} registered" : $"multiple {NounPlural} registered — specify project",
            known = all.Select(r => r.Name).ToArray(),
            hint = $"call list to see the running {NounPlural}",
        }, Json);
        return null;
    }

    private static string Soft(string error, string? project, string? detail) =>
        JsonSerializer.Serialize(new { error, project, detail }, Json);

    // Health-ping every registered supervisor's /status, drop the dead ones, return the rest.
    // detail=false (the default for the `list` tool) returns a compact entry per project — name,
    // running, pid, and the build headline — so `list` stays cheap; detail=true embeds each full
    // /status clone (what `status` with no project returns).
    public static async Task<string> ListAsync(bool detail = false)
    {
        var items = new List<object>();
        foreach (var r in Registry.All())
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var body = await Http.GetStringAsync(r.ControlUrl.TrimEnd('/') + "/status", cts.Token);
                using var doc = JsonDocument.Parse(body);
                items.Add(detail
                    ? new { name = r.Name, controlUrl = r.ControlUrl, status = doc.RootElement.Clone() }
                    : Terse(r.Name, doc.RootElement));
            }
            catch
            {
                Registry.Remove(r.Name);
            }
        }
        return JsonSerializer.Serialize(new Dictionary<string, object> { [NounPlural] = items }, Json);
    }

    // A compact list entry: name + whether it's up + the build's headline (status, error/warning
    // counts, the in-flight building/pending flags). Drops the per-line diagnostic arrays, file
    // lists, log paths and timestamps the full `status` carries — `list`'s job is to name projects
    // and show at-a-glance health, not to dump every diagnostic.
    private static object Terse(string name, JsonElement status)
    {
        var running = status.TryGetProperty("running", out var ru) && ru.ValueKind == JsonValueKind.True;
        int? pid = status.TryGetProperty("pid", out var pe) && pe.ValueKind == JsonValueKind.Number ? pe.GetInt32() : null;
        object? build = null;
        if (status.TryGetProperty("build", out var b) && b.ValueKind == JsonValueKind.Object)
            build = new
            {
                status = b.TryGetProperty("status", out var s) ? s.GetString() : null,
                errors = b.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 0,
                warnings = b.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0,
                building = b.TryGetProperty("building", out var bg) && bg.ValueKind == JsonValueKind.True,
                pending = b.TryGetProperty("pending", out var pn) && pn.ValueKind == JsonValueKind.True,
            };
        return new { name, running, pid, build };
    }

    // Remove supervisors whose /health no longer answers.
    public static async Task PruneAsync()
    {
        foreach (var r in Registry.All())
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var resp = await Http.SendAsync(
                    new HttpRequestMessage(HttpMethod.Get, r.ControlUrl.TrimEnd('/') + "/health"), cts.Token);
                if (!resp.IsSuccessStatusCode) Registry.Remove(r.Name);
            }
            catch { Registry.Remove(r.Name); }
        }
    }

    // Tear down the whole stack: tell every registered supervisor to exit (each deregisters and
    // kills its dev-server tree), then stop the hub itself. This backs `<tool> shutdown`, the
    // shutdown MCP tool, and POST/GET /shutdown — a `shutdown` means "stop everything".
    public static async Task<string> ShutdownAllAsync()
    {
        var regs = Registry.All();
        await Task.WhenAll(regs.Select(async r =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var req = new HttpRequestMessage(HttpMethod.Post, r.ControlUrl.TrimEnd('/') + "/shutdown");
                using var resp = await Http.SendAsync(req, cts.Token);
            }
            catch { /* already gone — nothing to stop */ }
        }));

        // Stop the hub a beat later so this response flushes before the host tears down.
        var hook = ShutdownHook;
        _ = Task.Run(async () => { await Task.Delay(250); hook?.Invoke(); });
        return JsonSerializer.Serialize(
            new { ok = true, stopped = regs.Count, message = $"hub and {regs.Count} supervisor(s) shutting down" }, Json);
    }

    private static string UnknownProject(string project) => JsonSerializer.Serialize(new
    {
        error = $"unknown {Noun}",
        project,
        known = Registry.All().Select(r => r.Name).ToArray(),
        hint = $"call list to see the running {NounPlural}",
    }, Json);
}
