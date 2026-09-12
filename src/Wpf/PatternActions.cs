using System.Runtime.InteropServices;

namespace KY.AI.Wpf;

// The five UI Automation patterns this tool drives, plus the explicit scroll-into-view.
//
// Every one of them reaches the control through its own provider — the app runs the same code path
// it runs for a screen reader. None of them needs the window focused, activated or even visible,
// which is the entire reason the tool exists: a test can drive the app while the user keeps typing
// in another window, and their caret, clipboard and mouse are never touched.
//
// That last part is only true because the client has AutoSetFocus off (Uia.Automation). With UIA's
// default, every call below would focus its element first and raise the window with it.
//
// When a control does not support the pattern being asked for, that is reported as a refusal with
// the patterns it *does* support — never worked around with synthetic input. A behaviour that is
// genuinely keyboard-only stays the user's to perform.
internal static class PatternActions
{
    public sealed record Outcome(bool Ok, string? Error = null, string? Hint = null, object? Detail = null);

    public static Outcome Invoke(IUIAutomationElement element, NodeInfo node)
    {
        // A ListBoxItem / TreeViewItem usually offers SelectionItem rather than Invoke, and selecting
        // it is what "click this row" means for it. Falling back keeps the agent from having to know
        // which of the two a given WPF item template happened to produce.
        if (Pattern<IUIAutomationInvokePattern>(element, UiaIds.InvokePattern) is { } p)
            return Guard(p.Invoke);
        if (Pattern<IUIAutomationSelectionItemPattern>(element, UiaIds.SelectionItemPattern) is { } s)
            return Guard(s.Select, new { via = "SelectionItem" });
        // Names both patterns it tried, not just the first: "does not support Invoke" on its own
        // invites the agent to reach for `select` next, which has already been tried here.
        return NoPattern(node, "Invoke or SelectionItem", "invoke");
    }

    public static Outcome SetValue(IUIAutomationElement element, NodeInfo node, string value)
    {
        if (Pattern<IUIAutomationValuePattern>(element, UiaIds.ValuePattern) is not { } pattern)
            return NoPattern(node, "Value", "set_value");
        if (pattern.get_CurrentIsReadOnly())
            return new Outcome(false, $"'{node.Name ?? node.AutomationId ?? node.Type}' is read-only",
                "the control refuses programmatic writes; it may need to be enabled first");
        // Read back rather than echoing the argument: a control with a mask, a converter or a
        // validating binding can accept the call and store something else, and the agent should see
        // what actually landed.
        return Guard(() => pattern.SetValue(value), () => new { value = ReadBack(pattern) });
    }

    // state: "on" | "off" | null (advance one step, which is what TogglePattern.Toggle does).
    // WPF's three-state checkbox cycles On → Off → Indeterminate, so an explicit target is toggled
    // toward until it is reached rather than assumed to be one step away.
    public static Outcome Toggle(IUIAutomationElement element, NodeInfo node, string? state)
    {
        if (Pattern<IUIAutomationTogglePattern>(element, UiaIds.TogglePattern) is not { } pattern)
            return NoPattern(node, "Toggle", "toggle");

        if (!TryParseToggle(state, out var wanted))
            return new Outcome(false, $"unknown toggle state '{state}'",
                "use on, off or indeterminate — or omit state to advance one step");

        return Guard(() =>
        {
            if (wanted is null) { pattern.Toggle(); return; }
            // Bounded: three states means at most three steps, and the guard stops a provider that
            // never reports the state we asked for from spinning here forever.
            for (var i = 0; i < 3 && pattern.get_CurrentToggleState() != wanted; i++)
                pattern.Toggle();
        }, () => new { state = pattern.get_CurrentToggleState().ToString() });
    }

    // Pure, so the spellings the agent may reasonably try are pinned by a test rather than by
    // whichever ones happened to be exercised by hand.
    internal static bool TryParseToggle(string? state, out ToggleState? wanted)
    {
        switch (state?.Trim().ToLowerInvariant())
        {
            case null or "" or "toggle": wanted = null; return true;
            case "on" or "true" or "checked": wanted = ToggleState.On; return true;
            case "off" or "false" or "unchecked": wanted = ToggleState.Off; return true;
            case "indeterminate" or "mixed": wanted = ToggleState.Indeterminate; return true;
            default: wanted = null; return false;
        }
    }

