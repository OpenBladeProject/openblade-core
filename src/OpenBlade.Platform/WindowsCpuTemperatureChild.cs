using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Platform;

internal sealed class WindowsCpuTemperatureChild : ICpuTemperatureChild
{
    private readonly Process process;
    private readonly CpuTemperatureWorkerJob job;
    private readonly SafeProcessHandle processHandle;
    private readonly Exception? startupFailure;

    private WindowsCpuTemperatureChild(Process process, CpuTemperatureWorkerJob job,
        SafeProcessHandle processHandle, Exception? startupFailure)
    {
        this.process = process;
        this.job = job;
        this.processHandle = processHandle;
        this.startupFailure = startupFailure;
    }

    internal static ICpuTemperatureChild Start()
    {
        var job = new CpuTemperatureWorkerJob();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "OpenBlade.CpuTemperature.exe"),
                Arguments = "--worker",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            },
        };
        SafeProcessHandle? retained = null;
        try
        {
            if (!process.Start()) { throw new InvalidOperationException("The CPU sensor worker did not start."); }
            retained = process.SafeHandle; // Retain the original process, never reopen a PID.
            job.Assign(retained);
            // The worker must not initialize hardware until it receives our first
            // request. Job assignment therefore precedes all sensor activity.
            return new WindowsCpuTemperatureChild(process, job, retained, null);
        }
        catch (Exception exception)
        {
            if (retained is not null)
            {
                // Keep failed assignment owned by the reader too: cleanup must prove
                // process exit before the client can launch a replacement.
                return new WindowsCpuTemperatureChild(process, job, retained, exception);
            }
            process.Dispose();
            job.Dispose();
            throw;
        }
    }

    public async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken)
    {
        if (startupFailure is not null)
        {
            throw new InvalidOperationException("The CPU sensor worker was not assigned to its lifetime owner.", startupFailure);
        }
        await process.StandardInput.BaseStream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        byte[] reply = new byte[CpuTemperatureProtocol.ReplyLength];
        await process.StandardOutput.BaseStream.ReadExactlyAsync(reply, cancellationToken).ConfigureAwait(false);
        return reply;
    }

    public void RequestStop()
    {
        job.Dispose();
        // Also covers assignment failure. This targets the retained kernel handle,
        // not a PID that could have been reused. No child tree enumeration or join.
        _ = TerminateProcess(processHandle, 1);
    }

    public Task WaitForExitAsync() => process.WaitForExitAsync();

    public void Dispose()
    {
        job.Dispose();
        process.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}

/// <summary>Unnamed, explicitly non-inheritable, crash-safe owner of one sensor worker.</summary>
internal sealed class CpuTemperatureWorkerJob : IDisposable
{
    private readonly SafeJobHandle handle;

    internal CpuTemperatureWorkerJob()
    {
        handle = CreateJobObjectW(nint.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Error();
        }
        try
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x00002000 } };
            if (!SetHandleInformation(handle, 1, 0)
                || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            {
                throw Error();
            }
        }
        catch { handle.Dispose(); throw; }
    }

    internal void Assign(SafeProcessHandle process)
    {
        if (!AssignProcessToJobObject(handle, process)) { throw Error(); }
    }

    public void Dispose() => handle.Dispose();

    private static Win32Exception Error() => new(Marshal.GetLastWin32Error(), "CPU sensor worker lifetime ownership failed.");

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
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
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        internal BasicLimits Basic;
        internal IoCounters Io;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeJobHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle handle, int informationClass,
        ref ExtendedLimits information, uint informationLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
