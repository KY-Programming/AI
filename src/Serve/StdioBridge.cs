using System.Net.Http.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KY.AI.Serve;

// `<tool> connect` — a stdio MCP server the agent (Claude Code, …) spawns fresh every session, that
// transparently proxies to this tool's hub over HTTP. It exists because pointing an MCP client
// straight at the hub's URL has a single point of failure: the hub is auto-managed and only runs
// while something needs it, so it's frequently not listening at the exact moment the client performs
// its one-shot startup handshake — and a client that fails that handshake gives up for the whole
// session, which is why the tools kept needing a client restart.
//
// The bridge fixes that by being a process the CLIENT owns:
//   - the client spawns it, so it can never be missing and can never lose a startup race;
//   - the hub is connected lazily, on the first real tool interaction, and auto-started if needed —
//     so the dev can start/stop/restart their dev servers whenever they like;
//   - if a forwarded call fails because the hub went away, the bridge reconnects and retries once,
//     transparently.
// Net effect: nothing the developer does to the dev tools requires restarting their agent.
//
// While connected it heartbeats the hub, which is both how an idle hub knows someone still has it
// open (so it doesn't self-exit under a live session) and the only channel a `<tool> shutdown` has
// to reach a process with no listener of its own — see Hub.ShutdownAllAsync.
public static class StdioBridge
{
    public static async Task<int> RunAsync(HubConfig cfg, string[] rest)
    {
        var port = cfg.DefaultPort;
        for (var i = 0; i < rest.Length; i++)
            if (rest[i] == "--port" && ++i < rest.Length && int.TryParse(rest[i], out var p)) port = p;

        // Tripped when the hub tells us (via the heartbeat) that a shutdown is underway; unblocks
        // RunAsync below so this process exits with it instead of being left holding the tool's exe.
        using var stopping = new CancellationTokenSource();
        await using var upstream = new UpstreamConnection(cfg.ToolName, port, stopping);

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = cfg.ToolName, Version = "1.0.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                // Both handlers just pass the hub's own answer through — the bridge deliberately
                // knows nothing about the tools, so whatever the hub currently exposes is what the
                // client sees.
                ListToolsHandler = async (ctx, ct) =>
                {
                    var tools = await upstream.CallAsync((c, t) => c.ListToolsAsync(cancellationToken: t), ct);
                    return new ListToolsResult { Tools = tools.Select(t => t.ProtocolTool).ToList() };
                },
                CallToolHandler = async (ctx, ct) =>
                {
                    var args = ctx.Params!.Arguments?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
                    return await upstream.CallAsync<CallToolResult>((c, t) => c.CallToolAsync(
                        ctx.Params.Name, args, cancellationToken: t), ct);
                },
            },
        };

        await using var server = McpServer.Create(new StdioServerTransport(options), options);
        try
        {
            await server.RunAsync(stopping.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The hub is shutting the stack down and we went with it — a normal exit, not a fault.
        }
        return 0;
    }

    // Owns the connection to the hub's HTTP /mcp endpoint: ensures a hub is running (starting one if
    // not) before the first call, keeps it aware of us with a heartbeat, and on a transport failure
    // mid-session ensures it again and retries the call once before giving up.
    private sealed class UpstreamConnection(string toolName, int port, CancellationTokenSource stopping) : IAsyncDisposable
    {
        private readonly string id = $"{toolName}-{Environment.ProcessId}";
        private readonly Uri endpoint = new($"http://127.0.0.1:{port}/mcp");
        private readonly string hubUrl = $"http://127.0.0.1:{port}";
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
        private McpClient? client;
        private Task? heartbeat;

        public async Task<T> CallAsync<T>(Func<McpClient, CancellationToken, ValueTask<T>> call, CancellationToken ct)
        {
            try
            {
                return await call(await ConnectedAsync(ct), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // The hub died under us (crashed, or the dev cycled it). Drop the dead client and
                // let the retry bring a fresh hub up — this is the path that keeps a long-lived
                // session working across hub restarts.
                await ResetAsync();
                return await call(await ConnectedAsync(ct), ct);
            }
        }

        private async Task<McpClient> ConnectedAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (client is not null) return client;
                await HubLifecycle.EnsureRunningAsync(toolName, port, TimeSpan.FromSeconds(15));
                var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = endpoint });
                client = await McpClient.CreateAsync(transport, cancellationToken: ct);
                // Only once we've actually got a hub — before that there'd be nothing to talk to.
                heartbeat ??= HeartbeatLoopAsync();
                return client;
            }
            finally { gate.Release(); }
        }

        private async Task ResetAsync()
        {
            await gate.WaitAsync();
            try { client = null; }
            finally { gate.Release(); }
        }

        // Check in with the hub until we're told to stop. A failed beat means the hub is gone; that's
        // not our problem to fix here — the next real call re-ensures it — so just keep beating.
        private async Task HeartbeatLoopAsync()
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    using var resp = await http.PostAsJsonAsync($"{hubUrl}/bridge/heartbeat", new BridgeRequest(id), stopping.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        var beat = await resp.Content.ReadFromJsonAsync<HeartbeatReply>(stopping.Token);
                        if (beat?.Shutdown == true)
                        {
                            await stopping.CancelAsync();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { /* hub not up (or not up yet) — the next call re-ensures it */ }

                try { await Task.Delay(Hub.BridgeHeartbeatInterval, stopping.Token); }
                catch { return; }
            }
        }

        // Say goodbye so the hub stops counting us immediately — it's what lets an idle hub wind down
        // (and a `shutdown` complete) without waiting out the liveness window.
        public async ValueTask DisposeAsync()
        {
            if (!stopping.IsCancellationRequested) await stopping.CancelAsync();
            try { if (heartbeat is not null) await heartbeat; } catch { /* already stopping */ }
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var resp = await http.PostAsJsonAsync($"{hubUrl}/bridge/deregister", new BridgeRequest(id), cts.Token);
            }
            catch { /* hub already gone — nothing to tell */ }
            if (client is not null) await client.DisposeAsync();
            http.Dispose();
        }

        private sealed record HeartbeatReply(bool Shutdown);
    }
}
