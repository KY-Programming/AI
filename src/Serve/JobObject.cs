using System.Runtime.InteropServices;

namespace KY.AI.Serve;

// A Windows Job Object configured with KILL_ON_JOB_CLOSE: every assigned process is
// terminated by the OS when the last handle to the job closes — which happens whenever
// THIS process dies, including a hard TerminateProcess (Rider's Stop button) that runs
// none of our graceful handlers. This guarantees the dev-server child tree can never
// orphan and hold the port, regardless of how the supervisor is stopped.
internal sealed class JobObject : IDisposable
{
    private readonly nint _handle;

    public JobObject()
    {
        _handle = CreateJobObject(nint.Zero, null);
        if (_handle == nint.Zero) return;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        var len = Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, (uint)len);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    // Put a process (and, by inheritance, its descendants) under the job. Best-effort:
    // if it fails we still have the graceful Ctrl+C / shutdown tree-kill as a fallback.
    public void Assign(System.Diagnostics.Process process)
    {
        if (_handle == nint.Zero) return;
        try { AssignProcessToJobObject(_handle, process.Handle); } catch { }
    }

    // Overload for a raw process handle (e.g. PROCESS_INFORMATION.hProcess from CreateProcess),
    // so a ConPTY-spawned shell can be reaped without first wrapping it in a Process.
    public void Assign(nint hProcess)
    {
        if (_handle == nint.Zero || hProcess == nint.Zero) return;
        try { AssignProcessToJobObject(_handle, hProcess); } catch { }
    }

    public void Dispose()
    {
        if (_handle != nint.Zero) CloseHandle(_handle);
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint hJob, int infoType, nint lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
