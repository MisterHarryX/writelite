using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Windows Job Object with KILL_ON_JOB_CLOSE so orphaned children die with the parent.
/// </summary>
public interface IWriteLiteProcessJob : IDisposable
{
    bool IsAvailable { get; }
    bool TryAssign(Process process);
}

public sealed class WindowsJobObject : IWriteLiteProcessJob
{
    private SafeJobHandle? _handle;
    private bool _disposed;

    public WindowsJobObject()
    {
        try
        {
            _handle = CreateJob();
            if (_handle is { IsInvalid: false })
            {
                ConfigureKillOnClose(_handle);
            }
        }
        catch
        {
            _handle?.Dispose();
            _handle = null;
        }
    }

    public bool IsAvailable => _handle is { IsInvalid: false, IsClosed: false };

    public bool TryAssign(Process process)
    {
        if (!IsAvailable || process is null)
        {
            return false;
        }

        try
        {
            if (!NativeMethods.AssignProcessToJobObject(_handle!, process.Handle))
            {
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            // Job assignment is best-effort; the process still runs without containment.
            CompatibilityLogger.Technical("job-assign-failed", $"type={exception.GetType().Name}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle?.Dispose();
        _handle = null;
    }

    private static SafeJobHandle CreateJob()
    {
        var handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return handle;
    }

    private static void ConfigureKillOnClose(SafeJobHandle job)
    {
        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        var length = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!NativeMethods.SetInformationJobObject(
                    job,
                    NativeMethods.JobObjectInfoClass.JobObjectExtendedLimitInformation,
                    ptr,
                    (uint)length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true)
        {
        }

        protected override bool ReleaseHandle()
            => NativeMethods.CloseHandle(handle);
    }

    private static class NativeMethods
    {
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        public enum JobObjectInfoClass
        {
            JobObjectExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeJobHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(
            SafeJobHandle hJob,
            JobObjectInfoClass infoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
