using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KY.AI.Wpf;

// MCP tools exposed by the ky-ai-wpf hub. Each (except list) takes a `window` name from `list`, in
// the same by-name style as every other KY.AI tool.
//
// Unlike ng/net/terminal/browser these do not forward to a supervisor: there is nothing to
// supervise, because the user starts their WPF app themselves and this attaches to a window that is
// already there. The hub drives UI Automation in-process (Engine → Uia).
//
// The whole surface is focus-free. Reads ask the app's automation provider for properties; actions
// drive the provider's patterns — the same path a screen reader takes. Nothing here synthesises a
// keystroke or a mouse event, activates a window, or moves the cursor, so an agent can exercise the
// app while the user goes on typing somewhere else. Lives in the exe assembly so the hub exposes
// only these tools. Allow-list as mcp__ky-ai-wpf__<name>.
[McpServerToolType]
internal static class WpfTools
{
    private const string WindowArg =
        "Window to target: process name, window title, a substring of either, or #<handle> (see list). " +
        "Omit only when exactly one window is visible.";

    private const string SelectorArg =
        "Selectors AND together. Use path from tree/find for an exact node, or automationId/name/" +
        "controlType/contains to describe one.";

    [McpServerTool(Name = "list"), Description(
        "List the application windows this tool can see: process name, pid, title, handle, whether " +
        "the window is minimized, whether it currently has the foreground, and its screen rectangle. " +
        "Call this first to learn the window names the other tools expect.")]
    public static Task<string> List() => Engine.ListWindows();

    [McpServerTool(Name = "tree"), Description(
        "The window's UI Automation tree as flat JSON, in top-to-bottom order: controlType, " +
        "automationId, name, value, enabled, offscreen, bounding rectangle, depth, child count, and " +
        "which of the drivable patterns each node supports (Invoke, Value, Toggle, ExpandCollapse, " +
        "SelectionItem, ScrollItem). Every node carries a `path` you can pass back to any action. " +
        "A whole WPF tree is very large, so this is bounded: start with the default depth and drill " +
        "in by passing the `path` of the subtree you care about, rather than raising depth blindly. " +
        "Does not focus, activate or scroll the window.")]
    public static Task<string> Tree(
        [Description(WindowArg)] string? window = null,
        [Description("Subtree to read, as a path from a previous tree/find call; omit for the whole window")] string? path = null,
        [Description("How many levels below the subtree root to read (default 4)")] int depth = 4,
        [Description("Include elements the app reports as offscreen — e.g. virtualized rows scrolled out of view (default false)")] bool includeOffscreen = false,
        [Description("Maximum nodes to return (default 300)")] int limit = 300)
        => Engine.Tree(window, path, depth, includeOffscreen, limit);

    [McpServerTool(Name = "find"), Description(
        "Find elements in a window by automationId, name, controlType, or a substring of any of " +
        "those (contains). Criteria AND together. Much cheaper than reading a deep tree when you " +
        "already know what you are looking for — a WPF list that gives each row an AutomationId per " +
        "row kind and an AutomationProperties.Name per row is addressed as " +
        "automationId + name. Returns the same node shape as tree, including each match's `path`.")]
    public static Task<string> Find(
        [Description(WindowArg)] string? window = null,
        [Description("Exact AutomationId (case-insensitive)")] string? automationId = null,
        [Description("Exact Name (case-insensitive)")] string? name = null,
        [Description("Exact control type, e.g. Button, Edit, ListItem, Text, TabItem")] string? controlType = null,
        [Description("Substring matched against name, automationId and value")] string? contains = null,
        [Description("How deep to search (default 30)")] int depth = 30,
        [Description("Include elements the app reports as offscreen (default false)")] bool includeOffscreen = false,
        [Description("Maximum matches to return (default 30)")] int limit = 30)
        => Engine.Find(window, new ElementSelector(null, automationId, name, controlType, contains), depth, includeOffscreen, limit);

