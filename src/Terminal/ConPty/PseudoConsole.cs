namespace KY.AI.Terminal.ConPty;

// Wraps an HPCON (pseudoconsole handle). Created from the read side of the input pipe and the
// write side of the output pipe; the spawned shell renders into it as if into a real console.
internal sealed class PseudoConsole : IDisposable
{
    public nint Handle { get; }

    private PseudoConsole(nint handle) => Handle = handle;

    public static PseudoConsole Create(PseudoConsolePipe input, PseudoConsolePipe output, short width, short height)
    {
        var size = new NativeMethods.COORD(Math.Max((short)1, width), Math.Max((short)1, height));
        var hr = NativeMethods.CreatePseudoConsole(size, input.ReadSide, output.WriteSide, 0, out var hpc);
        if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole failed (HRESULT 0x{hr:X8}).");
        return new PseudoConsole(hpc);
    }

    public void Resize(short width, short height)
    {
        if (Handle == nint.Zero) return;
        NativeMethods.ResizePseudoConsole(Handle, new NativeMethods.COORD(Math.Max((short)1, width), Math.Max((short)1, height)));
    }

    public void Dispose()
    {
        if (Handle != nint.Zero) NativeMethods.ClosePseudoConsole(Handle);
    }
}
