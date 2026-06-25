using KY.AI.Serve;
using Xunit;

namespace KY.AI.Serve.Tests;

// The generic, reversible HTML injector that backs the supervisor's /inject + /uninject (and the
// self-heal that strips a leftover tag on startup).
public class HtmlInjectorTests
{
    private const string M = "ky-ai-ng-inject";

    [Fact]
    public void Apply_appends_marked_block_inside_head()
    {
        var html = "<html><head><title>x</title></head><body></body></html>";
        var r = HtmlInjector.Apply(html, "/html/head", "<script src=\"x.js\"></script>", M);

        Assert.NotNull(r);
        Assert.Contains(HtmlInjector.Begin(M) + "<script src=\"x.js\"></script>" + HtmlInjector.End(M), r);
        Assert.True(r!.IndexOf(M, System.StringComparison.Ordinal) < r.IndexOf("</head>", System.StringComparison.Ordinal));
        Assert.True(r.IndexOf("<title>", System.StringComparison.Ordinal) < r.IndexOf(M, System.StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_into_body_before_body_close()
    {
        var html = "<html><head></head><body><div></div></body></html>";
        var r = HtmlInjector.Apply(html, "/html/body", "<b>", M)!;
        Assert.True(r.IndexOf(M, System.StringComparison.Ordinal) < r.IndexOf("</body>", System.StringComparison.Ordinal));
        Assert.True(r.IndexOf("<div>", System.StringComparison.Ordinal) < r.IndexOf(M, System.StringComparison.Ordinal));
    }

    [Fact]
    public void Remove_round_trips_back_to_the_original()
    {
        var html = "<html><head><title>x</title></head><body></body></html>";
        var injected = HtmlInjector.Apply(html, "/html/head", "<script></script>", M)!;
        Assert.NotEqual(html, injected);
        Assert.Equal(html, HtmlInjector.Remove(injected, M));
    }

    [Fact]
    public void Apply_is_idempotent()
    {
        var html = "<html><head></head></html>";
        var once = HtmlInjector.Apply(html, "/html/head", "<script></script>", M)!;
        var twice = HtmlInjector.Apply(once, "/html/head", "<script></script>", M)!;
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Contains_reflects_state()
    {
        var html = "<html><head></head></html>";
        Assert.False(HtmlInjector.Contains(html, M));
        Assert.True(HtmlInjector.Contains(HtmlInjector.Apply(html, "/html/head", "<s>", M)!, M));
    }

    [Fact]
    public void Unsupported_path_returns_null()
        => Assert.Null(HtmlInjector.Apply("<html></html>", "/html/footer", "<s>", M));

    [Fact]
    public void Remove_without_marker_is_unchanged()
    {
        const string html = "<html><head></head></html>";
        Assert.Equal(html, HtmlInjector.Remove(html, M));
    }

    [Fact]
    public void Apply_targets_real_head_not_header_lookalike()
    {
        var html = "<html><head></head><body><header>nav</header></body></html>";
        var r = HtmlInjector.Apply(html, "/html/head", "<s>", M)!;
        Assert.True(r.IndexOf(M, System.StringComparison.Ordinal) < r.IndexOf("</head>", System.StringComparison.Ordinal));
        Assert.True(r.IndexOf("</head>", System.StringComparison.Ordinal) < r.IndexOf("<header>", System.StringComparison.Ordinal));
    }
}
