using System.Security.Cryptography;
using OpenBlade.Deployment;

namespace OpenBlade.Setup;

// Typed package-selection wrapper for the existing Setup command runner. Product
// construction awaits the integrated immutable-layout/loaded-origin launch owner;
// no command-line implementation toggle, fabricated launch, or firmware authority.
internal sealed class SetupNativePackageTransaction : ISetupPayloadTransaction
{
    private readonly SetupPayloadTransaction payload;
    private readonly WindowsNativeEpochFiles files;
    private readonly WindowsNativePackage package;
    private readonly Guid request;
    private WindowsNativeEpochTransaction? epoch;
    private SetupCandidateLaunch? launch;
    private ISetupRetainedCandidateLaunch? retained;
    private SetupCandidateServiceVerification? verified;
    private bool attemptedActivation;

    internal SetupNativePackageTransaction(SetupPayloadTransaction payload, WindowsNativeEpochFiles files,
        WindowsNativePackage package, Guid request)
    {
        this.payload = payload;
        this.files = files;
        this.package = package;
        this.request = request;
    }

    public string StagedPayloadDirectory => payload.StagedPayloadDirectory;
    public bool HadExistingInstall => payload.HadExistingInstall;
    public bool IsActivated => attemptedActivation || payload.IsActivated;
    public string? CommitWarning => payload.CommitWarning;
    internal string ServicePath => (epoch ?? throw new InvalidOperationException()).ServicePath;
    internal string AgentPath => (epoch ?? throw new InvalidOperationException()).AgentPath;

    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (attemptedActivation) { throw new InvalidOperationException("Native activation is never replayed."); }
        attemptedActivation = true;
        // The actual held package producer, immutable images and provisional
        // selection precede StartService/health in SetupCommandRunner.
        epoch = WindowsNativeEpochTransaction.Begin(files, package, request);
        await payload.ActivateAsync(cancellationToken);
    }

    // The next integrated origin owner must supply the original retained launch,
    // not reopen a PID. This method performs no launch and adopts no newer epoch.
    internal void BindIncomingLaunch(SetupCandidateLaunch incoming, ISetupRetainedCandidateLaunch original)
    {
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
        var expected = launch ?? throw new InvalidDataException("Native loaded-origin launch binding is unavailable.");
        var original = retained ?? throw new InvalidDataException("Native original launch is unavailable.");
        var candidate = new SetupCandidateServiceVerification(expected, original, WindowsSetupCandidateService.ReadAsync);
        await SetupServiceHealthProbe.CreateCandidate().VerifyAsync(cancellationToken, timing: null, candidate);
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
        byte[] bound = NativeCanonical.Lines("OpenBlade.NativeReadiness.v1", Convert.ToHexStringLower(current.ProvisionalCommitment),
            incoming.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            incoming.CreationFileTimeUtc.ToString(System.Globalization.CultureInfo.InvariantCulture), incoming.ExecutablePath, incoming.ExecutableSha256);
        current.CommitAfterBoundReadiness(Convert.ToHexStringLower(SHA256.HashData(bound)));
    }

    internal void RecordUnresolvedFailure() => epoch?.MarkUnresolved();

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (!attemptedActivation) { await payload.RollbackAsync(cancellationToken); return; }
        // Startup may have submitted work. Package state cannot certify native
        // drain; preserve unresolved state and require explicit typed recovery.
        epoch?.MarkUnresolved();
        throw new IOException("Native activation requires explicit origin/retirement recovery; automatic fallback is denied.");
    }

    public async ValueTask DisposeAsync()
    {
        epoch?.Dispose();
        package.Dispose();
        files.Dispose();
        // Avoid the managed payload's automatic active rollback after a native
        // activation. That recovery must be owned by the integrated origin stage.
        if (!attemptedActivation || epoch?.Record.Phase == NativeEpochPhase.Committed) { await payload.DisposeAsync(); }
    }
}
