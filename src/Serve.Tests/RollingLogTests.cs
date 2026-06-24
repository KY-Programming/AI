using KY.AI.Serve;
using Xunit;

namespace KY.AI.Serve.Tests;

// The build-aware rolling buffer: seq + kind tagging powering the summary / since-seq / grep
// tails, plus the unchanged plain tail and capacity trimming.
public class RollingLogTests
{
    private static RollingLog Seeded()
    {
        var log = new RollingLog(null, 10);
        log.Add("Changes detected", 1, LineKind.BuildStart);
        log.Add("│ chunk app.js | 714 more lazy chunks", 1, LineKind.None);
        log.Add("[ERROR] TS2304: boom", 1, LineKind.Error);
        log.Add("bundle generation complete", 1, LineKind.SettledSuccess);
        log.Add("Changes detected", 2, LineKind.BuildStart);
        log.Add("[vite] ws proxy error: read ECONNRESET", 2, LineKind.None);
        log.Add("bundle generation complete", 2, LineKind.SettledSuccess);
        return log;
    }

    [Fact]
    public void SummaryOnly_keeps_classified_lines_and_drops_noise()
    {
        var summary = Seeded().Tail(0, summaryOnly: true);

        Assert.DoesNotContain("│ chunk app.js | 714 more lazy chunks", summary);
        Assert.DoesNotContain("[vite] ws proxy error: read ECONNRESET", summary);
        Assert.Contains("[ERROR] TS2304: boom", summary);
        Assert.Contains("bundle generation complete", summary);
    }

    [Fact]
    public void SinceSeq_returns_only_lines_from_builds_at_or_after_the_seq()
    {
        var since2 = Seeded().Tail(0, summaryOnly: false, sinceSeq: 2);

        Assert.Equal(
            new[] { "Changes detected", "[vite] ws proxy error: read ECONNRESET", "bundle generation complete" },
            since2);
    }

    [Fact]
    public void Grep_filters_case_insensitively()
    {
        var grep = Seeded().Tail(0, summaryOnly: false, grep: "error");

        Assert.Equal(
            new[] { "[ERROR] TS2304: boom", "[vite] ws proxy error: read ECONNRESET" },
            grep);
    }

    [Fact]
    public void Summary_combined_with_sinceSeq_scopes_to_one_build()
    {
        var log = Seeded();
        log.Add("[WARNING] deprecated API", 2, LineKind.Warning);

        // Only build 2's classified lines.
        var summary = log.Tail(0, summaryOnly: true, sinceSeq: 2);

        Assert.Equal(
            new[] { "Changes detected", "bundle generation complete", "[WARNING] deprecated API" },
            summary);
    }

    [Fact]
    public void PlainTail_returns_trailing_text_unchanged()
    {
        var plain = Seeded().Tail(2);
        Assert.Equal(new[] { "[vite] ws proxy error: read ECONNRESET", "bundle generation complete" }, plain);
    }

    [Fact]
    public void Capacity_trims_oldest_lines()
    {
        var log = new RollingLog(null, 3);
        for (var i = 0; i < 6; i++) log.Add($"line {i}", 1, LineKind.None);

        Assert.Equal(3, log.Count);
        Assert.Equal(new[] { "line 3", "line 4", "line 5" }, log.Tail(0));
    }
}
