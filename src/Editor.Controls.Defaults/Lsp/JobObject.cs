using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Editor.Controls.Lsp;

/// <summary>Wraps a Win32 Job Object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: every process
/// assigned to it is killed by the OS when the job's last handle closes — including when that happens because
/// this process died ungracefully (crash, Task Manager "End Task", a debugger force-stop). <see cref="LspProcess.Dispose"/>
/// already kills the language-server child on a normal shutdown; this covers the case Dispose can never run for.
/// One job is shared process-wide (assigning many processes to one job is the documented, supported usage).</summary>
internal static class JobObject
{
    private static readonly IntPtr Handle = Create();

    /// <summary>Assigns <paramref name="process"/> to the shared kill-on-close job. Safe to call even if job
    /// creation failed (e.g. sandboxed environments without the privilege) — silently a no-op then.</summary>
    public static void Assign(Process process)
    {
        if (Handle == IntPtr.Zero) return;
        try { AssignProcessToJobObject(Handle, process.Handle); } catch { /* best-effort */ }
    }

    private static IntPtr Create()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE };
            var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = info };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(extended, ptr, false);
                if (!SetInformationJobObject(job, JobObjectInfoType.ExtendedLimitInformation, ptr, (uint)length))
                    return IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(ptr); }

            return job;
        }
        catch { return IntPtr.Zero; }  // best-effort: no Job Object support → orphans still possible, but no crash
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoType { ExtendedLimitInformation = 9 }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
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
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
