using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KY.AI.Serve;

// `<tool> connect` — a stdio MCP server Claude Code (or any stdio-only MCP client) spawns fresh
// every session, that transparently proxies to this tool's hub over HTTP. Exists because a raw
// `"type":"http"` .mcp.json entry pointing straight at the hub has a single point of failure: the
// hub only runs while a dev server happens to be attached (SupervisorHost/TerminalHost auto-start
// it with --exit-when-idle), so it's frequently not listening at the exact moment Claude Code
// performs its one-shot startup handshake — and Claude Code doesn't reliably retry that.
//
// This bridge fixes that by owning the flakiness itself instead of exposing it to Claude Code:
//   - the stdio connection Claude Code holds is to THIS process, which has no dependency on the
//     hub to start up, so it's always there;
//   - the upstream hub connection is established lazily, on the first actual tool interaction
//     (ensuring/spawning a *persistent* hub — no --exit-when-idle — if none is reachable yet);
//   - if a forwarded call fails because the hub has since died (crashed, or the dev restarted the
//     whole machine), the bridge respawns it and retries once, transparently.
// So the developer can start/stop/restart the underlying dev tool and even the hub itself at any
// time without ever needing to restart Claude Code.
public static class StdioBridge
{
    public static async Task<int> RunAsync(HubConfig cfg, string[] rest)
    {
        var port = cfg.DefaultPort;
        for (var i = 0; i < rest.Length; i++)
            if (rest[i] == "--port" && ++i < rest.Length && int.TryParse(rest[i], out var p)) port = p;

        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var upstream = new UpstreamConnection(cfg.ToolName, port, endpoint);

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = cfg.ToolName, Version = "1.0.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
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
        await server.RunAsync();
        return 0;
    }

    // Owns the lazy connection to the hub's HTTP /mcp endpoint: ensures the hub is running (spawning
    // a persistent one if not) before the first call, and on a transport failure mid-session, ensures
    // it again and retries the call exactly once before giving up.
    private sealed class UpstreamConnection(string toolName, int port, Uri endpoint)
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private McpClient? client;

        public async Task<T> CallAsync<T>(Func<McpClient, CancellationToken, ValueTask<T>> call, CancellationToken ct)
        {
            try
            {
                return await call(await ConnectedAsync(ct), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
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
    }
}
