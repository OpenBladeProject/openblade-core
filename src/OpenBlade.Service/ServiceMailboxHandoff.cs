using OpenBlade.Deployment;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// Owns exclusion before production factory construction and through late work.
// Construction does no machine I/O: Open acquires custody, and until then the
// transport lifetime refuses every mailbox reservation.
internal sealed class ServiceMailboxHandoff : IDisposable
{
    private IManagedMailboxRun? run;
    private ManagedMailboxStartup.Admission? startup;
    private int openAttempted;
    private int quarantined;
    private int completionAttempted;
    private Task? ownerRetirement;
    private Func<Task>? retainedOwnerClose;
    private Action? retainedGuardianProof;
    internal MailboxLifetime Lifetime { get; } = new();
    // Fresh native custody computed this handback binding at this start; the
    // Service adopts only a record bound to it. Without an admission there is
    // no evidence of "no handback", so this refuses instead of reading empty.
    internal NativeHandbackBinding NativeHandbacks =>
        startup?.NativeHandbacks ?? throw new InvalidOperationException("Native custody admission is not open.");
    private readonly Func<ManagedMailboxStartup.Admission> admit = OpenProductAdmission;
    public ServiceMailboxHandoff()
    {
        Lifetime = new(admitted: false);
    }
    // Tests inject the admission that the hosted startup's Open acquires.
    internal ServiceMailboxHandoff(Func<ManagedMailboxStartup.Admission> admit) : this() => this.admit = admit;

    // Called once by ServiceMailboxCustodyStartup, which the host constructs before Worker.
    internal void Open()
    {
        if (Interlocked.Exchange(ref openAttempted, 1) != 0)
        { throw new IOException("Service mailbox custody is never reacquired."); }
        startup = admit();
        run = startup.Run;
        Lifetime.Admit();
    }

    private static ManagedMailboxStartup.Admission OpenProductAdmission() =>
        ManagedMailboxStartup.Open(ManagedMailboxNativeCustody.Acquire,
            WindowsManagedMailboxRun.AcquireLegacyExclusion,
            native =>
            {
                if (!WindowsManagedMailboxRun.IsEnrolledImage) { return null; }
                using var upgrade = OpenBlade.DeploymentBroker.BrokerNativePackageCommitmentProducer
                    .OpenManagedCurrentAsync(CancellationToken.None).GetAwaiter().GetResult();
                return WindowsManagedMailboxRun.Start(upgrade, native);
            }, WindowsManagedMailboxRun.AcquireUnenrolledExclusion);
    internal ServiceMailboxHandoff(IManagedMailboxRun run) { this.run = run; }
    // An admission produced by ManagedMailboxStartup.Open over injected storage.
    internal ServiceMailboxHandoff(ManagedMailboxStartup.Admission startup) { this.startup = startup; run = startup.Run; }

    internal Task CompleteAsync(IVerifiedBladeDeviceProvider provider, IServiceControlRuntime runtime, bool intentionalStop, CancellationToken token)
    {
        if (provider is not WindowsVerifiedBladeDeviceProvider tracked || runtime is not ServiceControlRuntime actual)
        { throw new IOException("Service runtime has no tracked mailbox/guardian custody proof."); }
        if (!ReferenceEquals(tracked.MailboxLifetime, Lifetime))
        { throw new IOException("Service provider belongs to another transport lifetime scope."); }
        return CompleteCoreAsync(
            tracked.RetireMailboxOwnersAsync,
            () => { actual.RequireMailboxGuardianQuiescent(); tracked.RequireTransportDelegationClear(); },
            intentionalStop, token);
    }

    // Injected resource tests use this same aggregate; production callbacks above
    // are bound to the actual provider and complete guardian ownership contract.
    internal async Task CompleteCoreAsync(Func<Task> closeOwners, Action requireGuardianClear, bool intentionalStop, CancellationToken token)
    {
        if (Interlocked.Exchange(ref completionAttempted, 1) != 0)
        { throw new IOException("Service transport retirement is never replayed."); }
        retainedOwnerClose = closeOwners;
        retainedGuardianProof = requireGuardianClear;
        try
        {
            token.ThrowIfCancellationRequested();
            retainedGuardianProof!();
            // Worker producers and guarded lifecycle cleanup have already stopped.
            // The remaining owner close may need its own admitted getter/cleanup.
            // Run synchronous Dispose off the caller so its remaining budget also
            // bounds that phase; retain the actual task through timeout or fault.
            ownerRetirement = Task.Run(async () =>
            {
                try { await retainedOwnerClose!().ConfigureAwait(false); }
                catch (Exception error) { Lifetime.Fail(error); throw; }
                finally { Lifetime.Seal(); }
            }, CancellationToken.None);
            await ownerRetirement.WaitAsync(token).ConfigureAwait(false);
            await Lifetime.WhenTerminal.WaitAsync(token).ConfigureAwait(false);
            retainedGuardianProof!();
            token.ThrowIfCancellationRequested();
            if (!Lifetime.SuccessfullyTerminal) { throw new IOException("Service transport retirement is unresolved."); }
            if (intentionalStop) { run?.PublishQuiescent(); }
        }
        catch (Exception error)
        {
            Lifetime.Fail(error);
            if (ownerRetirement is null) { Lifetime.Seal(); }
            throw;
        }
    }

    public void Dispose()
    {
        if (ownerRetirement?.IsCompletedSuccessfully != true || !Lifetime.SuccessfullyTerminal)
        {
            // Keep process-wide custody on timeout/fault, even when DI teardown
            // drops the provider. OS process exit cannot mint a receipt.
            if (Interlocked.Exchange(ref quarantined, 1) == 0)
            { _ = System.Runtime.InteropServices.GCHandle.Alloc(this); }
            return;
        }
        if (startup is not null) { startup.Dispose(); } else { run?.Dispose(); }
        retainedOwnerClose = null;
        retainedGuardianProof = null;
    }
}

// The host constructs hosted services in registration order after its lifetime
// starts, so registering this before Worker acquires custody before Worker's
// dependencies construct the production factories, at the same point in startup
// where DI resolution of the handoff used to acquire it.
internal sealed class ServiceMailboxCustodyStartup : IHostedService
{
    public ServiceMailboxCustodyStartup(ServiceMailboxHandoff handoff) => handoff.Open();
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
