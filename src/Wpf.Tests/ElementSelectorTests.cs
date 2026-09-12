using KY.AI.Wpf;
using Xunit;

namespace KY.AI.Wpf.Tests;

// `find` and every action tool share one selector, so what a selector means is fixed here rather
// than in six places that could drift apart.
public class ElementSelectorTests
{
    private static NodeInfo Node(string path, string type, string? id = null, string? name = null, string? value = null) =>
        new(path, path.Length == 0 ? 0 : path.Count(c => c == '.') + 1, type, id, name, value, true, false, null, null, 0);

    // Modelled on KY.ProjectHub: one AutomationId per row KIND, one Name per row.
    private static readonly List<NodeInfo> Rows =
    [
        Node("0", "Edit", "SearchBox", "Search projects, solutions and folders", "switchboard"),
        Node("1.0", "Button", "SolutionRow", "Switchboard"),
        Node("1.1", "Button", "SolutionRow", "Garden"),
        Node("1.2", "Button", "ToolRow", "Rider"),
        Node("1.3", "Text", null, "487 projects"),
    ];

    [Fact]
    public void Criteria_and_together()
    {
        var (node, count) = new ElementSelector(AutomationId: "SolutionRow", Name: "Garden").Pick(Rows);
        Assert.Equal("1.1", node!.Path);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Matching_is_case_insensitive_and_trims()
    {
        var (node, _) = new ElementSelector(AutomationId: " solutionrow ", Name: "switchboard").Pick(Rows);
        Assert.Equal("1.0", node!.Path);
    }

    [Fact]
    public void Path_wins_over_every_other_field()
    {
        // A path names one node outright, so a stale extra criterion cannot redirect the action
        // somewhere else — it is simply ignored.
        var (node, _) = new ElementSelector(Path: "1.2", AutomationId: "SolutionRow").Pick(Rows);
        Assert.Equal("1.2", node!.Path);
    }

    [Fact]
    public void Contains_spans_name_automation_id_and_value()
    {
        // "switchb" is in the row's Name AND in the search box's typed-in Value, so both match —
        // which is the whole point of contains spanning all three fields.
        Assert.Equal(2, new ElementSelector(Contains: "switchb").Pick(Rows).Matches);
        Assert.Equal("1.0", new ElementSelector(Contains: "switchb", ControlType: "Button").Pick(Rows).Node!.Path);
        Assert.Equal("0", new ElementSelector(Contains: "searchbo").Pick(Rows).Node!.Path);    // by automationId
        Assert.Equal(3, new ElementSelector(Contains: "row", ControlType: "Button").Pick(Rows).Matches);   // all three *Row buttons
    }

    [Fact]
    public void Control_type_filters_without_the_ControlType_prefix()
    {
        var (_, count) = new ElementSelector(ControlType: "button").Pick(Rows);
        Assert.Equal(3, count);
    }

    // The safety rule behind Engine.Act: an ambiguous selector must not silently take the first
    // match, so Pick reports how many matched and only an explicit index chooses one.
    [Fact]
    public void Index_picks_among_several_matches_in_tree_order()
    {
        var selector = new ElementSelector(AutomationId: "SolutionRow");
        Assert.Equal(2, selector.Pick(Rows).Matches);
        Assert.Equal("1.0", (selector with { Index = 0 }).Pick(Rows).Node!.Path);
        Assert.Equal("1.1", (selector with { Index = 1 }).Pick(Rows).Node!.Path);
    }

    [Fact]
    public void Index_past_the_end_yields_no_node_but_still_reports_the_count()
    {
        var (node, count) = new ElementSelector(AutomationId: "SolutionRow", Index: 9).Pick(Rows);
        Assert.Null(node);
        Assert.Equal(2, count);
    }

    [Fact]
    public void No_criteria_is_recognised_as_empty()
    {
        Assert.True(new ElementSelector().IsEmpty);
        Assert.True(new ElementSelector(Name: "   ").IsEmpty);
        Assert.False(new ElementSelector(Name: "Garden").IsEmpty);
        // An index alone selects nothing — it only disambiguates real criteria.
        Assert.True(new ElementSelector(Index: 2).IsEmpty);
    }

    [Fact]
    public void Unmatched_selector_reports_zero()
    {
        var (node, count) = new ElementSelector(Name: "not here").Pick(Rows);
        Assert.Null(node);
        Assert.Equal(0, count);
    }
}

// The spellings an agent may reasonably reach for when asking for a checkbox state.
public class ToggleStateTests
{
    [Theory]
    [InlineData("on")]
    [InlineData("On")]
    [InlineData("true")]
    [InlineData("checked")]
    public void On_spellings(string s)
    {
        Assert.True(PatternActions.TryParseToggle(s, out var state));
        Assert.Equal(ToggleState.On, state);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("false")]
    [InlineData("unchecked")]
    public void Off_spellings(string s)
    {
        Assert.True(PatternActions.TryParseToggle(s, out var state));
        Assert.Equal(ToggleState.Off, state);
    }

    // No state means "advance one step", which is what TogglePattern.Toggle does on its own.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("toggle")]
    public void Omitted_state_means_advance_one_step(string? s)
    {
        Assert.True(PatternActions.TryParseToggle(s, out var state));
        Assert.Null(state);
    }

    [Fact]
    public void Unknown_state_is_rejected_rather_than_guessed()
    {
        Assert.False(PatternActions.TryParseToggle("yes-please", out _));
    }
}

public class ScreenCaptureTests
{
    // Window titles routinely carry colons, backslashes and quotes ("C:\Projekte\Web - Commit"),
    // and the default capture path is built from one.
    [Fact]
    public void Sanitize_strips_characters_a_path_cannot_hold()
    {
        var cleaned = ScreenCapture.Sanitize("C:\\Projekte\\Web - Commit?");
        Assert.DoesNotContain(':', cleaned);
        Assert.DoesNotContain('\\', cleaned);
        Assert.DoesNotContain('?', cleaned);
    }

    [Fact]
    public void Sanitize_never_returns_an_empty_name()
    {
        Assert.Equal("window", ScreenCapture.Sanitize(string.Empty));
    }

    [Fact]
    public void Default_path_is_a_png_under_the_temp_folder()
    {
        var window = new WindowInfo(1, 2, "KY.ProjectHub", "Project Hub", false, null, null, 0, 0, 100, 100);
        var path = ScreenCapture.DefaultPath(window);
        Assert.EndsWith(".png", path);
        Assert.Contains("ky-ai-wpf", path);
        // Nothing lands in the user's repository just because they asked for a screenshot.
        Assert.StartsWith(Path.GetTempPath(), path);
    }
}
