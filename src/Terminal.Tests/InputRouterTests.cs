using System.Text;
using KY.AI.Terminal;
using Xunit;

namespace KY.AI.Terminal.Tests;

public class InputRouterTests
{
    private static (string Composer, List<string> Events) Run(params byte[][] feeds)
    {
        var comp = new List<byte>();
        var events = new List<string>();
        var r = new InputRouter(
            toComposer: span => comp.AddRange(span.ToArray()),
            approve: () => events.Add("approve"),
            dismiss: () => events.Add("dismiss"));
        foreach (var f in feeds) r.Feed(f);
        return (Encoding.ASCII.GetString(comp.ToArray()), events);
    }

    [Fact]
    public void Plain_bytes_go_to_the_composer()
    {
        var (comp, ev) = Run(Encoding.ASCII.GetBytes("ls -la"));
        Assert.Equal("ls -la", comp);
        Assert.Empty(ev);
    }

    [Fact]
    public void CtrlE_approves_and_is_not_sent()
    {
        var (comp, ev) = Run(new byte[] { 0x05 });
        Assert.Equal("", comp);
        Assert.Equal(new[] { "approve" }, ev);
    }

    [Fact]
    public void Lone_esc_dismisses()
    {
        var (comp, ev) = Run(new byte[] { 0x1B });
        Assert.Equal("", comp);
        Assert.Equal(new[] { "dismiss" }, ev);
    }

    [Fact]
    public void Arrow_key_csi_goes_to_composer_not_dismiss()
    {
        var (comp, ev) = Run(new byte[] { 0x1B, (byte)'[', (byte)'D' });   // Left arrow
        Assert.Equal("[D", comp[1..]);   // composer received ESC [ D
        Assert.Equal('\x1b', comp[0]);
        Assert.Empty(ev);
    }

    [Fact]
    public void Esc_then_char_dismisses_then_types()
    {
        var (comp, ev) = Run(new byte[] { 0x1B, (byte)'a' });
        Assert.Equal("a", comp);
        Assert.Equal(new[] { "dismiss" }, ev);
    }

    [Fact]
    public void Text_around_a_chord_is_preserved()
    {
        var (comp, ev) = Run(new byte[] { (byte)'x', 0x05, (byte)'y' });
        Assert.Equal("xy", comp);
        Assert.Equal(new[] { "approve" }, ev);
    }
}
