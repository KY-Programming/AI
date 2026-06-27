using KY.AI.Terminal;
using Xunit;

namespace KY.AI.Terminal.Tests;

public class ShellResolverTests
{
    [Fact]
    public void Cmd_resolves_to_cmd_exe()
    {
        var r = ShellResolver.Resolve("cmd", Array.Empty<string>());
        Assert.Equal("cmd", r.Display);
        Assert.EndsWith("cmd.exe", r.ExePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("\"", r.CommandLine);                 // exe is quoted
    }

    [Fact]
    public void Ssh_passes_target_through_in_command_line()
    {
        var r = ShellResolver.Resolve("ssh", new[] { "user@host" });
        Assert.Equal("ssh", r.Display);
        Assert.Contains("user@host", r.CommandLine);
    }

    [Fact]
    public void Default_picks_a_real_shell()
    {
        var r = ShellResolver.Resolve(null, Array.Empty<string>());
        Assert.False(string.IsNullOrWhiteSpace(r.Display));
        Assert.False(string.IsNullOrWhiteSpace(r.ExePath));
    }

    [Fact]
    public void Args_with_spaces_are_quoted()
    {
        var r = ShellResolver.Resolve("ssh", new[] { "-o", "ProxyCommand=foo bar" });
        Assert.Contains("\"ProxyCommand=foo bar\"", r.CommandLine);
        Assert.Contains("-o", r.CommandLine);
    }
}
