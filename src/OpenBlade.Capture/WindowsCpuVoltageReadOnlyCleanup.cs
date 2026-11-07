using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Capture;

// One failed readonly job, using the session host's actual launched owner and
// retained Service objects. No caller-supplied PID/path, driver creation or CPU API.
internal sealed class WindowsCpuVoltageReadOnlyCleanup : ICpuVoltageReadOnlyCleanupPlatform
{
    private readonly WindowsCpuVoltageSessionPlatform session;
    private readonly WindowsCpuVoltageServiceProcess service;
    private readonly Process owner;
    private readonly CpuPmRetainedLaunchProcess retainedOwner;
    private readonly Task ownerOutput;
    private readonly CpuVoltageReadOnlyCleanupProof proof;
    private readonly Guid run, request;
    private readonly Func<bool> contextCurrent;
    private readonly Func<string?> ownerReason;
    private readonly WindowsCpuPmDriverLifecyclePlatform drivers = new();
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
    private ICpuPmCreatedDriver? driver;
    private ICpuPmDriverMutex? mutex;
    private bool serviceTerminal, ownerTerminal, stopRequested, driverAbsent;
    private int phase;

    internal WindowsCpuVoltageReadOnlyCleanup(WindowsCpuVoltageSessionPlatform session,
        WindowsCpuVoltageServiceProcess service, Process owner, CpuPmRetainedLaunchProcess retainedOwner, Task ownerOutput,
        CpuVoltageReadOnlyCleanupProof proof, Guid run, Guid request, Func<bool> contextCurrent, Func<string?> ownerReason)
    {
        this.session = session; this.service = service; this.owner = owner; this.retainedOwner = retainedOwner;
        this.ownerOutput = ownerOutput;
        this.proof = proof; this.run = run; this.request = request; this.contextCurrent = contextCurrent; this.ownerReason = ownerReason;
        serviceTerminal = service.ReadState() == CpuPmChildState.Terminal;
    }
    public void Validate()
    {
        deadline.Token.ThrowIfCancellationRequested();
        if (!contextCurrent()) { throw new IOException("Cleanup requires the unchanged active AC session."); }
        session.Pins.AssertAllPins();
        RequireNoSmokeChild(proof.Child.ProcessId);
        if (CpuVoltageReadOnlyCleanup.Hash(session.ReadJournal(run)) != proof.JournalSha256)
        { throw new IOException("Original lifecycle changed; cleanup refused."); }
        CpuPmChildState ownerState = retainedOwner.ReadState();
        if (ownerState != (ownerTerminal ? CpuPmChildState.Terminal : CpuPmChildState.Running)
            && !(ownerState == CpuPmChildState.Terminal && !ownerTerminal))
        { throw new IOException("Retained owner state unavailable."); }
        if (ownerState == CpuPmChildState.Running && !CpuVoltageReadOnlyCleanup.IsParkReason(ownerReason()))
        { throw new IOException("Owner has not reported its terminal park path."); }
        service.AssertState(serviceTerminal ? CpuPmChildState.Terminal : CpuPmChildState.Running);
        if (driverAbsent)
        {
            if (!drivers.ReadIndependentAbsence().IsAbsent) { throw new IOException("Driver absence changed."); }
        }
        else if (driver is not null)
        {
            var snapshot = driver.ReadFresh();
            if (!snapshot.Owned(proof.Tag.Value) || (!stopRequested && snapshot.State != CpuPmDriverState.Running)
                || stopRequested && snapshot.State is not (CpuPmDriverState.StopPending or CpuPmDriverState.Stopped))
            { throw new IOException("Owned driver configuration/state changed."); }
            var inventory = drivers.ReadIndependentAbsence();
            if (inventory.ScmOpenError != 0 || inventory.Registry64Absent || !inventory.ActiveScanComplete
                || inventory.MatchingActiveDrivers != (snapshot.State == CpuPmDriverState.Stopped ? 0 : 1))
            { throw new IOException("Owned driver inventory changed."); }
        }
        deadline.Token.ThrowIfCancellationRequested();
    }
    public void ShutdownServiceAndVerifyTerminal()
    {
        session.VerifyCleanupVendorFiles();
        driver = WindowsCpuPmDriverRegistration.OpenExistingOwnedForCleanup(proof.Tag);
        Validate();
        Intent("ServiceShutdown");
        if (!serviceTerminal) { service.ShutdownOnce(deadline.Token, requireNoCpuRecovery: true); }
        Wait(() => service.ReadState() == CpuPmChildState.Terminal, TimeSpan.FromSeconds(30));
        service.AssertState(CpuPmChildState.Terminal); serviceTerminal = true;
    }
    public void TerminateParkedOwnerAndVerifyTerminal()
    {
        Validate();
        Intent("ParkedOwnerTerminal");
        if (retainedOwner.ReadState() == CpuPmChildState.Running)
        {
            // The SafeHandle belongs to Process.Start in THIS host. Never reopen
            // by PID; never terminate the FirmwareSmoke child or process tree.
            if (!TerminateProcess(owner.SafeHandle, 0xE0671001)) { throw new IOException("Parked owner termination unacknowledged."); }
        }
        Wait(() => retainedOwner.ReadState() == CpuPmChildState.Terminal, TimeSpan.FromSeconds(10));
        RequireOutputTerminal(ownerOutput, deadline.Token);
        ownerTerminal = true;
        session.PinTerminalJournal(run, proof.JournalSha256);
    }
    public void StopDeleteOwnedDriverAndVerifyAbsence()
    {
        mutex = drivers.AcquireMutex(deadline.Token);
        if (!mutex.IsOwnedByCurrentThread || mutex.WasAbandoned) { throw new IOException("Cleanup mutex uncertain."); }
        Validate(); Intent("OwnedDriverStop");
        if (!driver!.StopOnce()) { throw new IOException("Owned driver stop unacknowledged."); }
        stopRequested = true;
        Wait(() =>
        {
            Validate(); return driver.ReadFresh().State == CpuPmDriverState.Stopped;
        }, TimeSpan.FromSeconds(30));
        Validate(); Intent("OwnedDriverDelete");
        if (!driver.DeleteOnce()) { throw new IOException("Owned driver delete unacknowledged."); }
        driver.CloseAfterDelete();
        if (!drivers.ReadIndependentAbsence().IsAbsent) { throw new IOException("Independent driver absence unproved."); }
        driverAbsent = true;
    }
    public void RecordCompletionAndRelease()
    {
        Validate();
        if (!ownerTerminal || !serviceTerminal || !driverAbsent) { throw new IOException("Cleanup completion unproved."); }
        session.WriteNew("cleanup-" + request.ToString("N") + ".json", new
        {
            Run = run,
            Request = request,
            session.Enrollment.Source,
            session.Enrollment.Manifest,
            session.Enrollment.ServiceVersion,
            proof.ResultSha256,
            proof.JournalSha256,
            OwnerTag = proof.Tag.Value,
            Child = proof.Child,
            Owner = retainedOwner.Identity,
            Service = service.Identity,
            NoLiveSmokeChild = true,
            OwnerTerminal = true,
            ServiceTerminal = true,
            OwnedDriverAbsent = true,
            CpuSetAttempted = false,
            CpuRestoreAttempted = false,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });
        session.ReleaseVerifiedTerminalService(); retainedOwner.ReleaseAfterTerminal(); owner.Dispose();
        mutex!.ReleaseAndCloseOnCurrentThread(); mutex = null; deadline.Dispose();
    }
    private void Intent(string name) => session.WriteNew($"cleanup-{request:N}-{++phase:D2}.json", new
    { Run = run, Request = request, Phase = name, AtUtc = DateTimeOffset.UtcNow });
    internal static void RequireOutputTerminal(Task output, CancellationToken token)
    {
        // Join the original reader before releasing anything or rearming; no
        // buffered prior-owner callback can mutate the next job's diagnostics.
        if (!output.Wait(TimeSpan.FromSeconds(5), token)) { throw new IOException("Retained owner output did not reach terminal."); }
    }
    private void Wait(Func<bool> terminal, TimeSpan limit)
    {
        long start = Stopwatch.GetTimestamp();
        while (!terminal())
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(start) >= limit) { throw new IOException("Cleanup terminal proof timed out."); }
            Thread.Sleep(100);
        }
    }
    private static void RequireNoSmokeChild(int childPid)
    {
        var values = Process.GetProcessesByName("OpenBlade.FirmwareSmoke");
        try { if (values.Length != 0) { throw new IOException("A smoke child is still present; never terminate it."); } }
        finally { foreach (var value in values) { value.Dispose(); } }
        // Missing is recorded as absence only, never a terminal claim. A reused
        // PID or access error refuses; this check never adopts a process handle.
        Process? samePid;
        try { samePid = Process.GetProcessById(childPid); }
        catch (ArgumentException) { return; }
        samePid.Dispose(); throw new IOException("Recorded child PID is still present or reused.");
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
