using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Capture;

namespace OpenBlade.Guardian.TestHost;

internal static class GuardianLaunchFixture
{
    private const uint BreakawayOk = 0x00000800;
    private const uint KillOnJobClose = 0x00002000;
    private const uint HandleFlagInherit = 1;
    private const uint JobObjectQuery = 4;
    private const int JobObjectExtendedLimitInformation = 9;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(40);

    internal static async Task RunCaller(string root, string mode, string[] payload)
    {
        using var timeout = new CancellationTokenSource(Limit);
        string jobName = "ob-guardian-job-" + Guid.NewGuid().ToString("N");
        using var job = CreateJobObjectW(0, jobName);
        var limits = new ExtendedJobLimits
        {
            Basic = new BasicJobLimits
            {
                Flags = mode == "restricted" ? 0 : BreakawayOk | (mode == "disconnect" ? KillOnJobClose : 0),
            },
        };
        if (job.IsInvalid || !SetInformationJobObject(job, JobObjectExtendedLimitInformation,
                ref limits, (uint)Marshal.SizeOf<ExtendedJobLimits>())
            || !AssignProcessToJobObject(job, GetCurrentProcess()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the fake caller to its test job.");
        }

        using var control = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        string eventName = "ob-guardian-" + Guid.NewGuid().ToString("N");
        using var unrelated = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        if (!SetHandleInformation(unrelated.SafeWaitHandle, HandleFlagInherit, HandleFlagInherit))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not make the unrelated test handle inheritable.");
        }

        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Fake apphost path missing.");
        string unrelatedHandle = unrelated.SafeWaitHandle.DangerousGetHandle().ToString(CultureInfo.InvariantCulture);
        string[] args = ["launch-owner", root, mode, control.GetClientHandleAsString(), jobName, unrelatedHandle, eventName, .. payload];
        So6120GuardianChild child;
        try
        {
            child = So6120GuardianProcess.Start(executable, args, control.ClientSafePipeHandle, showConsole: false);
        }
        catch (Win32Exception error) when (mode == "restricted")
        {
            Program.WriteReport(root, "rejected.json", error.NativeErrorCode);
            return;
        }
        finally { control.DisposeLocalCopyOfClientHandle(); }

        using (child)
        {
            Program.WriteReport(root, "launched.json", child.Id);
            if (mode == "observe")
            {
                using var cancelWait = new CancellationTokenSource();
                Task<int> canceledWait = child.WaitForExitAsync(cancelWait.Token);
                cancelWait.Cancel();
                try
                {
                    await canceledWait;
                    throw new InvalidOperationException("A canceled fake-owner wait completed successfully.");
                }
                catch (OperationCanceledException) when (cancelWait.IsCancellationRequested) { }

                Task<int> exit = child.WaitForExitAsync(timeout.Token);
                child.Dispose();
                control.WriteByte(2);
                control.Flush();
                Program.WriteReport(root, "exit.json", await exit);
                return;
            }

            Task<int> completion = child.WaitForExitAsync(timeout.Token);
            while (mode == "disconnect" && !completion.IsCompleted)
            {
                control.WriteByte(1);
                await Task.WhenAny(completion, Task.Delay(100, timeout.Token));
            }

            if (await completion != 23)
            {
                throw new InvalidOperationException("The fake guardian did not reach its expected exit.");
            }
        }
    }

