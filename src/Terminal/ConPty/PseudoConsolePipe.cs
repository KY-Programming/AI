using Microsoft.Win32.SafeHandles;

namespace KY.AI.Terminal.ConPty;

// One anonymous pipe pair (a read side and a write side) created via CreatePipe. The terminal
// uses two: an input pipe (we write keystrokes to WriteSide; ConPTY reads ReadSide) and an
// output pipe (ConPTY writes WriteSide; we read ReadSide).
internal sealed class PseudoConsolePipe : IDisposable
{
    public SafeFileHandle ReadSide { get; }
    public SafeFileHandle WriteSide { get; }

    public PseudoConsolePipe()
    {
        if (!NativeMethods.CreatePipe(out var read, out var write, nint.Zero, 0))
            throw new InvalidOperationException("CreatePipe failed while setting up the pseudoconsole.");
        ReadSide = read;
        WriteSide = write;
    }

    public void Dispose()
    {
        ReadSide.Dispose();
        WriteSide.Dispose();
    }
}
