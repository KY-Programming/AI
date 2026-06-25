using KY.AI.Serve;
using Xunit;

namespace KY.AI.Serve.Tests;

// The shared serve/run option parser — focused on --after-start, whose greedy "everything after me
// is the command" rule has to coexist with the unknown-token-goes-to-Extra forwarding.
public class ServeCommandLineTests
{
    [Fact]
    public void AfterStart_captures_all_trailing_tokens_as_the_command()
    {
        var o = ServeCommandLine.Parse(new[] { "--after-start", "ky-ai-browser", "-y" }, 5101);

        Assert.Equal(new[] { "ky-ai-browser", "-y" }, o.AfterStart);
        Assert.Empty(o.Extra);
    }

    [Fact]
    public void Flags_before_after_start_still_parse_and_forward()
    {
        var o = ServeCommandLine.Parse(
            new[] { "--name", "web", "--port", "4015", "--after-start", "ky-ai-browser", "-y" }, 5101);

        Assert.Equal("web", o.Name);
        Assert.Equal(new[] { "--port", "4015" }, o.Extra);   // unknown → forwarded to ng/dotnet
        Assert.Equal(new[] { "ky-ai-browser", "-y" }, o.AfterStart);
    }

    [Fact]
    public void Tokens_after_after_start_are_not_treated_as_known_flags()
    {
        // --no-hub appears AFTER --after-start, so it is part of the command, not a serve flag.
        var o = ServeCommandLine.Parse(new[] { "--after-start", "some-tool", "--no-hub" }, 5101);

        Assert.True(o.UseHub);
        Assert.Equal(new[] { "some-tool", "--no-hub" }, o.AfterStart);
    }

    [Fact]
    public void No_after_start_leaves_it_empty()
    {
        var o = ServeCommandLine.Parse(new[] { "--port", "4015" }, 5101);

        Assert.Empty(o.AfterStart);
    }
}
