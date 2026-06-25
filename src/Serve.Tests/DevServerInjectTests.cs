using System.IO;
using System.Text.Json;
using KY.AI.Serve;
using Xunit;

namespace KY.AI.Serve.Tests;

// The supervisor side of the dev-controlled inject: InjectJson/UninjectJson against a real file, and
// the self-heal that strips a leftover marker on construction (no child process is started).
public class DevServerInjectTests
{
    private sealed class StubMatcher : IBuildMatcher
    {
        public bool FirstSettleWins => false;
        public LineKind Classify(string line, bool building) => LineKind.None;
    }

    private static DevServer New(string workingDir, string? injectTarget) => new(
        new SupervisorOptions
        {
            Name = "t",
            WorkingDir = workingDir,
            ChildFileName = "dotnet",
            ChildArgs = new[] { "--version" },
            BannerCommand = "noop",
            HubUrl = "http://127.0.0.1:1",
            UseHub = false,
            AutostartHub = false,
        },
        new SupervisorConfig
        {
            ToolName = "t",
            Noun = "frontend",
            DefaultHubPort = 1,
            Matcher = new StubMatcher(),
            SourceExtensions = new[] { ".ts" },
            WatchExcludeSegments = new[] { "/node_modules/" },
            WatchRoot = wd => wd,
            ResolveInjectTarget = _ => injectTarget,
        });

    [Fact]
    public void Inject_then_uninject_round_trips_the_file()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var idx = Path.Combine(dir, "index.html");
        const string original = "<html><head></head><body></body></html>";
        File.WriteAllText(idx, original);

        using var dev = New(dir, idx);

        Assert.Contains("\"ok\":true", dev.InjectJson(null, "/html/head", "<script src=\"x\"></script>"));
        var after = File.ReadAllText(idx);
        Assert.Contains("ky-ai-ng-inject:begin", after);
        Assert.Contains("<script src=\"x\"></script>", after);

        dev.UninjectJson();
        Assert.Equal(original, File.ReadAllText(idx));
    }

    [Fact]
    public void Constructor_self_heals_a_leftover_marker()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var idx = Path.Combine(dir, "index.html");
        File.WriteAllText(idx,
            "<html><head>\n<!-- ky-ai-ng-inject:begin --><script></script><!-- ky-ai-ng-inject:end -->\n</head></html>");

        using var dev = New(dir, idx);   // ctor RevertInject() should strip it

        Assert.DoesNotContain("ky-ai-ng-inject", File.ReadAllText(idx));
    }

    [Fact]
    public void Inject_reports_error_when_the_tool_has_no_target()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        using var dev = New(dir, injectTarget: null);   // dotnet-like: no inject target
        Assert.Contains("\"ok\":false", dev.InjectJson(null, "/html/head", "<s>"));
    }

    [Fact]
    public void Heartbeat_reports_active_after_inject_then_inactive_after_uninject()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var idx = Path.Combine(dir, "index.html");
        File.WriteAllText(idx, "<html><head></head></html>");
        using var dev = New(dir, idx);

        dev.InjectJson(null, "/html/head", "<script></script>");
        using (var d = JsonDocument.Parse(dev.InjectHeartbeatJson()))
            Assert.True(d.RootElement.GetProperty("active").GetBoolean());

        dev.UninjectJson();
        using (var d = JsonDocument.Parse(dev.InjectHeartbeatJson()))
            Assert.False(d.RootElement.GetProperty("active").GetBoolean());
    }
}
