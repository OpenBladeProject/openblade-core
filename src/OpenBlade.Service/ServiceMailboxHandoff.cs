using OpenBlade.Deployment;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// Owns exclusion before production factory construction and through late work.
internal sealed class ServiceMailboxHandoff : IDisposable
{
    private readonly IManagedMailboxRun? run;
    private readonly IDisposable? unenrolledExclusion;
    private int quarantined;
    private int completionAttempted;
    internal MailboxLifetime Lifetime { get; } = new();
    public ServiceMailboxHandoff()
    {
        if (WindowsManagedMailboxRun.IsEnrolledImage)
        {
            try
            {
                using var upgrade = OpenBlade.DeploymentBroker.BrokerNativePackageCommitmentProducer
                    .OpenManagedCurrentAsync(CancellationToken.None).GetAwaiter().GetResult();
                run = WindowsManagedMailboxRun.Start(upgrade);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException or OpenBladeReleaseVerificationException)
            {
                // An unauthenticated/default managed run remains operational;
                // the native migration prerequisite is explicitly unavailable.
                System.Diagnostics.Trace.TraceWarning("Managed mailbox handoff enrollment unavailable: {0}", error.Message);
            }
        }
        if (run is null) { unenrolledExclusion = WindowsManagedMailboxRun.AcquireUnenrolledExclusion(); }
    }
    internal ServiceMailboxHandoff(IManagedMailboxRun run) { this.run = run; }

    internal Task CompleteAsync(IVerifiedBladeDeviceProvider provider, IServiceControlRuntime runtime, bool intentionalStop, CancellationToken token)
    {
        if (provider is not WindowsVerifiedBladeDeviceProvider tracked || runtime is not ServiceControlRuntime actual)
        { throw new IOException("Service runtime has no tracked mailbox/guardian custody proof."); }
        if (!ReferenceEquals(tracked.MailboxLifetime, Lifetime))
        { throw new IOException("Service provider belongs to another transport lifetime scope."); }
        return CompleteCoreAsync(
            () => { tracked.Dispose(); return Task.CompletedTask; },
            () => { actual.RequireMailboxGuardianQuiescent(); tracked.RequireTransportDelegationClear(); },
            intentionalStop, token);
    }

    // Injected resource tests use this same aggregate; production callbacks above
    // are bound to the actual provider and complete guardian ownership contract.
    internal async Task CompleteCoreAsync(Func<Task> closeOwners, Action requireGuardianClear, bool intentionalStop, CancellationToken token)
    {
        if (Interlocked.Exchange(ref completionAttempted, 1) != 0)
        { throw new IOException("Service transport retirement is never replayed."); }
        Lifetime.Seal();
        try
        {
            requireGuardianClear();
            await closeOwners().WaitAsync(token).ConfigureAwait(false);
            await Lifetime.WhenTerminal.WaitAsync(token).ConfigureAwait(false);
            requireGuardianClear();
            if (!Lifetime.SuccessfullyTerminal) { throw new IOException("Service transport retirement is unresolved."); }
            if (intentionalStop) { run?.PublishQuiescent(); }
        }
        catch (Exception error) { Lifetime.Fail(error); throw; }
    }

    public void Dispose()
    {
        if (!Lifetime.SuccessfullyTerminal)
        {
            // Keep process-wide custody on timeout/fault, even when DI teardown
            // drops the provider. OS process exit cannot mint a receipt.
            if (Interlocked.Exchange(ref quarantined, 1) == 0)
            { _ = System.Runtime.InteropServices.GCHandle.Alloc(this); }
            return;
        }
        try { run?.Dispose(); } finally { unenrolledExclusion?.Dispose(); }
    }
}