    [McpServerTool(Name = "invoke"), Description(
        "Press a button / activate an element through InvokePattern — falling back to SelectionItem " +
        "for list and tree items, which is what activating those means. Works on an unfocused window " +
        "that is behind other windows: the app runs the same handler it runs for a real click, but " +
        "nothing is typed, no window is raised and the cursor does not move. The reply reports the " +
        "foreground window before and after so you can confirm it did not change. If the element " +
        "supports neither pattern this refuses and lists what it does support — it will not fake a " +
        "keystroke.")]
    public static Task<string> Invoke(
        [Description(WindowArg)] string? window = null,
        [Description("Exact node path from tree/find")] string? path = null,
        [Description(SelectorArg)] string? automationId = null,
        [Description("Exact Name (case-insensitive)")] string? name = null,
        [Description("Exact control type, e.g. Button, ListItem")] string? controlType = null,
        [Description("Substring matched against name, automationId and value")] string? contains = null,
        [Description("Which match to act on when the selector matches several, zero-based in tree order. Required to disambiguate.")] int? index = null,
        [Description("Include offscreen elements when resolving the selector (default false)")] bool includeOffscreen = false)
        => Engine.Act(window, Selector(path, automationId, name, controlType, contains, index), 30, includeOffscreen,
            PatternActions.Invoke, "invoke");

    [McpServerTool(Name = "set_value"), Description(
        "Set a text box / editable control's value through ValuePattern. Does not focus the control " +
        "and does not type — the value is handed to the app, which raises its normal change " +
        "notifications, so bindings and filters react exactly as they do for a user. The reply " +
        "reports the value read back afterwards (a mask, converter or validating binding can store " +
        "something else) and the unchanged foreground window.")]
    public static Task<string> SetValue(
        [Description(WindowArg)] string? window = null,
        [Description("The value to set")] string value = "",
        [Description("Exact node path from tree/find")] string? path = null,
        [Description(SelectorArg)] string? automationId = null,
        [Description("Exact Name (case-insensitive)")] string? name = null,
        [Description("Exact control type, e.g. Edit")] string? controlType = null,
        [Description("Substring matched against name, automationId and value")] string? contains = null,
        [Description("Which match to act on when the selector matches several, zero-based in tree order")] int? index = null,
        [Description("Include offscreen elements when resolving the selector (default false)")] bool includeOffscreen = false)
        => Engine.Act(window, Selector(path, automationId, name, controlType, contains, index), 30, includeOffscreen,
            (element, node) => PatternActions.SetValue(element, node, value), "set_value");

    [McpServerTool(Name = "toggle"), Description(
        "Flip a checkbox / toggle button through TogglePattern. Pass state 'on' or 'off' to reach a " +
        "specific state (a three-state checkbox is stepped toward it), or omit state to advance one " +
        "step. Returns the resulting state.")]
    public static Task<string> Toggle(
        [Description(WindowArg)] string? window = null,
        [Description("on | off | indeterminate; omit to advance one step")] string? state = null,
        [Description("Exact node path from tree/find")] string? path = null,
        [Description(SelectorArg)] string? automationId = null,
        [Description("Exact Name (case-insensitive)")] string? name = null,
        [Description("Exact control type, e.g. CheckBox")] string? controlType = null,
        [Description("Substring matched against name, automationId and value")] string? contains = null,
        [Description("Which match to act on when the selector matches several, zero-based in tree order")] int? index = null,
        [Description("Include offscreen elements when resolving the selector (default false)")] bool includeOffscreen = false)
        => Engine.Act(window, Selector(path, automationId, name, controlType, contains, index), 30, includeOffscreen,
            (element, node) => PatternActions.Toggle(element, node, state), "toggle");

    [McpServerTool(Name = "expand"), Description(
        "Expand or collapse a tree item, expander or combo box through ExpandCollapsePattern. " +
        "Returns the resulting state. Expanding a virtualized tree node is what makes its children " +
        "appear in the automation tree, so follow this with tree on the node's path.")]
    public static Task<string> Expand(
        [Description(WindowArg)] string? window = null,
        [Description("true to expand, false to collapse (default true)")] bool expand = true,
        [Description("Exact node path from tree/find")] string? path = null,
        [Description(SelectorArg)] string? automationId = null,
        [Description("Exact Name (case-insensitive)")] string? name = null,
        [Description("Exact control type, e.g. TreeItem, ComboBox")] string? controlType = null,
        [Description("Substring matched against name, automationId and value")] string? contains = null,
        [Description("Which match to act on when the selector matches several, zero-based in tree order")] int? index = null,
        [Description("Include offscreen elements when resolving the selector (default false)")] bool includeOffscreen = false)
        => Engine.Act(window, Selector(path, automationId, name, controlType, contains, index), 30, includeOffscreen,
            (element, node) => PatternActions.Expand(element, node, expand), "expand");

