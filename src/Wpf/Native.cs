using System.Runtime.InteropServices;
using System.Text;

namespace KY.AI.Wpf;

// The handful of Win32 calls this tool needs. Every one of them is a READ of the window manager
// (enumerate, measure, ask who has focus) or a PrintWindow capture — deliberately none of the
// input-synthesis or focus-stealing family: no SendInput/keybd_event/mouse_event, no SetCursorPos,
// no SetForegroundWindow/SetActiveWindow/BringWindowToTop. That absence is the tool's contract, so
// keep this file free of them; a behaviour that genuinely needs a keystroke is reported to the
// agent as such instead of being faked (see PatternActions.NoPattern).
internal static class Native
{
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // Used only to REPORT which window has focus — before and after every action — so the agent can
    // prove it never took it. Never paired with a call that changes it.
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    // Renders the window into a DC. Unlike a screen grab this reads the window's own content, so it
    // works while the window sits BEHIND others — which is the only kind of screenshot this tool is
    // willing to take (bringing a window forward would be exactly the theft it exists to avoid).
    [DllImport("user32.dll")]
    internal static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    internal const int GwlExStyle = -20;
    internal const long WsExToolWindow = 0x00000080;
    internal const long WsExAppWindow = 0x00040000;
    internal const uint GwOwner = 4;
    internal const int DwmwaCloaked = 14;

    // PW_RENDERFULLCONTENT. Without it PrintWindow returns a blank bitmap for a WPF (DirectComposition)
    // window — the flag is what makes this work at all on the tool's first-class target.
    internal const uint PwRenderFullContent = 0x00000002;

    internal static string WindowTitle(IntPtr hWnd)
    {
        var len = GetWindowTextLength(hWnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // A UWP/store window that is "visible" but composited away still shows up in EnumWindows; the
    // cloak attribute is the only way to tell it from a real one.
    internal static bool IsCloaked(IntPtr hWnd)
        => DwmGetWindowAttribute(hWnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
}
