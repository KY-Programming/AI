using System.Drawing;
using System.Drawing.Imaging;

namespace KY.AI.Wpf;

// PrintWindow capture: asks the window to render itself into an off-screen DC.
//
// Not a screen grab. A screen grab shows whatever is physically in front of the window, so it only
// works if you first bring the window forward — and stealing the foreground is the one thing this
// tool refuses to do. PrintWindow reads the window's own content, so it captures a window sitting
// behind three others, on another virtual desktop, without anything moving on the user's screen.
internal static class ScreenCapture
{
    public sealed record Result(bool Ok, string? Path = null, int Width = 0, int Height = 0, string? Error = null, string? Hint = null);

    public static Result Capture(WindowInfo window, string path)
    {
        if (window.Minimized)
            return new Result(false,
                Error: "the window is minimized, so it has nothing to render",
                Hint: "read it with tree/find/text instead — those work while it is minimized. " +
                      "Restoring the window would take the user's screen, which this tool will not do.");

        var hWnd = (IntPtr)window.Handle;
        if (!Native.GetWindowRect(hWnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            return new Result(false, Error: "the window reports no drawable area");

        var windowDc = Native.GetWindowDC(hWnd);
        if (windowDc == IntPtr.Zero)
            return new Result(false, Error: "could not obtain the window's device context");

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memoryDc = Native.CreateCompatibleDC(windowDc);
            bitmap = Native.CreateCompatibleBitmap(windowDc, rect.Width, rect.Height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
                return new Result(false, Error: "could not allocate the capture bitmap");

            previous = Native.SelectObject(memoryDc, bitmap);
            // PW_RENDERFULLCONTENT. Without it a WPF window — which composes through
            // DirectComposition rather than painting into its own DC — comes back blank, so the flag
            // is what makes this work on the tool's first-class target at all.
            if (!Native.PrintWindow(hWnd, memoryDc, Native.PwRenderFullContent))
                return new Result(false,
                    Error: "the window declined to render itself",
                    Hint: "some hardware-accelerated or protected windows refuse PrintWindow; " +
                          "read the window with tree/find/text instead");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // FromHbitmap copies the DIB into a managed Bitmap, so the GDI handles below can be
            // released in the finally regardless of when the file is written.
            using var image = Image.FromHbitmap(bitmap);
            image.Save(path, ImageFormat.Png);

            return new Result(true, path, rect.Width, rect.Height);
        }
        finally
        {
            if (previous != IntPtr.Zero) Native.SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) Native.DeleteDC(memoryDc);
            Native.ReleaseDC(hWnd, windowDc);
        }
    }

    // Where a capture goes when the agent does not say. Under the OS temp folder, one folder for the
    // tool, timestamped per shot — the agent gets a path it can read straight back and nothing lands
    // in the user's repository.
    public static string DefaultPath(WindowInfo window)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        return Path.Combine(Path.GetTempPath(), "ky-ai-wpf", $"{Sanitize(window.Process)}-{stamp}.png");
    }

    // A window title is a filename's worst case: it carries colons, slashes and quotes routinely.
    internal static string Sanitize(string name)
    {
        var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "window" : cleaned;
    }
}