    internal static async Task<int> RunOwner(string root, string mode, string control, string jobName,
        string unrelatedHandle, string eventName, string[] payload)
    {
        bool outsideCallerJob;
        using (var callerJob = OpenJobObjectW(JobObjectQuery, false, jobName))
        {
            if (callerJob.IsInvalid || !IsProcessInJob(GetCurrentProcess(), callerJob, out bool inCallerJob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not check the fake caller job.");
            }

            outsideCallerJob = !inCallerJob;
        } // Release the query handle before the caller can close its last job handle.

        if (!outsideCallerJob) { throw new InvalidOperationException("The fake owner retained the caller's job."); }
        bool productionIsolationPassed = true;
        try { So6120GuardianProcess.VerifyOwnerIsolation(); }
        catch (InvalidOperationException error) when (error.Message == "The guardian must run outside a Windows job.")
        {
            // A restrictive host ancestor can retain the owner even after it
            // escapes the caller's job. Production refuses. Only this fake
            // apphost proceeds, after the production console checks passed.
            productionIsolationPassed = false;
        }

        using var pipe = new AnonymousPipeClientStream(PipeDirection.In, control);
        So6120GuardianProcess.VerifyInheritedControl(pipe.SafePipeHandle);
        using var unrelated = EventWaitHandle.OpenExisting(eventName);
        nint candidate = nint.Parse(unrelatedHandle, CultureInfo.InvariantCulture);
        // OpenExisting can itself occupy the numeric slot the caller used.
        bool inheritedUnrelated = candidate != unrelated.SafeWaitHandle.DangerousGetHandle()
            && CompareObjectHandles(candidate, unrelated.SafeWaitHandle);
        Program.WriteReport(root, "ready.json", new LaunchReadyReport(Environment.ProcessId, inheritedUnrelated,
            productionIsolationPassed, outsideCallerJob, payload));
        if (mode == "disconnect")
        {
            await RunTrialUntilDisconnect(root, pipe);
            return 23;
        }

        int command = pipe.ReadByte();
        Program.WriteReport(root, "control.json", command);

        using var timeout = new CancellationTokenSource(Limit);
        if (mode == "observe")
        {
            // A normal child inherits this fake owner's console, unlike the
            // guardian launcher. Both processes stay attached during the probe.
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                ArgumentList = { "probe-shared-console" },
            };
            using var probe = Process.Start(start) ?? throw new InvalidOperationException("Fake console probe launch failed.");
            await probe.WaitForExitAsync(timeout.Token);
            Program.WriteReport(root, "shared-console.json", probe.ExitCode);
        }

        while (!File.Exists(Path.Combine(root, "release")))
        {
            await Task.Delay(20, timeout.Token);
        }

        return 23;
    }

    internal static int ProbeSharedConsole()
    {
        try { So6120GuardianProcess.VerifyOwnerIsolation(); }
        catch (InvalidOperationException error) when (error.Message == "The guardian cannot run in a shared console.")
        { return 0; }
        return 1;
    }

    private static async Task RunTrialUntilDisconnect(string root, AnonymousPipeClientStream pipe)
    {
        using var timeout = new CancellationTokenSource(Limit);
        await using var signals = new So6120GuardianSignals();
        var reader = So6120GuardianHost.ReadControlAsync(pipe, signals, timeout.Token);
        try
        {
            await signals.FirstHeartbeat.WaitAsync(timeout.Token);
            var trial = new FakeTrial(root, "zero-checkpoint");
            var guardian = So6120TrialGuardian.Start(trial.Prepare, signals: signals);
            await reader;
            var snapshot = guardian.Snapshot;
            Program.WriteReport(root, "disconnected.json", new DisconnectReport(Environment.ProcessId,
                snapshot.StopReason.ToString(), snapshot.OwnerRunning));
            Program.WriteTerminalReport(root, await guardian.Completion.WaitAsync(timeout.Token));
        }
        finally
        {
            await timeout.CancelAsync();
            await reader;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicJobLimits
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
    private struct ExtendedJobLimits
    {
        internal BasicJobLimits Basic;
        internal IoCounters Io;
        internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle OpenJobObjectW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(nint process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref ExtendedJobLimits info, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernelbase.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CompareObjectHandles(nint first, SafeWaitHandle second);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeWaitHandle handle, uint mask, uint flags);
}

public sealed record LaunchReadyReport(int ProcessId, bool InheritedUnrelated, bool ProductionIsolationPassed,
    bool OutsideCallerJob, string[] Payload);
