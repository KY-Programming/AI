using System.Diagnostics;

namespace KY.AI.Wpf;

// Enumerates the top-level application windows on the desktop. This is the tool's only discovery
// step — there is no registry to consult, because the user starts their app themselves and this
// tool attaches to whatever is already running. It never starts, stops or changes a window.
internal static class WindowFinder
{
    // Visible, titled, not composited away, and not a tool window. Without that filter `list` is a
    // hundred rows of invisible shell and IME helper windows, and the agent pays to read all of them.
    //
    // Owned windows ARE listed, with their owner — they are the modal dialogs, and a dialog is the
    // single most likely thing an agent is looking for. A WPF dialog with ShowInTaskbar="False" is
    // owned, so the taskbar's own rule (which excludes owned windows) hid exactly the case that
    // matters, and an agent looking for a password prompt found nothing and concluded it was not
    // open. It is reachable through its owner's tree either way; listing it just means the agent can
    // see that it exists and target it directly.
    public static List<WindowInfo> List()
    {
        var found = new List<WindowInfo>();
        var names = new Dictionary<uint, string>();

        Native.EnumWindows((hWnd, _) =>
        {
            if (!Native.IsWindowVisible(hWnd)) return true;
            if (Native.IsCloaked(hWnd)) return true;

            var title = Native.WindowTitle(hWnd);
            if (title.Length == 0) return true;

            var exStyle = (long)Native.GetWindowLongPtr(hWnd, Native.GwlExStyle);
            var toolWindow = (exStyle & Native.WsExToolWindow) != 0;
            var appWindow = (exStyle & Native.WsExAppWindow) != 0;
            // WS_EX_APPWINDOW is the explicit "show me in the taskbar anyway" opt-in, so it beats the
            // tool-window exclusion.
            if (!appWindow && toolWindow) return true;
            var owner = Native.GetWindow(hWnd, Native.GwOwner);

            Native.GetWindowThreadProcessId(hWnd, out var pid);
            if (!names.TryGetValue(pid, out var process))
            {
                process = ProcessName(pid);
                names[pid] = process;
            }

            Native.GetWindowRect(hWnd, out var rect);
            found.Add(new WindowInfo(
                Handle: hWnd.ToInt64(),
                Pid: (int)pid,
                Process: process,
                Title: title,
                Minimized: Native.IsIconic(hWnd),
                OwnerHandle: owner == IntPtr.Zero ? null : owner.ToInt64(),
                OwnerTitle: owner == IntPtr.Zero ? null : Blank(Native.WindowTitle(owner)),
                X: rect.Left,
                Y: rect.Top,
                Width: rect.Width,
                Height: rect.Height));
            return true;
        }, IntPtr.Zero);

        return found.OrderBy(w => w.Process, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    private static string? Blank(string s) => s.Length == 0 ? null : s;

    // A process can exit (or be a protected system process) between the enumeration and this read;
    // an unnamed window is still worth listing by pid and title.
    private static string ProcessName(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch (ArgumentException) { return "?"; }
        catch (InvalidOperationException) { return "?"; }
    }
}
