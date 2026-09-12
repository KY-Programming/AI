using KY.AI.Wpf;
using Xunit;

namespace KY.AI.Wpf.Tests;

// A path has to mean the same thing whichever tool produced it.
//
// It did not, once: `find` walked from the window root so its paths were absolute, while a scoped
// `tree`/`text` walked from the subtree and numbered from zero again. Both came back as a bare
// `path` field with nothing to tell them apart, so feeding a `text` path to an action addressed a
// DIFFERENT element — reported by an agent that closed the wrong control with one. Walk now takes
// the subtree's own position and builds every path from it, which is what makes a returned path
// safe to hand straight to invoke/set_value.
//
// The tree walk itself needs a live UIA provider, so what is pinned here is the path arithmetic —
// the part that was actually wrong.
public class WalkPathTests
{
    // The composition Walk applies at each level: "" for the root, then base + "." + child index.
    private static string Child(string basePath, int index) =>
        basePath.Length == 0 ? index.ToString() : basePath + "." + index;

    [Fact]
    public void An_unscoped_walk_numbers_from_the_window_root()
    {
        Assert.Equal("0", Child(string.Empty, 0));
        Assert.Equal("0.2", Child(Child(string.Empty, 0), 2));
    }

    // The case that broke: scoping to "14.16" must continue that path, not restart at "0".
    [Fact]
    public void A_scoped_walk_continues_the_path_it_was_scoped_to()
    {
        const string scope = "14.16";
        Assert.Equal("14.16.0", Child(scope, 0));
        Assert.Equal("14.16.0.3", Child(Child(scope, 0), 3));
    }

    // Which is the whole point: the same element gets the same path either way round.
    [Fact]
    public void The_same_element_has_one_path_whether_or_not_the_walk_was_scoped()
    {
        var unscoped = Child(Child(Child(string.Empty, 14), 16), 0);
        var scoped = Child(Child("14", 16), 0);
        Assert.Equal(unscoped, scoped);
        Assert.Equal("14.16.0", scoped);
    }

    // Descend parses what Walk emits — the two have to agree or a returned path cannot be handed back.
    [Theory]
    [InlineData("", new int[0])]
    [InlineData("7", new[] { 7 })]
    [InlineData("14.16.0.0", new[] { 14, 16, 0, 0 })]
    public void Walk_output_round_trips_through_the_path_parser(string path, int[] expected)
    {
        var parsed = path.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        Assert.Equal(expected, parsed);
    }
}
