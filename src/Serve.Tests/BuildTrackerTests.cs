using KY.AI.Net;
using KY.AI.Ng;
using KY.AI.Serve;
using Xunit;

namespace KY.AI.Serve.Tests;

// The shared build-verdict logic: warning counting, change→build correlation, the (kind, seq)
// observation contract, and structured-diagnostic assembly (including esbuild's two-line shape,
// which the tracker stitches together from a header line plus a standalone location line).
public class BuildTrackerTests
{
    // A controllable matcher: line prefixes map to kinds, no diagnostic parsing.
    private sealed class FakeMatcher : IBuildMatcher
    {
        public bool FirstSettleWins => false;
        public LineKind Classify(string line, bool building) =>
            line.StartsWith("START") ? LineKind.BuildStart :
            line.StartsWith("ERR") ? LineKind.Error :
            line.StartsWith("WARN") ? LineKind.Warning :
            line.StartsWith("OK") ? LineKind.SettledSuccess :
            line.StartsWith("FAIL") ? LineKind.SettledFailed :
            LineKind.None;
    }

    [Fact]
    public void Observe_returns_kind_and_the_seq_the_line_belongs_to()
    {
        var t = new BuildTracker(new FakeMatcher());

        var (k1, s1) = t.Observe("START one");
        var (k2, s2) = t.Observe("OK done");
        var (k3, s3) = t.Observe("START two");

        Assert.Equal(LineKind.BuildStart, k1);
        Assert.Equal(1, s1);
        Assert.Equal(LineKind.SettledSuccess, k2);
        Assert.Equal(1, s2);          // settle stays in the same cycle
        Assert.Equal(LineKind.BuildStart, k3);
        Assert.Equal(2, s3);          // a new build increments the seq
    }

    [Fact]
    public void Warnings_are_counted_separately_from_errors()
    {
        var t = new BuildTracker(new FakeMatcher());
        t.Observe("START");
        t.Observe("WARN deprecated: allowSignalWrites");
        t.Observe("WARN another");
        t.Observe("ERR boom");
        t.Observe("OK done");

        var r = t.Snapshot();

        Assert.Equal("success", r.Status);
        Assert.Equal(1, r.Errors);
        Assert.Equal(2, r.Warnings);
        Assert.Contains("WARN deprecated: allowSignalWrites", r.WarningLines);
        Assert.Single(r.ErrorLines);
    }

    [Fact]
    public void Files_changed_since_the_prior_build_are_reported_as_this_build_s_inputs()
    {
        var t = new BuildTracker(new FakeMatcher());
        t.NoteSourceChange("src/app/a.ts");
        t.NoteSourceChange("src/app/b.ts");
        t.Observe("START");           // begins a build → captures the changed set
        t.Observe("OK done");

        var r = t.Snapshot();

        Assert.Equal(new[] { "src/app/a.ts", "src/app/b.ts" }, r.FilesInLastBuild.OrderBy(x => x));
        Assert.NotNull(r.LastChangeAt);
    }

    [Fact]
    public void Dotnet_single_line_diagnostic_is_parsed_with_location()
    {
        var t = new BuildTracker(new DotnetBuildMatcher());
        t.MarkBuilding();             // dotnet only counts diagnostics while building
        t.Observe(@"C:\proj\File.cs(12,34): error CS0103: The name 'foo' does not exist [C:\proj\proj.csproj]");

        var d = Assert.Single(t.Snapshot().Diagnostics);

        Assert.Equal("error", d.Severity);
        Assert.Equal(@"C:\proj\File.cs", d.File);
        Assert.Equal(12, d.Line);
        Assert.Equal(34, d.Column);
        Assert.Equal("The name 'foo' does not exist", d.Message);
        Assert.Contains("CS0103", d.Raw);
    }

