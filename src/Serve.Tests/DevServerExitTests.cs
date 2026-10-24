using System.Diagnostics;
using System.IO;
using System.Text.Json;
using KY.AI.Serve;
using Xunit;

namespace KY.AI.Serve.Tests;

// Every way the supervised child goes away must settle the build state at once — a crash (failed,
// with the exit code), a start that fails outright (failed, with the cause), our own stop mid-build
// (stopped) — instead of leaving restart / wait_for_build to run into their timeouts. A child we stop
// ourselves must not be reported as a crash. Spawns real (Windows) child processes.
public class DevServerExitTests
{
    private sealed class StubMatcher : IBuildMatcher
    {
        public bool FirstSettleWins => false;
        public LineKind Classify(string line, bool building) => LineKind.None;
    }

    private static DevServer New(string fileName, params string[] args) => new(
        new SupervisorOptions
        {
            Name = "t",
            WorkingDir = Directory.CreateTempSubdirectory().FullName,
            ChildFileName = fileName,
            ChildArgs = args,
            BannerCommand = "cmd",
            HubUrl = "http://127.0.0.1:1",
            UseHub = false,
            AutostartHub = false,
        },
        Config());

    private static SupervisorConfig Config() => new()
    {
        ToolName = "t",
        Noun = "frontend",
        DefaultHubPort = 1,
        DefaultTimeoutMs = 30000,
        Matcher = new StubMatcher(),
        SourceExtensions = new[] { ".ts" },
        WatchExcludeSegments = new[] { "/node_modules/" },
        WatchRoot = wd => wd,
        ResolveInjectTarget = _ => null,
    };

    private static JsonElement Build(DevServer dev) =>
        JsonDocument.Parse(dev.StatusJson()).RootElement.GetProperty("build").Clone();

    [Fact]
    public async Task Restart_of_a_child_that_dies_immediately_returns_a_failed_verdict_well_inside_the_timeout()
    {
        using var dev = New("cmd", "/c", "echo boom & exit 1");

        var sw = Stopwatch.StartNew();
        var r = await dev.RestartAsync(timeoutMs: 30000, quietMs: 100);

        Assert.True(sw.ElapsedMilliseconds < 10000, $"restart took {sw.ElapsedMilliseconds} ms");
        Assert.False(r.TimedOut);
        Assert.Equal("failed", r.Status);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("cmd exited during startup with code 1", r.SettledBy);
        // The exit is logged after the child's own last words.
        var tail = dev.TailText(0);
        Assert.True(tail.IndexOf("boom") < tail.IndexOf("exited during startup"), tail);
    }

    [Fact]
    public async Task A_child_that_cannot_be_started_returns_a_failed_verdict_instead_of_throwing()
    {
        using var dev = New("ky-ai-no-such-command-" + Guid.NewGuid().ToString("N"));

        var sw = Stopwatch.StartNew();
        var r = await dev.RestartAsync(timeoutMs: 30000, quietMs: 100);

        Assert.True(sw.ElapsedMilliseconds < 5000, $"restart took {sw.ElapsedMilliseconds} ms");
        Assert.False(r.TimedOut);
        Assert.Equal("failed", r.Status);
        Assert.StartsWith("cmd failed to start: ", r.SettledBy);
        Assert.False(dev.Running);
        Assert.Contains("failed to start", dev.TailText(0));
    }

    [Fact]
    public async Task A_stop_mid_build_settles_as_stopped_and_is_not_reported_as_an_exit()
    {
        using var dev = New("cmd", "/c", "ping -n 30 127.0.0.1 >nul");
        dev.Start();
        Assert.True(dev.Running);
        Assert.Equal("building", Build(dev).GetProperty("status").GetString());

        await dev.StopAsync();
        var r = await dev.WaitForBuildAsync(timeoutMs: 5000, quietMs: 100);
        await Task.Delay(300);                    // give a (wrongly) firing Exited handler time to land

        Assert.False(r.TimedOut);
        Assert.Equal("stopped", r.Status);
        var build = Build(dev);
        Assert.Equal("stopped", build.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, build.GetProperty("exitCode").ValueKind);
        Assert.DoesNotContain("exited", dev.TailText(0));
    }

    [Fact]
    public async Task After_start_is_skipped_when_the_dev_server_died_during_its_first_build()
    {
        using var dev = New("cmd", "/c", "exit 1");
        using var launcher = new AfterStartLauncher();
        var marker = Path.Combine(Directory.CreateTempSubdirectory().FullName, "launched");

        var launch = launcher.LaunchAfterBuildAsync(Config(), dev, new[] { "cmd", "/c", "type", "nul", ">", marker }, "t", CancellationToken.None);
        dev.Start();
        await launch.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);                    // a (wrongly) launched command would have written the marker by now

        Assert.False(File.Exists(marker));
    }
}
