using System.Runtime.InteropServices;

namespace KY.AI.Wpf;

// Walks a window's automation tree into flat NodeInfo records, keeping each node's live
// IUIAutomationElement beside it so an action tool can act on exactly the node the agent selected.
//
// Everything here is a read through the UIA client: it asks the provider for properties and never
// touches the window, its focus, its scroll position or the mouse. Run it on the Uia thread — that
// is where the client with AutoSetFocus off lives (see Uia.Automation).
internal static class AutomationReader
{
    // A walked node: what the agent sees (Info) and what we act on (Element). The element is only
    // valid while the tree keeps its shape, which is why paths are documented as read-then-act.
    internal sealed record Walked(NodeInfo Info, IUIAutomationElement Element);

    // The patterns this tool can actually drive. Reported per node so the agent knows which action
    // will work before it calls one, instead of discovering it from an error.
    private static readonly (int Id, string Name)[] Actionable =
    [
        (UiaIds.InvokePattern, "Invoke"),
        (UiaIds.ValuePattern, "Value"),
        (UiaIds.TogglePattern, "Toggle"),
        (UiaIds.ExpandCollapsePattern, "ExpandCollapse"),
        (UiaIds.SelectionItemPattern, "SelectionItem"),
        (UiaIds.ScrollItemPattern, "ScrollItem"),
    ];

    public static IUIAutomationElement? FromWindow(WindowInfo window)
    {
        try { return Uia.Automation.ElementFromHandle((IntPtr)window.Handle); }
        catch (COMException) { return null; }
        catch (ArgumentException) { return null; }
    }