    public static Outcome Expand(IUIAutomationElement element, NodeInfo node, bool expand)
    {
        if (Pattern<IUIAutomationExpandCollapsePattern>(element, UiaIds.ExpandCollapsePattern) is not { } pattern)
            return NoPattern(node, "ExpandCollapse", "expand");
        return Guard(() =>
        {
            if (expand) pattern.Expand();
            else pattern.Collapse();
        }, () => new { state = pattern.get_CurrentExpandCollapseState().ToString() });
    }

    public static Outcome Select(IUIAutomationElement element, NodeInfo node, bool addToSelection)
    {
        if (Pattern<IUIAutomationSelectionItemPattern>(element, UiaIds.SelectionItemPattern) is not { } pattern)
            return NoPattern(node, "SelectionItem", "select");
        return Guard(() =>
        {
            if (addToSelection) pattern.AddToSelection();
            else pattern.Select();
        }, () => new { selected = pattern.get_CurrentIsSelected() });
    }

    // The one operation that deliberately moves something the user can see, which is why it is its
    // own tool rather than a side effect of the others: reads and actions leave the window's scroll
    // position exactly where the user left it unless they explicitly ask for this.
    public static Outcome ScrollIntoView(IUIAutomationElement element, NodeInfo node)
    {
        if (Pattern<IUIAutomationScrollItemPattern>(element, UiaIds.ScrollItemPattern) is not { } pattern)
            return NoPattern(node, "ScrollItem", "scroll_into_view");
        return Guard(pattern.ScrollIntoView);
    }

    private static T? Pattern<T>(IUIAutomationElement element, int patternId) where T : class =>
        AutomationReader.Pattern<T>(element, patternId);

    private static Outcome Guard(Action act, object? detail = null) => Guard(act, () => detail);

    // The failure modes are all "the UI moved under us" or "the provider said no", and each has a
    // different fix for the agent, so they are reported apart instead of as one opaque error. The
    // COM client reports them as HRESULTs on one exception type, where the managed client had a type
    // per case — the codes are UIA's own (UiaIds).
    private static Outcome Guard(Action act, Func<object?> detail)
    {
        try
        {
            act();
            return new Outcome(true, Detail: detail());
        }
        catch (COMException ex) when (ex.HResult == UiaIds.ElementNotAvailable)
        {
            return new Outcome(false, "the element disappeared before the action completed",
                "the tree changed (a filter, a reload, a closed popup) — call find again and act on the fresh result");
        }
        catch (COMException ex) when (ex.HResult == UiaIds.ElementNotEnabled)
        {
            return new Outcome(false, "the element is disabled",
                "the app is refusing the action, not the tool — check the state the control depends on");
        }
        catch (COMException ex)
        {
            return new Outcome(false, $"the control refused the action: {ex.Message.TrimEnd()}", null);
        }
        // UIA_E_INVALIDOPERATION shares its HRESULT with the CLR's own, so it arrives as this rather
        // than as a COMException (see UiaIds). Without it here a plain "the control says no" escapes
        // to Engine's catch-all and is reported as an unreachable provider — which sends the reader
        // looking for an elevation problem that is not there.
        catch (InvalidOperationException ex)
        {
            return new Outcome(false, $"the control refused the action: {ex.Message.TrimEnd()}", null);
        }
        catch (ArgumentException ex)
        {
            return new Outcome(false, ex.Message, null);
        }
    }

    private static string? ReadBack(IUIAutomationValuePattern pattern)
    {
        try { return pattern.get_CurrentValue(); }
        catch (COMException) { return null; }
    }

    // The refusal that gives this tool its shape. It says what the element *can* do, so the agent's
    // next call is a working one — and it says out loud that faking a keystroke is not on the menu,
    // because that is precisely the fallback this tool was built to replace.
    private static Outcome NoPattern(NodeInfo node, string pattern, string tool)
    {
        var supported = node.Patterns is { Length: > 0 }
            ? string.Join(", ", node.Patterns)
            : "none";
        return new Outcome(false,
            $"'{Label(node)}' does not support {pattern}, so {tool} cannot act on it",
            $"supported here: {supported}. ky-ai-wpf never synthesises keyboard or mouse input, so if this " +
            "behaviour is genuinely keyboard-only, ask the user to perform it — or have the app expose the " +
            "pattern (a WPF control reachable by keyboard alone is usually a missing automation peer).");
    }

    private static string Label(NodeInfo node) =>
        node.Name ?? node.AutomationId ?? $"{node.Type} at {(node.Path.Length == 0 ? "<root>" : node.Path)}";
}
