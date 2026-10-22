using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Swiftdock.Snapshots;

// Workers inherit the coordinator's job. Killing just the coordinator therefore
// closes its last job handle and Windows removes every worker/window as well.
internal sealed class RenderProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    internal RenderProcessJob()
    {
        _handle = CreateJobObject(0, null);
        if (_handle.IsInvalid) throw new Win32Exception();
        try
        {
            SetLimits(0x2000); // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            using var process = Process.GetCurrentProcess();
            if (!AssignProcessToJobObject(_handle, process.Handle)) throw new Win32Exception();
        }
        catch { _handle.Dispose(); throw; }
    }

    public void Dispose()
    {
        // Normal completion already reaped all workers. Do not terminate ourselves
        // while returning the report's exit code; abnormal process exit keeps the flag.
        SetLimits(0);
        _handle.Dispose();
    }

    private void SetLimits(uint flags)
    {
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = flags } };
        if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw new Win32Exception();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessUserTime, JobUserTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
}