    [Fact]
    public void Esbuild_two_line_diagnostic_is_correlated_into_one()
    {
        var t = new BuildTracker(new NgBuildMatcher());
        t.Observe("Changes detected. Rebuilding...");
        t.Observe("✘ [ERROR] TS2304: Cannot find name 'foo'. [plugin angular-compiler]");
        t.Observe("");                // esbuild's blank line between message and location
        t.Observe("    src/app/app.component.ts:12:34:");
        t.Observe("bundle generation failed");

        var r = t.Snapshot();
        var d = Assert.Single(r.Diagnostics);

        Assert.Equal("failed", r.Status);
        Assert.Equal("error", d.Severity);
        Assert.Equal("src/app/app.component.ts", d.File);   // backfilled from the location line
        Assert.Equal(12, d.Line);
        Assert.Equal(34, d.Column);
        Assert.Contains("Cannot find name 'foo'", d.Message);
    }

    [Fact]
    public void A_new_build_clears_the_previous_diagnostics_and_warnings()
    {
        var t = new BuildTracker(new FakeMatcher());
        t.Observe("START");
        t.Observe("ERR boom");
        t.Observe("FAIL");
        t.Observe("START");           // fresh cycle
        t.Observe("OK");

        var r = t.Snapshot();
        Assert.Equal("success", r.Status);
        Assert.Empty(r.Diagnostics);
        Assert.Equal(0, r.Errors);
    }

    // ── stale-after-build hint (mayHaveStaleInstances) ──

    private static readonly string[] HotSafe = { ".html", ".scss", ".css" };

    [Fact]
    public void Stale_hint_fires_when_an_incremental_rebuild_changed_code()
    {
        var t = new BuildTracker(new FakeMatcher(), HotSafe);
        t.NoteSourceChange("src/app/wire.ts");
        t.Observe("START");           // incremental (startedBy non-null)
        t.Observe("OK");

        var r = t.Snapshot();
        Assert.True(r.MayHaveStaleInstances);
        Assert.NotNull(r.StaleHint);
    }

    [Fact]
    public void Stale_hint_silent_for_template_or_style_only_changes()
    {
        var t = new BuildTracker(new FakeMatcher(), HotSafe);
        t.NoteSourceChange("src/app/wire.component.html");
        t.NoteSourceChange("src/app/wire.component.scss");
        t.Observe("START");
        t.Observe("OK");

        var r = t.Snapshot();
        Assert.False(r.MayHaveStaleInstances);
        Assert.Null(r.StaleHint);
    }

    [Fact]
    public void Stale_hint_silent_on_cold_start_even_for_code_changes()
    {
        var t = new BuildTracker(new FakeMatcher(), HotSafe);
        t.NoteSourceChange("src/app/wire.ts");
        t.MarkBuilding();             // cold start → page fully reloads, no stale instances
        t.Observe("OK");

        Assert.False(t.Snapshot().MayHaveStaleInstances);
    }

    [Fact]
    public void Stale_hint_silent_when_the_tool_did_not_opt_in()
    {
        var t = new BuildTracker(new FakeMatcher());   // no hot-reload-safe extensions
        t.NoteSourceChange("src/app/wire.ts");
        t.Observe("START");
        t.Observe("OK");

        Assert.False(t.Snapshot().MayHaveStaleInstances);
    }

    // ── change→build attribution (closes the watcher-lag race) ──

    [Fact]
    public void A_change_event_that_lagged_into_the_build_is_still_attributed_to_it()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var t = new BuildTracker(new FakeMatcher(), clock: () => now);

        t.Observe("START");                       // dev server detected the change first…
        now = now.AddMilliseconds(500);
        t.NoteSourceChange("src/app/late.ts");    // …our watcher event arrives 500ms into the build
        t.Observe("OK");

        Assert.Contains("src/app/late.ts", t.Snapshot().FilesInLastBuild);
    }

    [Fact]
    public void A_change_after_the_grace_belongs_to_the_next_build_not_the_current_one()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var t = new BuildTracker(new FakeMatcher(), clock: () => now);

        t.Observe("START");
        now = now.AddSeconds(3);                  // a genuine later edit while the build was running
        t.NoteSourceChange("src/app/next.ts");
        t.Observe("OK");
        Assert.DoesNotContain("src/app/next.ts", t.Snapshot().FilesInLastBuild);

        t.Observe("START");                       // the next build picks it up
        t.Observe("OK");
        Assert.Contains("src/app/next.ts", t.Snapshot().FilesInLastBuild);
    }
}