    // Navigate to the subtree root named by a dot-separated child-index path (null/empty = the window
    // itself). Returns null when the path no longer resolves — which is the honest answer after the
    // tree changed shape, and better than silently acting on whatever moved into that slot.
    public static IUIAutomationElement? Descend(IUIAutomationElement root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return root;
        var current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var index) || index < 0) return null;
            var child = FirstChild(current);
            for (var i = 0; i < index && child is not null; i++)
                child = NextSibling(child);
            if (child is null) return null;
            current = child;
        }
        return current;
    }

    public sealed record WalkResult(List<Walked> Nodes, bool Truncated);

    // Depth-first walk in tree order, so `index` on a selector means "the nth one reading top to
    // bottom" — the order the user sees.
    //
    // maxDepth counts from the subtree root (0 = the root alone). includeOffscreen:false prunes an
    // offscreen node AND its subtree: that is what keeps a virtualized WPF list cheap, since the rows
    // scrolled out of view are exactly the ones the agent cannot act on anyway.
    // basePath is where `root` sits in the WINDOW's tree, and every path handed out is built from it.
    // That is what makes a path mean the same thing no matter which call produced it: a scoped `tree`
    // or `text` returns paths an action can use as-is, instead of ones that silently address a
    // different element unless the caller remembers to re-prefix them. An agent reported exactly that
    // — closing the wrong control after mixing a `text` path with a `find` path — and the fix belongs
    // here rather than in a warning nobody reads at the moment they need it.
    public static WalkResult Walk(IUIAutomationElement root, int maxDepth, bool includeOffscreen, int limit,
        string? basePath = null)
    {
        var nodes = new List<Walked>();
        var truncated = false;

        void Visit(IUIAutomationElement element, string path, int depth)
        {
            if (truncated) return;
            if (nodes.Count >= limit) { truncated = true; return; }

            var info = Describe(element, path, depth);
            if (info is null) return;                       // vanished mid-walk — skip it, keep going
            // The root always goes in: a minimized window reports itself offscreen, and answering
            // "your window has no elements" there would be misleading.
            if (!includeOffscreen && info.Offscreen && depth > 0) return;
            nodes.Add(new Walked(info, element));

            if (depth >= maxDepth) return;
            var child = FirstChild(element);
            var i = 0;
            while (child is not null)
            {
                Visit(child, path.Length == 0 ? i.ToString() : path + "." + i, depth + 1);
                if (truncated) return;
                child = NextSibling(child);
                i++;
            }
        }

        Visit(root, basePath?.Trim() ?? string.Empty, 0);
        return new WalkResult(nodes, truncated);
    }

    // Read one element's properties. Every access is its own cross-process call and can fail if the
    // element disappears between two of them (a row scrolled away, a popup closed), so a lost node is
    // dropped rather than failing the walk.
    private static NodeInfo? Describe(IUIAutomationElement element, string path, int depth)
    {
        try
        {
            var rect = element.get_CurrentBoundingRectangle();

            var patterns = Actionable
                .Where(p => Pattern<object>(element, p.Id) is not null)
                .Select(p => p.Name)
                .ToArray();

            return new NodeInfo(
                Path: path,
                Depth: depth,
                Type: UiaIds.ControlTypeName(element.get_CurrentControlType()),
                AutomationId: Blank(element.get_CurrentAutomationId()),
                Name: Blank(element.get_CurrentName()),
                Value: ReadValue(element),
                Enabled: element.get_CurrentIsEnabled(),
                Offscreen: element.get_CurrentIsOffscreen(),
                Rect: rect.IsEmpty ? null : [rect.Left, rect.Top, rect.Width, rect.Height],
                Patterns: patterns.Length > 0 ? patterns : null,
                Children: CountChildren(element));
        }
        catch (COMException ex) when (ex.HResult == UiaIds.ElementNotAvailable) { return null; }
    }

    // The one field an assertion usually wants. A text box carries it in ValuePattern; a checkbox and
    // an expander carry their state in their own pattern instead, and reporting those here means
    // `tree` alone answers "is it checked / is it open" without a second call per node.
    private static string? ReadValue(IUIAutomationElement element)
    {
        try
        {
            if (Pattern<IUIAutomationValuePattern>(element, UiaIds.ValuePattern) is { } v)
                return Blank(v.get_CurrentValue());
            if (Pattern<IUIAutomationTogglePattern>(element, UiaIds.TogglePattern) is { } t)
                return t.get_CurrentToggleState().ToString();
            if (Pattern<IUIAutomationExpandCollapsePattern>(element, UiaIds.ExpandCollapsePattern) is { } e)
                return e.get_CurrentExpandCollapseState().ToString();
            if (Pattern<IUIAutomationSelectionItemPattern>(element, UiaIds.SelectionItemPattern) is { } s)
                return s.get_CurrentIsSelected() ? "Selected" : null;
        }
        catch (COMException) { /* gone — no value to report */ }
        catch (InvalidOperationException) { /* the provider refused the pattern read (see UiaIds) */ }
        return null;
    }

    // GetCurrentPattern answers null for a pattern the element does not support, which is the COM
    // spelling of TryGetCurrentPattern. A provider that hands back something of the wrong shape is
    // treated as "not supported" rather than taking the walk down with it.
    internal static T? Pattern<T>(IUIAutomationElement element, int patternId) where T : class
    {
        try { return element.GetCurrentPattern(patternId) as T; }
        catch (InvalidCastException) { return null; }
    }

    private static int CountChildren(IUIAutomationElement element)
    {
        var count = 0;
        var child = FirstChild(element);
        while (child is not null && count < 10_000)
        {
            count++;
            child = NextSibling(child);
        }
        return count;
    }

    private static IUIAutomationElement? FirstChild(IUIAutomationElement element)
    {
        try { return Uia.ControlView.GetFirstChildElement(element); }
        catch (COMException ex) when (ex.HResult == UiaIds.ElementNotAvailable) { return null; }
    }

    private static IUIAutomationElement? NextSibling(IUIAutomationElement element)
    {
        try { return Uia.ControlView.GetNextSiblingElement(element); }
        catch (COMException ex) when (ex.HResult == UiaIds.ElementNotAvailable) { return null; }
    }

    private static string? Blank(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
