using KY.AI.Serve;
using Xunit;

namespace KY.AI.Serve.Tests;

// The hub counts live bridges (`<tool> connect` processes) alongside registered supervisors when
// deciding whether anything still needs it — that's what makes the idle-exit safe to have always on.
public class BridgeRegistryTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    [Fact]
    public void A_bridge_counts_once_it_has_beaten_and_is_not_double_counted()
    {
        var reg = new BridgeRegistry();
        Assert.Equal(0, reg.LiveCount(Window));

        reg.Heartbeat("a");
        Assert.Equal(1, reg.LiveCount(Window));

        reg.Heartbeat("a");                       // same bridge beating again
        Assert.Equal(1, reg.LiveCount(Window));

        reg.Heartbeat("b");
        Assert.Equal(2, reg.LiveCount(Window));
    }

    [Fact]
    public void Saying_goodbye_stops_it_counting_immediately()
    {
        var reg = new BridgeRegistry();
        reg.Heartbeat("a");
        reg.Heartbeat("b");

        reg.Remove("a");

        // The point of the goodbye: an idle hub (or a shutdown) doesn't have to wait out the window.
        Assert.Equal(1, reg.LiveCount(Window));
    }

    [Fact]
    public void A_bridge_that_stops_beating_falls_out_of_the_window()
    {
        var reg = new BridgeRegistry();
        reg.Heartbeat("a");

        // A hard-killed bridge never says goodbye, so staleness alone has to stop it holding the hub
        // open. A zero-width window makes any past beat stale.
        Assert.Equal(0, reg.LiveCount(TimeSpan.Zero));
    }

    [Fact]
    public void A_stale_bridge_that_comes_back_counts_again()
    {
        var reg = new BridgeRegistry();
        reg.Heartbeat("a");
        Assert.Equal(0, reg.LiveCount(TimeSpan.Zero));   // pruned by the stale check

        reg.Heartbeat("a");

        Assert.Equal(1, reg.LiveCount(Window));
    }

    [Fact]
    public void Removing_an_unknown_bridge_is_a_no_op()
    {
        var reg = new BridgeRegistry();
        reg.Heartbeat("a");

        reg.Remove("never-seen");

        Assert.Equal(1, reg.LiveCount(Window));
    }
}
