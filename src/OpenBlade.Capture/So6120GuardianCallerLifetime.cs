using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Capture;

internal sealed class So6120GuardianCallerLifetime : IDisposable
{
    internal const uint RequiredJobFlags = 0x00002000 | 0x00000800; // KILL_ON_JOB_CLOSE | BREAKAWAY_OK
    private const uint JobObjectQuery = 0x0004;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const int ExtendedLimitInformation = 9;
    private static readonly object CreationLock = new();
    private static SafeFileHandle? processLifetimeJob;
    private static bool creationAttempted;

    private So6120GuardianCallerLifetime(SafeFileHandle job, SafeProcessHandle caller)
    {
        JobQueryHandle = job;
        CallerQueryHandle = caller;
    }

    internal SafeFileHandle JobQueryHandle { get; }
    internal SafeProcessHandle CallerQueryHandle { get; }

    internal static So6120GuardianCallerLifetime CreateForCurrentProcess()
    {
        lock (CreationLock)
        {
            if (creationAttempted || processLifetimeJob is not null)
            {
                throw new InvalidOperationException("The guardian caller lifetime can be created only once per process.");
            }

            creationAttempted = true;
            SafeFileHandle? owningJob = CreateJobObjectW(0, null);
            SafeFileHandle? queryJob = null;
            SafeProcessHandle? queryCaller = null;
            try
            {
                var limits = new JobLimits { Basic = new BasicLimits { Flags = RequiredJobFlags } };
                if (owningJob.IsInvalid || !SetInformationJobObject(owningJob, ExtendedLimitInformation, ref limits,
                        (uint)Marshal.SizeOf<JobLimits>()))
                {
                    throw NativeFailure("Creating the guardian caller job failed.");
                }

                nint current = GetCurrentProcess();
                if (!DuplicateHandle(current, owningJob, current, out queryJob, JobObjectQuery, true, 0))
                {
                    throw NativeFailure("Creating the guardian caller job query handle failed.");
                }

                queryCaller = OpenProcess(ProcessQueryLimitedInformation | Synchronize, true, (uint)Environment.ProcessId);
                if (queryCaller.IsInvalid)
                {
                    throw NativeFailure("Creating the original caller process query handle failed.");
                }

                if (!AssignProcessToJobObject(owningJob, current))
                {
                    throw NativeFailure("Assigning the guardian caller to its private job failed.");
                }

                // Closing the last job handle while this process is alive would
                // terminate the caller. Root it until OS process teardown.
                processLifetimeJob = owningJob;
                owningJob = null;
                var result = new So6120GuardianCallerLifetime(queryJob, queryCaller);
                queryJob = null;
                queryCaller = null;
                return result;
            }
            finally
            {
                queryCaller?.Dispose();
                queryJob?.Dispose();
                owningJob?.Dispose(); // Only an unassigned job can reach here.
            }
        }
    }

    internal static uint ReadJobFlags(nint job)
    {
        if (!QueryInformationJobObject(job, ExtendedLimitInformation, out JobLimits limits,
                (uint)Marshal.SizeOf<JobLimits>(), out _))
        {
            throw NativeFailure("Reading the inherited caller job contract failed.");
        }

        return limits.Basic.Flags;
    }

    public void Dispose()
    {
        CallerQueryHandle.Dispose();
        JobQueryHandle.Dispose();
    }

    private static Win32Exception NativeFailure(string message) => new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        internal uint Flags;
        internal nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        internal BasicLimits Basic;
        internal IoCounters Io;
        internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref JobLimits information, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(nint job, int informationClass,
        out JobLimits information, uint size, out uint returnedSize);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, SafeFileHandle source, nint targetProcess,
        out SafeFileHandle target, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();
}
