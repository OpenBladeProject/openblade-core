using OpenBlade.Deployment;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// Owns exclusion before production factory construction and through late work.
internal sealed class ServiceMailboxHandoff : IDisposable
{
    private readonly IManagedMailboxRun? run;
    private readonly ManagedMailboxStartup.Admission? startup;
    private int quarantined;
    private int completionAttempted;
    private Task? ownerRetirement;
    private Func<Task>? retainedOwnerClose;
    private Action? retainedGuardianProof;
    internal MailboxLifetime Lifetime { get; } = new();
    public ServiceMailboxHandoff()
    {
        startup = ManagedMailboxStartup.Open(ManagedMailboxNativeCustody.Acquire,
            WindowsManagedMailboxRun.AcquireLegacyExclusion,
            native =>
            {
                if (!WindowsManagedMailboxRun.IsEnrolledImage) { return null; }
                using var upgrade = OpenBlade.DeploymentBroker.BrokerNativePackageCommitmentProducer
                    .OpenManagedCurrentAsync(CancellationToken.None).GetAwaiter().GetResult();
                return WindowsManagedMailboxRun.Start(upgrade, native);
            }, WindowsManagedMailboxRun.AcquireUnenrolledExclusion);
        run = startup.Run;
    }
    internal ServiceMailboxHandoff(IManagedMailboxRun run) { this.run = run; }

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
