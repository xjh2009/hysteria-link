using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hysteria2Link.Plugin.Infrastructure;

internal sealed class ProcessJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeFileHandle? _handle;
    private readonly Exception? _initializationError;

    public ProcessJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            _initializationError = new PlatformNotSupportedException("Hysteria 进程作业只支持 Windows。");
            return;
        }

        var rawHandle = CreateJobObject(IntPtr.Zero, null);
        if (rawHandle == IntPtr.Zero)
        {
            _initializationError = new Win32Exception(Marshal.GetLastWin32Error());
            return;
        }

        var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(handle, 9, buffer, (uint)size))
            {
                var exception = new Win32Exception(Marshal.GetLastWin32Error());
                handle.Dispose();
                _initializationError = exception;
                return;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        _handle = handle;
    }

    public void Assign(Process process)
    {
        if (_handle is null || _handle.IsInvalid)
            throw new InvalidOperationException("Hysteria 进程作业不可用，已阻止启动内核。", _initializationError);

        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new InvalidOperationException(
                "无法把 Hysteria 加入进程作业。",
                new Win32Exception(Marshal.GetLastWin32Error()));
    }

    public void Dispose() => _handle?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public long Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