    [McpServerTool(Name = "select"), Description(
        "Select a list item, tab or tree item through SelectionItemPattern — without focusing the " +
        "window. Pass addToSelection to extend a multi-select rather than replace it.")]
    public static Task<string> Select(
        [Description(WindowArg)] string? window = null,
        [Description("Add to the current selection instead of replacing it (default false)")] bool addToSelection = false,
        [Description("Exact node path from tree/find")] string? path = null,
        [Description(SelectorArg)] string? automationId = null,
        [Description("Exact Name (case-insensitive)")] string? name = null,
        [Description("Exact control type, e.g. ListItem, TabItem")] string? controlType = null,
        [Description("Substring matched against name, automationId and value")] string? contains = null,
        [Description("Which match to act on when the selector matches several, zero-based in tree order")] int? index = null,
        [Description("Include offscreen elements when resolving the selector (default false)")] bool includeOffscreen = false)
        => Engine.Act(window, Selector(path, automationId, name, controlType, contains, index), 30, includeOffscreen,
            (element, node) => PatternActions.Select(element, node, addToSelection), "select");

    [McpServerTool(Name = "scroll_into_view"), Description(
        "Scroll an element into view through ScrollItemPattern. This is the ONLY tool that moves " +
        "anything the user can see, which is why it is separate: reads and actions never scroll on " +
        "their own. Use it to realize a virtualized row you could otherwise only reach with " +
        "includeOffscreen, or before a screenshot that has to show a particular row.")]
    public static Task<string> ScrollIntoView(
        [Description(WindowArg)] string? window = null,
        [Description("Exact node path from tree/find")] string? path = null,
        [Description(SelectorArg)] string? automationId = null,
        [Description("Exact Name (case-insensitive)")] string? name = null,
        [Description("Exact control type")] string? controlType = null,
        [Description("Substring matched against name, automationId and value")] string? contains = null,
        [Description("Which match to act on when the selector matches several, zero-based in tree order")] int? index = null,
        [Description("Include offscreen elements when resolving the selector (default true here — the point is to reach one)")] bool includeOffscreen = true)
        => Engine.Act(window, Selector(path, automationId, name, controlType, contains, index), 30, includeOffscreen,
            PatternActions.ScrollIntoView, "scroll_into_view");

    [McpServerTool(Name = "text"), Description(
        "All text on screen in a window or one of its subtrees: label/text/edit/document/link " +
        "elements plus anything carrying a value, each with its path. Use it to assert on what the " +
        "user would see — after a filter, a load, or an action — without paying for a full tree. " +
        "Pass path to scope it to one panel.")]
    public static Task<string> Text(
        [Description(WindowArg)] string? window = null,
        [Description("Subtree to read, as a path from a previous tree/find call; omit for the whole window")] string? path = null,
        [Description("How deep to read (default 30)")] int depth = 30,
        [Description("Include elements the app reports as offscreen (default false)")] bool includeOffscreen = false,
        [Description("Maximum text entries to return (default 200)")] int limit = 200)
        => Engine.Text(window, path, depth, includeOffscreen, limit);

    [McpServerTool(Name = "screenshot"), Description(
        "Capture a window to a PNG file and return its path, for you to read back. Uses PrintWindow, " +
        "so it captures the window's own content even while it sits behind other windows — nothing " +
        "is raised and the user's screen does not change. Fails (rather than restoring it) if the " +
        "window is minimized. Omit path to get a temp file.")]
    public static Task<string> Screenshot(
        [Description(WindowArg)] string? window = null,
        [Description("Where to write the PNG; omit for a temp file")] string? path = null)
        => Engine.Screenshot(window, path);

    private static ElementSelector Selector(string? path, string? automationId, string? name,
        string? controlType, string? contains, int? index)
        => new(path, automationId, name, controlType, contains, index);
}
