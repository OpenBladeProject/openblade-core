using System.Diagnostics;
using System.Security.Cryptography;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

// Developer-only synchronous owner thread. Native lifecycle calls never run on a
// replacement task. Pipe proof work runs separately only while this thread merely
// observes the retained child, and is fully drained before lifecycle cleanup.
internal sealed class CpuPmNativeSupervisor
{
    private static CpuPmNativeSupervisor? processRoot;
    private static int claimed;
    private readonly Guid run;
    private readonly CpuPmObservationProfile profile;
    private readonly TextWriter output;
    private readonly object logGate = new();
    private CpuPmSupervisorBootstrap? bootstrap;
    private CpuPmParentDriverSafety? safety;
    private WindowsCpuPmLaunchPublication? publication;
    private CpuPmOwnedDriverLifecycle? lifecycle;
    private CpuPmAuthorityListenerSession? listener;
    private Task<CpuPmListenerResult>? serving;
    private Process? actualChild;
    private CpuPmRetainedLaunchProcess? child;
    private readonly CancellationTokenSource deadline = new();
    private readonly CancellationTokenSource stopListener = new();
    private Timer? watchdog;
    private int uncertain;
    private bool driverPossible, launchPossible;
    private CpuPmNativeSupervisor(Guid run, TextWriter output, CpuPmObservationProfile profile)
    { this.run = run; this.output = output; this.profile = profile; }
    internal static int Run(Guid run, TextWriter output) => RunProfile(run, output, CpuPmObservationProfile.IdleThreeSamples);
    internal static int RunRange(Guid run, TextWriter output) => RunProfile(run, output, CpuPmObservationProfile.RangeSingleSample);
    private static int RunProfile(Guid run, TextWriter output, CpuPmObservationProfile profile)
    {
        if (run == Guid.Empty || Interlocked.CompareExchange(ref claimed, 1, 0) != 0) { return 2; }
        var value = new CpuPmNativeSupervisor(run, output, profile); processRoot = value;
        return value.Execute();
    }
    private int Execute()
    {
        try
        {
            bootstrap = CpuPmSupervisorBootstrap.Load(run, true, CancellationToken.None);
            if (bootstrap.Trusted.Profile != profile) { throw new IOException("Reviewed supervisor profile mismatch."); }
            WindowsCpuPmFreshRunTree.Create(run);
            Guid lifetime = Guid.NewGuid(); var clock = new CpuPmAuthorityClock();
            safety = CpuPmParentDriverSafety.CreateNative(bootstrap.Trusted, true, CancellationToken.None)
                ?? throw new IOException("Parent safety unavailable.");
            bootstrap.AssertHeld();
            // Publication requires pristine control/evidence directories; its
            // owner record therefore precedes the journal's CREATE_NEW.
            publication = WindowsCpuPmLaunchPublication.Create(run, lifetime, bootstrap.Trusted, CancellationToken.None);
            var tag = new CpuPmDriverOwnerTag(run, lifetime, bootstrap.Trusted.ManifestSha256,
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
            var journal = WindowsCpuPmDriverJournalStorage.CreateForFixedRun(tag);
            lifecycle = new(tag, new WindowsCpuPmDriverLifecyclePlatform(),
                new CpuPmBootstrapBoundSafety(safety, bootstrap.AssertHeld), journal, clock);
            listener = WindowsCpuPmAuthorityListener.Create(run, lifetime, clock);
            watchdog = new Timer(_ => MarkUnknown("SupervisorBudgetExpired"), null, TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);
            driverPossible = true; // before a synchronous lifecycle call can submit
            if (!lifecycle.PrepareAndStart(deadline.Token)) { return PreserveUnknown("PrepareOrStartUnavailable"); }
            bootstrap.AssertHeld();
            if (!lifecycle.BeginChildLaunch(deadline.Token)) { return PreserveUnknown("ChildLaunchAdmissionDenied"); }
            launchPossible = true;
            var start = new ProcessStartInfo(Path.Combine(bootstrap.Trusted.PayloadPath, "OpenBlade.Capture.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = bootstrap.Trusted.PayloadPath };
            start.ArgumentList.Add(profile == CpuPmObservationProfile.RangeSingleSample
                ? Rz090528CpuPmObservationProbe.RangeName : Rz090528CpuPmObservationProbe.Name);
            start.ArgumentList.Add(Rz090528CpuPmObservationProbe.TransportApproval);
            start.ArgumentList.Add(Rz090528CpuPmObservationProbe.LifetimeApproval);
            if (profile == CpuPmObservationProfile.RangeSingleSample) { start.ArgumentList.Add(Rz090528CpuPmObservationProbe.RangeApproval); }
            start.ArgumentList.Add(Rz090528CpuPmObservationProbe.RunIdFlag); start.ArgumentList.Add(run.ToString("N"));
            actualChild = Process.Start(start) ?? throw new IOException("Child launch result unavailable.");
            // Root keeps the actual returned Process even if duplicate/bind fails.
            child = publication.PublishActuallyLaunchedChild(actualChild, retained =>
            {
                if (!lifecycle!.BindStartedChild(retained, deadline.Token)) { throw new IOException("Child binding denied."); }
                // Async accept is armed before child.json permits the child loader
                // to proceed. The callback must return before publication occurs.
                serving = listener!.ServeOnceAsync(retained.Identity, new BoundProof(bootstrap!, lifecycle), stopListener.Token);
                if (serving.IsCompleted) { throw new IOException("Authority listener ended before publication."); }
            });
            Log("ChildBound");
            bool cleaned = CpuPmSupervisorCompletion.Finish(child,
                () => Volatile.Read(ref uncertain) != 0 || deadline.IsCancellationRequested,
                bootstrap.AssertHeld, lifecycle.RevokeAuthority,
                () => { stopListener.Cancel(); listener.DisposeAsync().AsTask().GetAwaiter().GetResult(); _ = serving!.GetAwaiter().GetResult(); },
                () => lifecycle.Cleanup(deadline.Token),
                () => { if (serving!.IsCompleted) { lifecycle.RevokeAuthority(); } Thread.Sleep(100); });
            if (!cleaned) { return PreserveUnknown("TerminalOrCleanupUnavailable"); }
            int exit = actualChild.ExitCode;
            if (!publication.TryReleaseAfterActualTerminal()) { return PreserveUnknown("ChildPinReleaseUnavailable"); }
            watchdog.Dispose(); watchdog = null;
            Log(exit == 0 ? "CompletedAndAbsent" : "ChildFailedButOwnedDriverAbsent");
            return exit == 0 ? 0 : 1;
        }
        catch (Exception)
        {
            if (driverPossible || launchPossible) { return PreserveUnknown("SupervisorFailureAfterPossibleMutation"); }
            Log("UnavailableBeforeDriverMutation"); return 3;
        }
        finally { GC.KeepAlive(processRoot); }
    }
    private void MarkUnknown(string stage)
    {
        if (Interlocked.Exchange(ref uncertain, 1) != 0) { return; }
        try { lifecycle?.MarkUncertain(); } catch (Exception) { }
        try { deadline.Cancel(); } catch (Exception) { }
        try { stopListener.Cancel(); } catch (Exception) { }
        Log(stage);
    }
    private int PreserveUnknown(string stage)
    {
        MarkUnknown(stage);
        if (actualChild is not null)
        {
            try
            {
                using var observed = WindowsCpuPmProcessProbe.FromLaunchedHandle(actualChild.SafeHandle);
                while (observed.Read(false).State != CpuPmChildState.Terminal) { Thread.Sleep(250); }
                Log("SameChildTerminalAfterUncertaintyResourcesRetained");
            }
            catch (Exception) { Log("SameChildTerminalUnconfirmedResourcesRetained"); }
        }
        // Unknown completion is deliberately nonterminal. All original process,
        // service, callback and file handles remain rooted. No kill, restart,
        // replacement invocation, stop/delete or automatic retry follows.
        using var parked = new ManualResetEventSlim(false); parked.Wait(); return 4;
    }
    private void Log(string stage)
    {
        // A diagnostic sink failure must not tear down a process whose native
        // call/child is unresolved, including from the watchdog callback.
        try { lock (logGate) { output.WriteLine("PmSupervisor=" + stage); output.Flush(); } }
        catch (Exception) { }
    }
    private sealed class BoundProof(CpuPmSupervisorBootstrap bootstrap, CpuPmOwnedDriverLifecycle owner) : ICpuPmOwnedDriverProofSource
    {
        public CpuPmOwnedDriverProof? ReadFresh(Guid runId, Guid lifetimeId)
        {
            bootstrap.AssertHeld(); var result = owner.ReadFresh(runId, lifetimeId); bootstrap.AssertHeld(); return result;
        }
    }
}
