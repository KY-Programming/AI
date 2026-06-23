namespace KY.AI.Serve;

// The common options every `serve`/`run` invocation accepts. Tool-specific flags (and the
// child-command building) are handled by each exe after parsing; anything unrecognised lands
// in Extra for the exe to consume (e.g. ng's `--port`, dotnet's `--no-watch`).
public sealed class ServeOptions
{
    public string? LogArg { get; set; }
    public string? Name { get; set; }
    public int LogLines { get; set; } = 200;
    public int ControlPort { get; set; }            // 0 = OS-assigned loopback port
    public string HubUrl { get; set; } = "";
    public bool UseHub { get; set; } = true;
    public bool AutostartHub { get; set; } = true;
    public List<string> Extra { get; } = new();
}

// Parses the flags shared by both tools' supervisor subcommands. Unknown tokens (and a bare
// trailing `*.log` is treated as the log file) are routed to Extra.
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
                case "--log" or "-l" or "--log-file": if (++i < rest.Length) o.LogArg = rest[i]; break;
                case "--name": if (++i < rest.Length) o.Name = rest[i]; break;
                case "--hub": if (++i < rest.Length) o.HubUrl = rest[i]; break;
                case "--log-lines": if (++i < rest.Length && int.TryParse(rest[i], out var n)) o.LogLines = Math.Max(1, n); break;
                case "--control-port": if (++i < rest.Length && int.TryParse(rest[i], out var cp)) o.ControlPort = cp; break;
                case "--no-hub": o.UseHub = false; break;
                case "--no-hub-autostart": o.AutostartHub = false; break;
                default:
                    if (o.LogArg is null && !a.StartsWith('-') && a.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                        o.LogArg = a;
                    else
                        o.Extra.Add(a);
                    break;
            }
        }
        return o;
    }
}
