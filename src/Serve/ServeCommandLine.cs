namespace KY.AI.Serve;

// The common options every `serve`/`run` invocation accepts. Tool-specific flags (and the
// child-command building) are handled by each exe after parsing; anything unrecognised lands
// in Extra for the exe to consume (e.g. ng's `--port`, dotnet's `--no-watch`).
public sealed class ServeOptions
{
    public string? LogArg { get; set; }
    public string? Name { get; set; }
    public int LogLines { get; set; } = 200;        // 0 = unlimited
    public int ControlPort { get; set; }            // 0 = OS-assigned loopback port
    public string HubUrl { get; set; } = "";
    public bool UseHub { get; set; } = true;
    public bool AutostartHub { get; set; } = true;  // always on (the hub is auto-managed)
    public List<string> Extra { get; } = new();
}

// Parses the flags shared by both tools' supervisor subcommands. Everything loopback-only, so
// hub/control are given as bare ports. Unrecognised tokens are routed to Extra for the exe to
// consume (e.g. ng's `--port`, dotnet's `--no-watch`).
public static class ServeCommandLine
{
    public static ServeOptions Parse(string[] rest, int defaultHubPort)
    {
        var o = new ServeOptions { HubUrl = $"http://127.0.0.1:{defaultHubPort}" };
        for (var i = 0; i < rest.Length; i++)
        {
            var a = rest[i];
            switch (a)
            {
                case "--log-file": if (++i < rest.Length) o.LogArg = rest[i]; break;
                case "--name": if (++i < rest.Length) o.Name = rest[i]; break;
                case "--hub-port": if (++i < rest.Length && int.TryParse(rest[i], out var hp)) o.HubUrl = $"http://127.0.0.1:{hp}"; break;
                case "--log-lines": if (++i < rest.Length && int.TryParse(rest[i], out var n)) o.LogLines = Math.Max(0, n); break;  // 0 = unlimited
                case "--rest-port": if (++i < rest.Length && int.TryParse(rest[i], out var cp)) o.ControlPort = cp; break;
                case "--no-hub": o.UseHub = false; break;
                default: o.Extra.Add(a); break;
            }
        }
        return o;
    }
}
