using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HandRaise.Infrastructure.Windows.Training;

/// <summary>Kill-on-close Windows Job Object: descendants die even if Edge itself crashes.</summary>
internal sealed class WindowsTrainingProcessLease : IDisposable
{
    private readonly SafeFileHandle _handle;
    public WindowsTrainingProcessLease(Process process)
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(_handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimit>())
                || !AssignProcessToJobObject(_handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { _handle.Dispose(); throw; }
    }
    public void Dispose() => _handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimit info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
