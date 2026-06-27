using KY.AI.Terminal.ConPty;

namespace KY.AI.Terminal;

// Puts the host console (the terminal ky-ai-terminal was launched in) into raw VT mode so
// keystrokes flow through to the pseudoconsole unprocessed and the shell's output renders with
// full fidelity. Restores the original modes on Dispose — and registers a process-exit hook so a
// crash doesn't leave the user's terminal stuck in raw mode.
internal sealed class HostConsole : IDisposable
{
    private readonly nint _inHandle;
    private readonly nint _outHandle;
    private readonly uint _origIn;
    private readonly uint _origOut;
    private readonly uint _origCp;
    private readonly bool _ok;
    private bool _restored;

    public HostConsole()
    {
        _inHandle = NativeMethods.GetStdHandle(NativeMethods.STD_INPUT_HANDLE);
        _outHandle = NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE);

        _ok = NativeMethods.GetConsoleMode(_inHandle, out _origIn)
              && NativeMethods.GetConsoleMode(_outHandle, out _origOut);
        if (!_ok) return;

        // UTF-8 output so the TUI's box-drawing chars (─, ❯) render. Raw Win32 (not
        // Console.OutputEncoding, which re-inits the .NET console and clears the VT flag we set next).
        _origCp = NativeMethods.GetConsoleOutputCP();
        NativeMethods.SetConsoleOutputCP(NativeMethods.CP_UTF8);

        // Output: enable VT processing and stop the auto-CR-on-wrap so the shell controls layout.
        var outMode = _origOut | NativeMethods.ENABLE_PROCESSED_OUTPUT
                                | NativeMethods.ENABLE_VIRTUAL_TERMINAL_PROCESSING
                                | NativeMethods.DISABLE_NEWLINE_AUTO_RETURN;
        NativeMethods.SetConsoleMode(_outHandle, outMode);

        // Input: raw — deliver VT input sequences, and stop the host from doing line buffering,
        // echo, or its own Ctrl+C handling (those belong to the shell inside the PTY).
        var inMode = (_origIn & ~(NativeMethods.ENABLE_LINE_INPUT
                                  | NativeMethods.ENABLE_ECHO_INPUT
                                  | NativeMethods.ENABLE_PROCESSED_INPUT))
                     | NativeMethods.ENABLE_VIRTUAL_TERMINAL_INPUT;
        NativeMethods.SetConsoleMode(_inHandle, inMode);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
    }

    // Current host window size, clamped to positive shorts for COORD.
    public static (short Width, short Height) Size()
    {
        short w = 80, h = 30;
        try { w = (short)Math.Clamp(Console.WindowWidth, 1, short.MaxValue); } catch { }
        try { h = (short)Math.Clamp(Console.WindowHeight, 1, short.MaxValue); } catch { }
        return (w, h);
    }

    public void Restore()
    {
        if (_restored || !_ok) return;
        _restored = true;
        NativeMethods.SetConsoleMode(_inHandle, _origIn);
        NativeMethods.SetConsoleMode(_outHandle, _origOut);
        if (_origCp != 0) NativeMethods.SetConsoleOutputCP(_origCp);
    }

    public void Dispose() => Restore();
}
