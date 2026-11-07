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
    private const int JobObjectExtendedLimitInformation = 9;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(40);

    internal static async Task RunCaller(string root, string mode, string[] payload)
    {
        using var timeout = new CancellationTokenSource(Limit);
        using var lifetime = So6120GuardianCallerLifetime.CreateForCurrentProcess();
        bool duplicateRefused = false;
        try { using var duplicate = So6120GuardianCallerLifetime.CreateForCurrentProcess(); }
        catch (InvalidOperationException) { duplicateRefused = true; }
        if (!duplicateRefused) { throw new InvalidOperationException("The fake caller acquired a second private job lifetime."); }

        using var restrictiveJob = CreateJobObjectW(0, null);
        if (mode == "restricted")
        {
            var limits = new ExtendedJobLimits();
            if (restrictiveJob.IsInvalid || !SetInformationJobObject(restrictiveJob, JobObjectExtendedLimitInformation,
                    ref limits, (uint)Marshal.SizeOf<ExtendedJobLimits>()) ||
                !AssignProcessToJobObject(restrictiveJob, GetCurrentProcess()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the fake caller to its restrictive test job.");
            }
        }

        using var control = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        string eventName = "ob-guardian-" + Guid.NewGuid().ToString("N");
        using var unrelated = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        if (!SetHandleInformation(unrelated.SafeWaitHandle, HandleFlagInherit, HandleFlagInherit))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not make the unrelated test handle inheritable.");
        }

        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Fake apphost path missing.");
        using var memberQuery = mode == "disconnect" ? DuplicateMemberQuery(lifetime.JobQueryHandle) : null;
        string unrelatedHandle = unrelated.SafeWaitHandle.DangerousGetHandle().ToString(CultureInfo.InvariantCulture);
        string jobHandle = lifetime.JobQueryHandle.DangerousGetHandle().ToString(CultureInfo.InvariantCulture);
        string callerHandle = lifetime.CallerQueryHandle.DangerousGetHandle().ToString(CultureInfo.InvariantCulture);
        string[] args = ["launch-owner", root, mode, control.GetClientHandleAsString(), jobHandle, callerHandle,
            unrelatedHandle, eventName, .. payload];
        So6120GuardianChild child;
        try
        {
            child = So6120GuardianProcess.Start(executable, args, control.ClientSafePipeHandle, showConsole: false, lifetime);
        }
        catch (Win32Exception error) when (mode == "restricted")
        {
            Program.WriteReport(root, "rejected.json", error.NativeErrorCode);
            return;
        }
        finally { control.DisposeLocalCopyOfClientHandle(); }

        using (child)
        {
            // Production roots the owning job for the process lifetime. Closing
            // the launcher query duplicates must not end this caller or its job.
            lifetime.Dispose();
            // A regular child may inherit every inheritable handle. The only
            // remaining caller-job query is explicitly noninheritable.
            using var member = mode == "disconnect" ? StartJobMember(executable, root, memberQuery!) : null;
            memberQuery?.Dispose();
            Program.WriteReport(root, "launched.json", child.Id);
            if (mode == "exit-259") { Environment.Exit(259); }
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

    private static SafeFileHandle DuplicateMemberQuery(SafeFileHandle job)
    {
        nint current = GetCurrentProcess();
        if (!DuplicateHandle(current, job, current, out SafeFileHandle duplicate, 0, false, 2))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not duplicate the fake member query.");
        }
        return duplicate;
    }

    private static Process StartJobMember(string executable, string root, SafeFileHandle callerJob)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "launch-job-member", root },
        };
        var member = Process.Start(start) ?? throw new InvalidOperationException("Could not start the fake caller job member.");
        try
        {
            if (!IsProcessInJob(member.SafeHandle, callerJob, out bool inJob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect the fake caller job member.");
            }
            if (!inJob) { throw new InvalidOperationException("The fake child did not inherit the caller's private job."); }
            Program.WriteReport(root, "member.json", member.Id);
            return member;
        }
        catch { member.Dispose(); throw; }
    }

    internal static async Task RunJobMember(string root)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (!File.Exists(Path.Combine(root, "release")))
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    internal static async Task<int> RunOwner(string root, string mode, string control, string jobHandle,
        string callerHandle, string unrelatedHandle, string eventName, string[] payload)
    {
        So6120GuardianIsolationEvidence isolation;
        using (var job = new SafeFileHandle(nint.Parse(jobHandle, CultureInfo.InvariantCulture), ownsHandle: true))
        using (var caller = new SafeProcessHandle(nint.Parse(callerHandle, CultureInfo.InvariantCulture), ownsHandle: true))
        {
            if (mode == "exit-259")
            {
                if (WaitForSingleObject(caller, 10_000) != 0)
                {
                    throw new InvalidOperationException("The synthetic original caller did not reach its terminal status.");
                }
                // Keep the fake owner alive until the test has retained its
                // process handle, avoiding a PID lookup after owner exit.
                await RunJobMember(root);
            }
            if (mode == "wrong-job")
            {
                using var unrelatedJob = CreateJobObjectW(0, null);
                var limits = new ExtendedJobLimits { Basic = new BasicJobLimits { Flags = BreakawayOk | KillOnJobClose } };
                if (!SetInformationJobObject(unrelatedJob, JobObjectExtendedLimitInformation, ref limits,
                        (uint)Marshal.SizeOf<ExtendedJobLimits>()))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                isolation = So6120GuardianProcess.VerifyOwnerIsolation(unrelatedJob, caller);
            }
            else { isolation = So6120GuardianProcess.VerifyOwnerIsolation(job, caller); }
        } // Release inherited queries before readiness and any fake acquisition.

        using var pipe = new AnonymousPipeClientStream(PipeDirection.In, control);
        So6120GuardianProcess.VerifyInheritedControl(pipe.SafePipeHandle);
        using var unrelated = EventWaitHandle.OpenExisting(eventName);
        nint candidate = nint.Parse(unrelatedHandle, CultureInfo.InvariantCulture);
        // OpenExisting can itself occupy the numeric slot the caller used.
        bool inheritedUnrelated = candidate != unrelated.SafeWaitHandle.DangerousGetHandle()
            && CompareObjectHandles(candidate, unrelated.SafeWaitHandle);
        Program.WriteReport(root, "ready.json", new LaunchReadyReport(Environment.ProcessId, inheritedUnrelated,
            isolation.CallerProcessId, isolation.InAmbientJob, payload));
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
        try { So6120GuardianProcess.VerifyOwnerConsoleIsolation(); }
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

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref ExtendedJobLimits info, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, SafeFileHandle source, nint targetProcess,
        out SafeFileHandle target, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool inJob);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [DllImport("kernelbase.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CompareObjectHandles(nint first, SafeWaitHandle second);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeWaitHandle handle, uint mask, uint flags);
}

public sealed record LaunchReadyReport(int ProcessId, bool InheritedUnrelated, int CallerProcessId,
    bool InAmbientJob, string[] Payload);
