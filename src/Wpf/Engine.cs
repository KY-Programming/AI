namespace KY.AI.Wpf;

// What each MCP tool actually does, once the arguments are in hand: resolve the window, walk the
// part of the tree that was asked for, and either report it or drive one pattern on one node.
//
// Every entry point marshals onto the single Uia thread and comes back with a finished JSON string,
// so WpfTools stays a thin declaration of the tool surface.
//
// Unlike the rest of the suite there is no supervisor process here and nothing to register: the
// user starts their app themselves, and this tool attaches to a window that already exists. The hub
// therefore does the automation in-process, and `list` reads the desktop instead of a registry.
internal static class Engine
{
    // A tree walk is bounded twice over: by depth (how far down the agent asked to look) and by a
    // node ceiling (how much it is willing to pay for). A real WPF window is tens of thousands of
    // elements, so an unbounded default would be a very expensive way to learn that.
    private const int TreeScanCeiling = 4000;

    public static Task<string> ListWindows() => Guarded(() =>
    {
        var windows = WindowFinder.List();
        var foreground = Native.GetForegroundWindow().ToInt64();
        return Json.Write(new
        {
            windows = windows.Select(w => new
            {
                w.Process,
                w.Pid,
                w.Title,
                w.Handle,
                w.Minimized,
                foreground = w.Handle == foreground,
                // Present only on an owned window — a modal dialog, usually. Naming the owner is what
                // turns "there is a second Project Hub row" into "Project Hub has a dialog open".
                ownedBy = w.OwnerTitle is null && w.OwnerHandle is null
                    ? null
                    : new { title = w.OwnerTitle, handle = w.OwnerHandle },
                rect = new[] { w.X, w.Y, w.Width, w.Height },
            }),
            count = windows.Count,
        });
    });

    public static Task<string> Tree(string? window, string? path, int depth, bool includeOffscreen, int limit) =>
        WithWindow(window, (target, root) =>
        {
            var subtree = AutomationReader.Descend(root, path);
            if (subtree is null) return PathGone(path);

            var walk = AutomationReader.Walk(subtree, Math.Max(0, depth), includeOffscreen, Math.Max(1, limit), path);
            return Json.Write(new
            {
                window = Describe(target),
                root = path ?? string.Empty,
                depth,
                count = walk.Nodes.Count,
                truncated = walk.Truncated,
                // The one thing an agent cannot infer from a truncated tree is where to look next, so
                // say it rather than leaving it to guess at another blind full walk.
                hint = walk.Truncated
                    ? "cut off at the node limit — narrow it with a subtree path, a smaller depth, or use find"
                    : BareRootHint(walk, depth, path),
                nodes = walk.Nodes.Select(n => n.Info),
            });
        });

    public static Task<string> Find(string? window, ElementSelector selector, int depth, bool includeOffscreen, int limit) =>
        WithWindow(window, (target, root) =>
        {
            if (selector.IsEmpty)
                return Json.Error("no search criteria given",
                    "pass at least one of automationId, name, controlType, contains or path");

            var walk = AutomationReader.Walk(root, Math.Max(0, depth), includeOffscreen, TreeScanCeiling);
            var matches = walk.Nodes.Where(n => selector.Matches(n.Info)).Select(n => n.Info).ToList();
            return Json.Write(new
            {
                window = Describe(target),
                count = matches.Count,
                scanned = walk.Nodes.Count,
                // A scan that hit the ceiling may have missed matches further down; saying so is the
                // difference between "there are none" and "I stopped looking".
                truncated = walk.Truncated,
                hint = BareRootHint(walk, depth),
                matches = matches.Take(Math.Max(1, limit)),
            });
        });

    public static Task<string> Text(string? window, string? path, int depth, bool includeOffscreen, int limit) =>
        WithWindow(window, (target, root) =>
        {
            var subtree = AutomationReader.Descend(root, path);
            if (subtree is null) return PathGone(path);

            var walk = AutomationReader.Walk(subtree, Math.Max(0, depth), includeOffscreen, TreeScanCeiling, path);
            var text = walk.Nodes
                .Select(n => n.Info)
                .Where(IsTextBearing)
                .Select(n => new { n.Path, n.Type, text = n.Value ?? n.Name })
                .Where(t => !string.IsNullOrWhiteSpace(t.text))
                .Take(Math.Max(1, limit))
                .ToList();

            return Json.Write(new
            {
                window = Describe(target),
                root = path ?? string.Empty,
                count = text.Count,
                truncated = walk.Truncated,
                hint = BareRootHint(walk, depth, path),
                text,
            });
        });

