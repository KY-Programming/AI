using System.Text;
using KY.AI.Terminal;
using Xunit;

namespace KY.AI.Terminal.Tests;

public class VtScreenTests
{
    private static void Feed(VtScreen s, string text) => s.Feed(Encoding.ASCII.GetBytes(text));

    [Fact]
    public void Plain_lines_render_on_the_grid()
    {
        var s = new VtScreen(cols: 20, rows: 3, scrollbackLines: 100);
        Feed(s, "hello\r\nworld\r\n");
        Assert.Equal(new[] { "hello", "world" }, s.ScreenLines());
        Assert.Empty(s.ScrollbackTail(10));
    }

    [Fact]
    public void Overflowing_lines_scroll_into_scrollback()
    {
        var s = new VtScreen(cols: 20, rows: 3, scrollbackLines: 100);
        Feed(s, "a\r\nb\r\nc\r\nd\r\n");
        Assert.Equal(new[] { "c", "d" }, s.ScreenLines());
        Assert.Equal(new[] { "a", "b" }, s.ScrollbackTail(10));
    }

    [Fact]
    public void Cursor_addressing_overwrites_in_place()
    {
        var s = new VtScreen(cols: 20, rows: 3, scrollbackLines: 100);
        Feed(s, "abc");
        Feed(s, "[1;1HX");   // home, overwrite 'a'
        Assert.Equal("Xbc", s.ScreenLines()[0]);
        Assert.Equal((0, 1), s.Cursor);
    }

    [Fact]
    public void Erase_line_to_end_clears_the_tail()
    {
        var s = new VtScreen(cols: 20, rows: 3, scrollbackLines: 100);
        Feed(s, "hello");
        Feed(s, "[3D");      // cursor back 3 → over the second 'l'
        Feed(s, "[0K");      // erase to end of line
        Assert.Equal("he", s.ScreenLines()[0]);
    }

    [Fact]
    public void Long_line_wraps_to_next_row()
    {
        var s = new VtScreen(cols: 4, rows: 3, scrollbackLines: 100);
        Feed(s, "abcdef");          // wraps after 4 cols
        var lines = s.ScreenLines();
        Assert.Equal("abcd", lines[0]);
        Assert.Equal("ef", lines[1]);
    }

    [Fact]
    public void Csi_sequence_split_across_feeds_is_handled()
    {
        var s = new VtScreen(cols: 20, rows: 3, scrollbackLines: 100);
        Feed(s, "abc[");       // CSI started but not finished
        Feed(s, "1;1HZ");            // completes: home + write 'Z'
        Assert.Equal("Zbc", s.ScreenLines()[0]);
    }
}
