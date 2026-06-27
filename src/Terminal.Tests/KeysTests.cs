using KY.AI.Terminal;
using Xunit;

namespace KY.AI.Terminal.Tests;

public class KeysTests
{
    [Fact] public void Enter_is_carriage_return() => Assert.Equal(new byte[] { 0x0D }, Keys.Translate("Enter"));
    [Fact] public void Tab_is_0x09() => Assert.Equal(new byte[] { 0x09 }, Keys.Translate("Tab"));
    [Fact] public void CtrlC_is_0x03() => Assert.Equal(new byte[] { 0x03 }, Keys.Translate("Ctrl-C"));
    [Fact] public void Caret_d_is_0x04() => Assert.Equal(new byte[] { 0x04 }, Keys.Translate("^d"));

    [Fact]
    public void Up_arrow_is_csi_A()
        => Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'A' }, Keys.Translate("Up"));

    [Fact]
    public void Sequence_concatenates()
        => Assert.Equal(
            new byte[] { 0x1B, (byte)'[', (byte)'A', 0x1B, (byte)'[', (byte)'A', 0x0D },
            Keys.Translate("Up Up Enter"));

    [Fact] public void Unknown_key_returns_null() => Assert.Null(Keys.Translate("frobnicate"));
    [Fact] public void Empty_is_empty() => Assert.Equal(Array.Empty<byte>(), Keys.Translate(""));
}
