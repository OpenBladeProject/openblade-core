using System.Security.Cryptography;
using OpenBlade.Deployment;

namespace OpenBlade.Setup;

// Typed package-selection wrapper for the existing Setup command runner. Product
// construction uses same-held authenticated package selection and the native
// immutable layout. Readiness does not grant firmware authority.
internal sealed class SetupNativePackageTransaction : ISetupPayloadTransaction
{
    private readonly ISetupPayloadTransaction payload;
    private readonly WindowsNativeEpochFiles files;
    private readonly WindowsNativePackage package;
    private readonly Guid request;
    private WindowsNativeEpochTransaction? epoch;
    private SetupCandidateLaunch? launch;
    private ISetupRetainedCandidateLaunch? retained;
    private SetupCandidateServiceVerification? verified;
    private bool priorRetirementPossible;
    private bool preparationAttempted, possiblePayloadActivation, possibleNativeActivation, disposed;
    private readonly Action<NativeEpochBoundary>? preparationBoundary;
    private readonly SetupNativeReadinessAdapters readiness;
    private WindowsManagedMailboxTransfer? managedTransfer;
    private readonly ManagedMailboxStorage? mailboxStorage;

    internal SetupNativePackageTransaction(ISetupPayloadTransaction payload, WindowsNativeEpochFiles files,
        WindowsNativePackage package, Guid request, Action<NativeEpochBoundary>? preparationBoundary = null, SetupNativeReadinessAdapters? readiness = null,
        WindowsManagedMailboxTransfer? managedTransfer = null, ManagedMailboxStorage? mailboxStorage = null)
    {
        this.payload = payload;
        this.files = files;
        this.package = package;
        this.request = request;
        this.preparationBoundary = preparationBoundary;
        this.readiness = readiness ?? SetupNativeReadinessAdapters.Windows;
        this.managedTransfer = managedTransfer;
        this.mailboxStorage = mailboxStorage;
    }

    public string StagedPayloadDirectory => payload.StagedPayloadDirectory;
    public bool HadExistingInstall => payload.HadExistingInstall;
    public bool IsActivated => possiblePayloadActivation || payload.IsActivated;
    internal bool RequiresExplicitNativeRecovery => possibleNativeActivation || priorRetirementPossible;
    internal void ObservePredecessorTransition(SetupServiceTransition transition)
    {
        priorRetirementPossible |= transition is SetupServiceTransition.NativeRetirementPossible or SetupServiceTransition.NativeRetired;
    }
    public string? CommitWarning => payload.CommitWarning;
    internal string ServicePath => (epoch ?? throw new InvalidOperationException()).ServicePath;
    internal string AgentPath => (epoch ?? throw new InvalidOperationException()).AgentPath;

    internal string SelectedServicePath() { (epoch ?? throw new InvalidOperationException()).RequireProvisional(); return ServicePath; }
    internal string SelectedAgentPath() { (epoch ?? throw new InvalidOperationException()).RequireProvisional(); return AgentPath; }

