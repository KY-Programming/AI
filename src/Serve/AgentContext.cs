namespace KY.AI.Serve;

// Carries the calling agent's identity from the hub's HTTP boundary down into a tool call, without
// any tool having to declare it as a parameter (so it never reaches the model). A `<tool> connect`
// bridge stamps its own id on every forwarded /mcp request as the X-KYAI-Agent header; HubHost's
// middleware lifts it into this AsyncLocal for the duration of the request, and Hub.ForwardAsync
// re-attaches it to the outgoing call to the supervisor — so a capture instance can tell which agent
// a click/start_interaction came from.
//
// This works because the streamable-HTTP transport runs a tool handler under the ExecutionContext of
// the HTTP request that delivered the tools/call (PerSessionExecutionContext defaults to false), so an
// AsyncLocal set in that request's middleware is visible inside the tool. A plain MCP client that talks
// straight to the hub (no bridge) sends no header ⇒ Current stays null ⇒ everything degrades to the
// single-implicit-agent behaviour the tools had before.
public static class AgentContext
{
    public const string Header = "X-KYAI-Agent";

    private static readonly AsyncLocal<string?> _current = new();

    public static string? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