    public static Task<string> Act(string? window, ElementSelector selector, int depth, bool includeOffscreen,
        Func<IUIAutomationElement, NodeInfo, PatternActions.Outcome> action, string name) =>
        WithWindow(window, (target, root) =>
        {
            if (selector.IsEmpty)
                return Json.Error("no element selected",
                    "pass path (from tree/find) or at least one of automationId, name, controlType, contains");

            var walk = AutomationReader.Walk(root, Math.Max(0, depth), includeOffscreen, TreeScanCeiling);
            var (node, count) = selector.Pick(walk.Nodes.Select(n => n.Info).ToList());
            if (node is null)
                return count == 0
                    ? Json.Error("no element matches the selector",
                        "call find with the same criteria to see what is there; a virtualized row that is " +
                        "scrolled out is skipped unless you pass includeOffscreen")
                    : Json.Error($"index {selector.Index} is out of range — {count} element(s) matched",
                        "index is zero-based over the matches in tree order");
            if (count > 1 && string.IsNullOrWhiteSpace(selector.Path) && selector.Index is null)
            {
                // Acting on the first of several silently is how an agent presses the wrong row and
                // then reports a bug in the app. Make it say which one it means.
                return Json.Error($"the selector matches {count} elements",
                    "narrow it (add name or automationId), or pass index to pick one — find lists them in the same order");
            }

            var element = walk.Nodes.First(n => ReferenceEquals(n.Info, node)).Element;
            var before = Native.GetForegroundWindow();
            var outcome = action(element, node);
            var after = Native.GetForegroundWindow();

            return Json.Write(new
            {
                ok = outcome.Ok,
                action = name,
                error = outcome.Error,
                hint = outcome.Hint,
                target = node,
                result = outcome.Detail,
                // Reported on every action so the agent can prove the window it was told not to take
                // is still the one in front. Two things could change it and neither is left to
                // chance: the tool imports no SetForegroundWindow (see Native), and UIA's own
                // auto-focus — which raised the target window on every action until it was found —
                // is switched off on the client (see Uia.Automation). So `changed:true` really is
                // the app activating ITSELF, which is worth saying out loud rather than leaving to
                // look like the tool did it.
                foreground = new
                {
                    changed = before != after,
                    before = ForegroundLabel(before),
                    after = ForegroundLabel(after),
                    note = before != after
                        ? "the app raised its own window in reaction to this change — ky-ai-wpf neither " +
                          "activates a window nor lets UI Automation focus the element it acts on. If that " +
                          "is unwanted, it is the app's focus handling to fix."
                        : null,
                },
            });
        });

    public static Task<string> Screenshot(string? window, string? path) => Guarded(() =>
    {
        var resolved = Resolve(window);
        if (resolved.Window is null) return NoWindow(resolved);

        var target = resolved.Window;
        var file = string.IsNullOrWhiteSpace(path)
            ? ScreenCapture.DefaultPath(target)
            : Path.GetFullPath(path!);

        var before = Native.GetForegroundWindow();
        var shot = ScreenCapture.Capture(target, file);
        var after = Native.GetForegroundWindow();

        return Json.Write(new
        {
            ok = shot.Ok,
            error = shot.Error,
            hint = shot.Hint,
            window = Describe(target),
            path = shot.Path,
            width = shot.Width,
            height = shot.Height,
            foreground = new { changed = before != after, before = ForegroundLabel(before), after = ForegroundLabel(after) },
        });
    });

    // ── shared plumbing ──

    private static Task<string> WithWindow(string? window,
        Func<WindowInfo, IUIAutomationElement, string> body) => Guarded(() =>
    {
        var resolved = Resolve(window);
        if (resolved.Window is null) return NoWindow(resolved);

        var root = AutomationReader.FromWindow(resolved.Window);
        if (root is null)
            return Json.Error("the window closed before it could be read", "call list again");

        return body(resolved.Window, root);
    });

