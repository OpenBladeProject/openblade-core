using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.SessionAgent;

/// <summary>Kills the assigned Snap Tap worker when its supervisor closes this job.</summary>
internal sealed class SnapTapWorkerJob : IDisposable
{
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
    internal const int JobObjectExtendedLimitInformationClass = 9;
    internal const uint HandleFlagInherit = 0x00000001;

    private readonly SafeJobHandle job;
    private bool disposed;

    internal SnapTapWorkerJob()
    {
        job = Native.CreateJobObjectW(nint.Zero, null);
        if (job.IsInvalid)
        {
            throw NativeError("The Snap Tap worker lifetime owner could not be created.");
        }

        try
        {
            // NULL security attributes already request a non-inheritable handle.
            // Clear the flag explicitly so later startup changes cannot inherit it.
            if (!Native.SetHandleInformation(job, HandleFlagInherit, 0))
            {
                throw NativeError("The Snap Tap worker lifetime handle could not be protected.");
            }

            JobObjectExtendedLimitInformation limits = CreateKillOnCloseLimits();
            if (!Native.SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformationClass,
                    ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw NativeError("The Snap Tap worker lifetime policy could not be configured.");
            }
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    internal void Assign(Process process)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(process);
        if (!Native.AssignProcessToJobObject(job, process.SafeHandle))
        {
            throw NativeError("The Snap Tap worker could not be assigned to its lifetime owner.");
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            job.Dispose();
        }
    }

    internal static JobObjectExtendedLimitInformation CreateKillOnCloseLimits() => new()
    {
        BasicLimitInformation = new JobObjectBasicLimitInformation
        {
            LimitFlags = JobObjectLimitKillOnJobClose,
        },
    };

    private static Win32Exception NativeError(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }

    private static class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern SafeJobHandle CreateJobObjectW(nint attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(
            SafeJobHandle handle,
            uint mask,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeJobHandle job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(
            SafeJobHandle job,
            SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(nint handle);
    }
}
