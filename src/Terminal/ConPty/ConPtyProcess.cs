using System.Runtime.InteropServices;

namespace KY.AI.Terminal.ConPty;

// Spawns a child process attached to a pseudoconsole via STARTUPINFOEX +
// PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE. Owns the proc-thread attribute list and the process/
// thread handles; exposes the raw process handle so it can be put under a JobObject.
internal sealed class ConPtyProcess : IDisposable
{
    private nint _attrList;
    private NativeMethods.PROCESS_INFORMATION _pi;
    private bool _disposed;

    public nint ProcessHandle => _pi.hProcess;
    public int Pid => _pi.dwProcessId;

    public static ConPtyProcess Start(string commandLine, string? workingDir, PseudoConsole console)
    {
        var p = new ConPtyProcess();

        // Size and build a one-entry attribute list carrying the pseudoconsole handle.
        nint size = nint.Zero;
        NativeMethods.InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref size);
        p._attrList = Marshal.AllocHGlobal(size);
        if (!NativeMethods.InitializeProcThreadAttributeList(p._attrList, 1, 0, ref size))
        {
            p.Dispose();
            throw new InvalidOperationException("InitializeProcThreadAttributeList failed.");
        }
        if (!NativeMethods.UpdateProcThreadAttribute(
                p._attrList, 0, NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                console.Handle, nint.Size, nint.Zero, nint.Zero))
        {
            p.Dispose();
            throw new InvalidOperationException("UpdateProcThreadAttribute failed.");
        }

        var si = new NativeMethods.STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>();
        si.lpAttributeList = p._attrList;

        // bInheritHandles must be false for ConPTY — the console is wired in via the attribute,
        // not via inherited std handles.
        var ok = NativeMethods.CreateProcess(
            null, commandLine, nint.Zero, nint.Zero, false,
            NativeMethods.EXTENDED_STARTUPINFO_PRESENT, nint.Zero, workingDir,
            ref si, out p._pi);
        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            p.Dispose();
            throw new InvalidOperationException($"CreateProcess failed (Win32 error {err}) for: {commandLine}");
        }
        return p;
    }

    public bool HasExited
    {
        get
        {
            if (_pi.hProcess == nint.Zero) return true;
            return NativeMethods.WaitForSingleObject(_pi.hProcess, 0) == NativeMethods.WAIT_OBJECT_0;
        }
    }

    public int WaitForExit()
    {
        if (_pi.hProcess == nint.Zero) return -1;
        NativeMethods.WaitForSingleObject(_pi.hProcess, NativeMethods.INFINITE);
        return NativeMethods.GetExitCodeProcess(_pi.hProcess, out var code) ? (int)code : -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pi.hThread != nint.Zero) NativeMethods.CloseHandle(_pi.hThread);
        if (_pi.hProcess != nint.Zero) NativeMethods.CloseHandle(_pi.hProcess);
        if (_attrList != nint.Zero)
        {
            NativeMethods.DeleteProcThreadAttributeList(_attrList);
            Marshal.FreeHGlobal(_attrList);
            _attrList = nint.Zero;
        }
    }
}
