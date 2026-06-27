using KY.AI.Terminal;
using Xunit;

namespace KY.AI.Terminal.Tests;

public class ModeStateTests
{
    [Fact]
    public void Default_mode_is_what_it_was_constructed_with()
    {
        Assert.Equal(TerminalMode.ReadOnly, new ModeState(TerminalMode.ReadOnly).Mode);
        Assert.Equal(TerminalMode.Auto, new ModeState(TerminalMode.Auto).Mode);
    }

    [Fact]
    public void Idle_when_no_input_and_timers_elapsed()
    {
        var m = new ModeState(TerminalMode.Auto) { IdleQuietMs = 0, HumanIdleMs = 0 };
        Assert.True(m.IsIdle());
    }

    [Fact]
    public void Human_typing_blocks_idle_until_line_submitted()
    {
        var m = new ModeState(TerminalMode.Auto) { IdleQuietMs = 0, HumanIdleMs = 0 };
        m.NoteHumanInput(new byte[] { (byte)'l', (byte)'s' });
        Assert.False(m.IsIdle());                       // mid-line
        m.NoteHumanInput(new byte[] { 0x0D });          // Enter submits the line
        Assert.True(m.IsIdle());
    }

    [Fact]
    public void Recent_output_blocks_idle()
    {
        var m = new ModeState(TerminalMode.Auto) { IdleQuietMs = 10_000, HumanIdleMs = 0 };
        m.NoteOutput();
        Assert.False(m.IsIdle());                       // output just now, quiet window not elapsed
    }

    [Fact]
    public void Proposal_queue_stages_and_takes()
    {
        var m = new ModeState(TerminalMode.Suggest);
        Assert.False(m.HasPending);
        var id = m.Propose("git status");
        Assert.True(id > 0);
        Assert.True(m.HasPending);
        Assert.Equal("git status", m.Pending);
        Assert.Equal("git status", m.TakePending());
        Assert.False(m.HasPending);
        Assert.Null(m.TakePending());
    }

    [Fact]
    public void Latest_proposal_replaces_the_previous_one()
    {
        var m = new ModeState(TerminalMode.Suggest);
        m.Propose("first");
        m.Propose("second");
        Assert.Equal("second", m.Pending);
    }
}