    // Every tool goes through here, because an exception that escapes reaches the agent as an opaque
    // "an error occurred invoking 'text'" — no window, no cause, nothing to act on. A tool that talks
    // to arbitrary other processes has too many ways to fail for a hand-picked catch list to stay
    // complete, so anything unforeseen still comes back as the same JSON error shape, naming the
    // exception so a report is actionable.
    private static Task<string> Guarded(Func<string> body) => Uia.RunAsync(() =>
    {
        try
        {
            return body();
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == UiaIds.ElementNotAvailable)
        {
            return Json.Error("the window closed while it was being read", "call list again");
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return Json.Error(
                "UI Automation refused access to that window",
                "a client cannot read a window running at a higher integrity level; ky-ai-wpf would " +
                "have to run elevated too. If it is your own app's window, that is a bug worth reporting.",
                new { exception = ex.GetType().Name });
        }
        catch (Exception ex)
        {
            return Json.Error($"the automation call failed: {ex.Message}",
                "call list to confirm the window is still there; if it is, this is a ky-ai-wpf bug worth reporting",
                new { exception = ex.GetType().FullName });
        }
    });

    // Deliberately narrow. `InvalidOperationException` must NOT be here: UIA_E_INVALIDOPERATION is
    // 0x80131509, the CLR's own COR_E_INVALIDOPERATION, so interop raises it for an ordinary "not
    // right now" from a control — mapping that to an access error sends the reader after an elevation
    // problem that does not exist. ElementNotAvailable is not here either; it means the element went
    // away and is answered above.
    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException ||
        (ex is System.Runtime.InteropServices.COMException com && com.HResult == UiaIds.AccessDenied);

    // A window that UI Automation will not let us read does not fail — it answers, with a single root
    // element that reports no children. `tree` then says "count: 1", `text` says "count: 0", and an
    // agent reasonably concludes the app has no UI, which is the most misleading thing this tool could
    // tell it. Task Manager is the everyday case. So a bare root is called out wherever it appears
    // rather than left to look like an empty app.
    //
    // Only when the walk was actually allowed to descend: depth 0 means the caller asked for the root
    // alone, and getting exactly that is not a surprise.
    // scopePath non-empty means the caller narrowed to a subtree, and an empty subtree there is an
    // ordinary answer about one panel — not the window-wide symptom this describes.
    internal static string? BareRootHint(AutomationReader.WalkResult walk, int depth, string? scopePath = null) =>
        depth > 0 && string.IsNullOrWhiteSpace(scopePath) && walk.Nodes is [{ Info.Depth: 0, Info.Children: 0 }]
            ? "this window exposed no elements at all — which is exactly how a window at a higher " +
              "integrity level (Task Manager, regedit, anything started as administrator) reads from " +
              "a normal client. If it is your own app, it has no automation peers yet."
            : null;

    private static WindowMatcher.Result Resolve(string? window) =>
        WindowMatcher.Resolve(WindowFinder.List(), window);

    private static string NoWindow(WindowMatcher.Result resolved) => Json.Error(
        resolved.Error ?? "no window matched",
        "call list to see the windows this tool can see, then target one by process name, title, or #<handle>",
        new { candidates = resolved.Candidates.Select(w => new { w.Process, w.Pid, w.Title, w.Handle }) });

    private static string PathGone(string? path) => Json.Error(
        $"no element at path '{path}'",
        "paths are positions in the tree, so they move when it changes shape — call tree or find again " +
        "and act on the fresh result");

    private static object Describe(WindowInfo w) => new { w.Process, w.Pid, w.Title, w.Handle };

    // Text worth asserting on: the label and read-out control types, plus anything carrying a
    // ValuePattern string. Deliberately not every named node — a window full of buttons would drown
    // the actual on-screen text the agent came to read.
    private static bool IsTextBearing(NodeInfo n) =>
        n.Type is "Text" or "Edit" or "Document" or "Hyperlink" or "HeaderItem" || n.Value is not null;

    private static string ForegroundLabel(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "<none>";
        Native.GetWindowThreadProcessId(hWnd, out var pid);
        var title = Native.WindowTitle(hWnd);
        return title.Length == 0 ? $"pid {pid}" : $"{title} (pid {pid})";
    }
}