    internal void BindManagedHandoff(WindowsManagedMailboxTransfer transfer)
    {
        if (managedTransfer is not null || preparationAttempted || disposed || !package.Service.RequiresMailboxHandoff)
        { throw new IOException("Managed handoff may bind only once before native epoch activation."); }
        managedTransfer = transfer;
    }

    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        if (preparationAttempted || disposed) { throw new InvalidOperationException("Native preparation/activation is never replayed."); }
        preparationAttempted = true;
        cancellationToken.ThrowIfCancellationRequested();
        // The actual held package producer, immutable images and provisional
        // selection precede StartService/health in SetupCommandRunner.
        epoch = WindowsNativeEpochTransaction.Begin(files, package, request, testCrashBoundary: preparationBoundary,
            managedTransfer: managedTransfer, storage: mailboxStorage);
        cancellationToken.ThrowIfCancellationRequested();
        // From entry into payload activation onward its outcome may be ambiguous.
        possiblePayloadActivation = true;
        await payload.ActivateAsync(cancellationToken);
    }

    // Marker publication may rename before readback fails. Fence rollback before
    // publication, after provisional validation; earlier configuration stays safe.
    internal void MarkNativeStartupPossible()
    {
        (epoch ?? throw new InvalidOperationException()).RequireProvisional();
        possibleNativeActivation = true;
        epoch.MarkStartupSubmitted();
    }

    // The next integrated origin owner must supply the original retained launch,
    // not reopen a PID. This method performs no launch and adopts no newer epoch.
    internal void BindIncomingLaunch(SetupCandidateLaunch incoming, ISetupRetainedCandidateLaunch original)
    {
        possibleNativeActivation = true;
        var current = epoch ?? throw new InvalidOperationException("Native selection is not provisional.");
        current.RequireProvisional();
        if (launch is not null || incoming.ExecutablePath != current.ServicePath || incoming.ExecutableSha256 != package.Service.Sha256)
        { throw new InvalidDataException("Native readiness belongs to a different immutable artifact/epoch."); }
        launch = incoming;
        retained = original;
    }

    internal async Task VerifyReadinessAsync(CancellationToken cancellationToken)
    {
        var current = epoch ?? throw new InvalidOperationException();
        current.RequireProvisional();
        if (launch is null)
        {
            var service = await readiness.ReadService(cancellationToken);
            var captured = readiness.CaptureOriginal(service, current.ServicePath, package.Service.Sha256);
            try { BindIncomingLaunch(captured.Launch, captured.Retained); }
            catch { (captured.Retained as IDisposable)?.Dispose(); throw; }
        }
        var expected = launch ?? throw new InvalidDataException("Native original launch binding is unavailable.");
        var original = retained ?? throw new InvalidDataException("Native original launch is unavailable.");
        var candidate = new SetupCandidateServiceVerification(expected, original, readiness.ReadService);
        await readiness.Verify(candidate, cancellationToken);
        current.RequireProvisional();
        verified = candidate;
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        var current = epoch ?? throw new InvalidOperationException();
        var readiness = verified ?? throw new InvalidDataException("Native commit requires bound typed readiness.");
        current.RequireProvisional();
        await readiness.VerifyLaunchAsync(cancellationToken);
        await payload.CommitAsync(cancellationToken);
        await readiness.VerifyLaunchAsync(cancellationToken);
        current.RequireProvisional();
        var incoming = launch!;
        byte[] bound = NativeCanonical.Lines("OpenBlade.NativeReadiness.v2", Convert.ToHexStringLower(current.ProvisionalCommitment),
            incoming.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            incoming.CreationFileTimeUtc.ToString(System.Globalization.CultureInfo.InvariantCulture), NativeStartupOwner.EncodePath(incoming.ExecutablePath), incoming.ExecutableSha256);
        current.CommitAfterBoundReadiness(Convert.ToHexStringLower(SHA256.HashData(bound)));
    }

    internal void RecordUnresolvedFailure() => epoch?.MarkUnresolved();

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (!possibleNativeActivation) { epoch?.MarkUnresolved(); await payload.RollbackAsync(cancellationToken); return; }
        // Startup may have submitted work. Package state cannot certify native
        // drain; preserve unresolved state and require explicit typed recovery.
        epoch?.MarkUnresolved();
        throw new IOException("Native activation requires explicit origin/retirement recovery; automatic fallback is denied.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) { return; }
        disposed = true;
        List<Exception> failures = [];
        try
        {
            // Managed cleanup is safe until activation could have submitted work.
            if (!possibleNativeActivation || epoch?.Record.Phase == NativeEpochPhase.Committed)
            { await payload.DisposeAsync(); }
        }
        catch (Exception failure) { failures.Add(failure); }
        foreach (var resource in new IDisposable?[] { retained as IDisposable, epoch, package, managedTransfer, files })
        {
            try { resource?.Dispose(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count != 0) { throw new AggregateException("Native package disposal failed.", failures); }
    }
}

// Injectable native operations for offline rehearsals. The product singleton
// always queries fixed SCM, retains the original process and performs real typed IPC.
internal sealed record SetupNativeReadinessAdapters(
    Func<CancellationToken, Task<SetupCandidateServiceObservation>> ReadService,
    Func<SetupCandidateServiceObservation, string, string, (SetupCandidateLaunch Launch, ISetupRetainedCandidateLaunch Retained)> CaptureOriginal,
    Func<SetupCandidateServiceVerification, CancellationToken, Task> Verify)
{
    internal static SetupNativeReadinessAdapters Windows { get; } = new(
        WindowsSetupCandidateService.ReadAsync,
        (service, path, digest) => { var original = WindowsSetupRetainedNativeLaunch.Capture(service, path, digest); return (original.Launch, original); },
        (candidate, token) => SetupServiceHealthProbe.CreateCandidate().VerifyAsync(token, timing: null, candidate));
}
