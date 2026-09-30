using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

public sealed record BrokerInstalledState(
    string Version,
    string ProductVersion,
    string PayloadSha256)
{
    internal static BrokerInstalledState NotInstalled { get; } = new(
        "0.0.0",
        "not-installed",
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");

    internal bool IsNotInstalled => Equals(NotInstalled);
}

public sealed record BrokerPostInstallVerification(
    bool ExactTargetInstalled,
    bool ServiceHealthy,
    bool ExactPreviousStateRestored);

public interface IBrokerInstalledStateVerifier
{
    Task<BrokerInstalledState> ReadBeforeInstallAsync(CancellationToken cancellationToken);

    Task<BrokerPostInstallVerification> VerifyAfterInstallAsync(
        BrokerInstalledState before,
        OpenBladeVerifiedRelease expectedRelease,
        CancellationToken cancellationToken);

    Task<BrokerPostInstallVerification> VerifyCurrentStateAsync(
        BrokerInstalledState before,
        string expectedVersion,
        string expectedSourceRevision,
        CancellationToken cancellationToken);
}

public sealed record BrokerInstallerExecutionResult(
    bool ExitObserved,
    int? ExitCode,
    bool TimedOut)
{
    internal Exception? ProcessBindingFailure { get; init; }
}

public interface IBrokerInstallerProcessRunner
{
    Task<BrokerInstallerExecutionResult> RunAsync(
        ProcessStartInfo startInfo,
        Action<int> processStarted,
        CancellationToken cancellationToken);
}

public sealed class WindowsBrokerInstallerProcessRunner : IBrokerInstallerProcessRunner
{
    internal static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(30);
    private readonly Func<ProcessStartInfo, Process?> startProcess;
    private readonly TimeSpan installerTimeout;

    public WindowsBrokerInstallerProcessRunner()
        : this(Process.Start, InstallerTimeout)
    {
    }

    internal WindowsBrokerInstallerProcessRunner(
        Func<ProcessStartInfo, Process?> startProcess,
        TimeSpan installerTimeout)
    {
        this.startProcess = startProcess;
        this.installerTimeout = installerTimeout;
    }

    public async Task<BrokerInstallerExecutionResult> RunAsync(
        ProcessStartInfo startInfo,
        Action<int> processStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(processStarted);
        using Process process = startProcess(startInfo)
            ?? throw new InvalidOperationException("The verified OpenBlade installer did not start.");
        using CancellationTokenSource deadline = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(installerTimeout);
        Exception? processBindingFailure = null;
        try
        {
            processStarted(process.Id);
        }
        catch (Exception exception)
        {
            // The child already exists. Even a failed durable identity binding must
            // retain its live monitor until exit, cancellation, or the deadline.
            processBindingFailure = exception;
        }

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return new BrokerInstallerExecutionResult(
                ExitObserved: true,
                ExitCode: process.ExitCode,
                TimedOut: false)
            {
                ProcessBindingFailure = processBindingFailure,
            };
        }
        catch (OperationCanceledException)
        {
            // Burn may still be completing or rolling back. Killing it would make
            // state more ambiguous, so the broker records indeterminate and never retries.
            return new BrokerInstallerExecutionResult(
                ExitObserved: false,
                ExitCode: null,
                TimedOut: !cancellationToken.IsCancellationRequested)
            {
                ProcessBindingFailure = processBindingFailure,
            };
        }
    }
}

public enum BrokerServiceObservation
{
    Absent,
    Healthy,
    Indeterminate,
}

public interface IBrokerServiceHealthProbe
{
    Task<BrokerServiceObservation> ObserveAsync(CancellationToken cancellationToken);
}

public sealed class WindowsBrokerInstalledStateVerifier(
    IBrokerServiceHealthProbe serviceHealthProbe) : IBrokerInstalledStateVerifier
{
    private static readonly TimeSpan HealthObservationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HealthObservationInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly IReadOnlyList<string> ProtectedPayloadRelativePaths =
    [
        "OpenBlade.Service.exe",
        "OpenBlade.Service.dll",
        "OpenBlade.Protocol.dll",
        "OpenBlade.Contracts.dll",
        "OpenBlade.SessionAgent.exe",
        "OpenBlade.SessionAgent.dll",
        "OpenBlade.Session.dll",
        "OpenBlade.Tray.exe",
        "OpenBlade.Tray.dll",
        "OpenBlade.Tray.Core.dll",
    ];

    /// <summary>
    /// The protected files the documented tray-only deployment path may replace on
    /// their own while shared contracts and both IPC versions are unchanged. Those
    /// files may therefore carry a different build identity from the rest of the
    /// payload; every other combination remains a refusal.
    /// </summary>
    internal static readonly IReadOnlyList<string> TrayOnlyDeploymentPaths =
    [
        "OpenBlade.Session.dll",
        "OpenBlade.Tray.exe",
        "OpenBlade.Tray.dll",
        "OpenBlade.Tray.Core.dll",
    ];

    private readonly string installDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "OpenBlade");
    private readonly TimeSpan healthObservationTimeout = HealthObservationTimeout;
    private readonly TimeSpan healthObservationInterval = HealthObservationInterval;

    internal WindowsBrokerInstalledStateVerifier(
        IBrokerServiceHealthProbe serviceHealthProbe,
        string installDirectory,
        TimeSpan? healthObservationTimeout = null,
        TimeSpan? healthObservationInterval = null)
        : this(serviceHealthProbe)
    {
        this.installDirectory = Path.GetFullPath(installDirectory);
        this.healthObservationTimeout = healthObservationTimeout ?? HealthObservationTimeout;
        this.healthObservationInterval = healthObservationInterval ?? HealthObservationInterval;
    }

    public Task<BrokerInstalledState> ReadBeforeInstallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadInstalledState());
    }

    public async Task<BrokerPostInstallVerification> VerifyAfterInstallAsync(
        BrokerInstalledState before,
        OpenBladeVerifiedRelease expectedRelease,
        CancellationToken cancellationToken) => await VerifyCurrentStateAsync(
            before,
            expectedRelease.Manifest.Version,
            expectedRelease.Manifest.SourceRevision,
            cancellationToken).ConfigureAwait(false);

    public async Task<BrokerPostInstallVerification> VerifyCurrentStateAsync(
        BrokerInstalledState before,
        string expectedVersion,
        string expectedSourceRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceRevision);
        BrokerInstalledState? after = null;
        try
        {
            after = ReadInstalledState();
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Absence or unreadable identity is neither target success nor proven rollback.
        }

        BrokerServiceObservation serviceObservation = await ObserveServiceAsync(
            stopOnConfirmedAbsence: after?.IsNotInstalled == true,
            cancellationToken)
            .ConfigureAwait(false);
        bool serviceHealthy = serviceObservation == BrokerServiceObservation.Healthy;
        return new BrokerPostInstallVerification(
            ExactTargetInstalled: after is not null
                && !after.IsNotInstalled
                && MatchesSignedBuildIdentity(
                    after.ProductVersion,
                    expectedVersion,
                    expectedSourceRevision),
            ServiceHealthy: serviceHealthy,
            ExactPreviousStateRestored: after is not null
                && (before.IsNotInstalled
                    ? after.IsNotInstalled
                        && serviceObservation == BrokerServiceObservation.Absent
                    : !after.IsNotInstalled
                        && string.Equals(after.Version, before.Version, StringComparison.Ordinal)
                        && string.Equals(
                            after.PayloadSha256,
                            before.PayloadSha256,
                            StringComparison.Ordinal)
                        && serviceHealthy));
    }

    private BrokerInstalledState ReadInstalledState()
    {
        try
        {
            ProtectedBrokerStorage.RejectReparsePoint(installDirectory);
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            return BrokerInstalledState.NotInstalled;
        }

        using IncrementalHash payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string? serviceProductVersion = null;
        string? trayProductVersion = null;
        foreach (string relativePath in ProtectedPayloadRelativePaths)
        {
            string path = Path.Combine(installDirectory, relativePath);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "A required installed OpenBlade payload file is unavailable.",
                    path);
            }

            ProtectedBrokerStorage.RejectReparsePoint(path);
            FileVersionInfo identity = FileVersionInfo.GetVersionInfo(path);
            string fileProductVersion = identity.ProductVersion
                ?? identity.FileVersion
                ?? string.Empty;
            if (TrayOnlyDeploymentPaths.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
            {
                if (trayProductVersion is null)
                {
                    trayProductVersion = fileProductVersion;
                }
                else if (!string.Equals(
                    trayProductVersion,
                    fileProductVersion,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The installed OpenBlade tray payload does not share one build identity.");
                }
            }
            else if (serviceProductVersion is null)
            {
                serviceProductVersion = fileProductVersion;
            }
            else if (!string.Equals(
                serviceProductVersion,
                fileProductVersion,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The installed OpenBlade service payload does not share one build identity.");
            }

            byte[] name = System.Text.Encoding.UTF8.GetBytes(relativePath);
            payloadHash.AppendData(name);
            payloadHash.AppendData([0]);
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.SequentialScan);
            byte[] buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                payloadHash.AppendData(buffer, 0, read);
            }
        }

        string effectiveServiceVersion = serviceProductVersion
            ?? throw new InvalidDataException("The installed OpenBlade payload is empty.");
        return CreateInstalledState(
            effectiveServiceVersion,
            trayProductVersion ?? effectiveServiceVersion,
            Convert.ToHexString(payloadHash.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>
    /// Describes the installed payload. A tray-only deployment leaves the tray
    /// files on a newer identity than the service files, so that pair is recorded
    /// rather than refused. The payload hash stays the rollback baseline in both
    /// cases, and the mixed marker holds no '+', so
    /// <see cref="MatchesSignedBuildIdentity"/> can never report a split payload as
    /// the installed target: only a complete, coherent install can claim success.
    /// </summary>
    internal static BrokerInstalledState CreateInstalledState(
        string serviceProductVersion,
        string trayProductVersion,
        string payloadSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadSha256);
        string serviceVersion = NormalizeVersion(serviceProductVersion);
        string trayVersion = NormalizeVersion(trayProductVersion);
        if (string.Equals(
            serviceProductVersion,
            trayProductVersion,
            StringComparison.OrdinalIgnoreCase))
        {
            return new BrokerInstalledState(
                serviceVersion,
                serviceProductVersion,
                payloadSha256);
        }

        // Both members must still be canonical, exactly as one identity must be.
        string marker = "mixed:" + Convert.ToHexString(
            SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    string.Concat(serviceProductVersion, "\n", trayProductVersion))))
            .ToLowerInvariant();

        // The lower member is the conservative baseline: an upgrade has to advance
        // every component, and restoring the same bytes reproduces it exactly.
        return new BrokerInstalledState(
            Version.Parse(trayVersion) < Version.Parse(serviceVersion)
                ? trayVersion
                : serviceVersion,
            marker,
            payloadSha256);
    }

    private static string NormalizeVersion(string? value)
    {
        string candidate = (value ?? string.Empty).Split('+', 2)[0];
        // BuildIdentity.ps1 emits base-nightly.<1..65535>+<eight lowercase hex>.
        // Only installed-baseline bookkeeping uses the base version. Keep the
        // exact product identity and payload hash for target/rollback checks.
        int nightly = candidate.IndexOf("-nightly.", StringComparison.Ordinal);
        if (nightly > 0
            && Version.TryParse(candidate[..nightly], out Version? nightlyBase)
            && nightlyBase.Revision < 0
            && nightlyBase.Major <= ushort.MaxValue
            && nightlyBase.Minor <= ushort.MaxValue
            && nightlyBase.Build is >= 0 and <= ushort.MaxValue
            && string.Equals(nightlyBase.ToString(3), candidate[..nightly], StringComparison.Ordinal)
            && int.TryParse(candidate[(nightly + 9)..], NumberStyles.None, CultureInfo.InvariantCulture, out int revision)
            && revision is > 0 and <= ushort.MaxValue
            && string.Equals(revision.ToString(CultureInfo.InvariantCulture), candidate[(nightly + 9)..], StringComparison.Ordinal)
            && value is not null && value.Length == candidate.Length + 9
            && value[(candidate.Length + 1)..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return nightlyBase.ToString(3);
        }

        if (OpenBladeReleaseManifestCodec.IsCanonicalLocalDevVersion(candidate))
        {
            return candidate;
        }

        if (Version.TryParse(candidate, out Version? parsed))
        {
            if (parsed.Revision == 0)
            {
                return parsed.ToString(3);
            }

            if (parsed.Revision < 0
                && parsed.Build >= 0
                && string.Equals(parsed.ToString(3), candidate, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new InvalidDataException("The installed OpenBlade version is unavailable or noncanonical.");
    }

    internal static bool MatchesSignedBuildIdentity(
        string productVersion,
        string expectedVersion,
        string expectedSourceRevision)
    {
        string[] parts = productVersion.Split('+', 2);
        return parts.Length == 2
            && string.Equals(parts[0], expectedVersion, StringComparison.Ordinal)
            && string.Equals(parts[1], expectedSourceRevision, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<BrokerServiceObservation> ObserveServiceAsync(
        bool stopOnConfirmedAbsence,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + healthObservationTimeout;
        BrokerServiceObservation lastObservation = BrokerServiceObservation.Indeterminate;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastObservation = await serviceHealthProbe.ObserveAsync(cancellationToken)
                .ConfigureAwait(false);
            if (lastObservation == BrokerServiceObservation.Healthy
                || (stopOnConfirmedAbsence
                    && lastObservation == BrokerServiceObservation.Absent))
            {
                return lastObservation;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return lastObservation;
            }

            await Task.Delay(healthObservationInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class ApplyFixedInboxBrokerRequestHandler(
    IFixedLocalDevInboxSource inboxSource,
    IBrokerAcceptedSequenceStore acceptedSequenceStore,
    BrokerDurableRecordStore durableRecords,
    BrokerInFlightStore inFlightStore,
    IBrokerInstalledStateVerifier installedStateVerifier,
    IBrokerInstallerProcessRunner processRunner,
    BrokerPaths paths,
    TimeProvider timeProvider,
    ILogger<ApplyFixedInboxBrokerRequestHandler> logger) : IBrokerRequestHandler
{
    public async Task<BrokerResponse> HandleAsync(
        BrokerRequest request,
        BrokerEnrollment enrollment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(enrollment);
        if (request.ProtocolVersion != BrokerRequestCodec.ProtocolVersion
            || request.RequestId == Guid.Empty
            || request.Action != BrokerAction.ApplyFixedInbox)
        {
            return Reject(request.RequestId, "invalid-request-contract");
        }

        OpenBladeReleaseTrustPolicy trust = new(
            Enabled: true,
            TrustedKeys:
            [
                new OpenBladeReleaseTrustKey(
                    enrollment.DevelopmentSigningKeyId,
                    enrollment.DevelopmentSigningSubjectPublicKeyInfoBase64),
            ]);
        try
        {
            BrokerTrustPolicyValidator.Validate(new BrokerTrustConfiguration(
                enrollment.DeploymentPolicyId,
                trust));
        }
        catch (Exception exception) when (exception is
            InvalidDataException or OpenBladeReleaseVerificationException)
        {
            logger.LogWarning(exception, "A deployment request was rejected because local development trust is invalid.");
            return Reject(request.RequestId, "local-dev-trust-invalid");
        }

        if (await inFlightStore.LoadAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            return new BrokerResponse(
                BrokerRequestCodec.ProtocolVersion,
                request.RequestId,
                BrokerResponseStatus.Indeterminate,
                "unresolved-deployment");
        }

        try
        {
            await inFlightStore.CreateOnceAsync(
                request.RequestId,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return new BrokerResponse(
                BrokerRequestCodec.ProtocolVersion,
                request.RequestId,
                BrokerResponseStatus.Indeterminate,
                "unresolved-deployment");
        }

        await RecordTransitionAsync(request.RequestId, BrokerTransitionKind.Accepted, "request-accepted", cancellationToken)
            .ConfigureAwait(false);
        BrokerInstalledState before;
        StagedVerifiedLocalDevDeployment downloaded;
        bool stagingCreated = false;
        try
        {
            before = await installedStateVerifier.ReadBeforeInstallAsync(cancellationToken)
                .ConfigureAwait(false);
            inFlightStore.BindPreviousState(
                request.RequestId,
                before,
                timeProvider.GetUtcNow());
            string staging = paths.CreateRequestStagingDirectory(request.RequestId);
            stagingCreated = true;
            downloaded = await inboxSource.StageAsync(
                staging,
                trust,
                cancellationToken).ConfigureAwait(false);
            OpenBladeVerifiedRelease independentlyVerified = OpenBladeSignedReleaseVerifier.Verify(
                downloaded.ManifestBytes,
                downloaded.SignatureBytes,
                trust);
            if (!string.Equals(
                    independentlyVerified.SigningKeyId,
                    downloaded.VerifiedRelease.SigningKeyId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The local development deployment identity changed across verification boundaries.");
            }

            if (independentlyVerified.Manifest.InstallationId != enrollment.InstallationId
                || !string.Equals(
                    independentlyVerified.Manifest.SigningKeyId,
                    enrollment.DevelopmentSigningKeyId,
                    StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    "The signed local development deployment is bound to a different enrollment.");
            }

            inFlightStore.BindRelease(
                request.RequestId,
                independentlyVerified.Manifest.InstallerSha256,
                independentlyVerified.Manifest.Version,
                independentlyVerified.Manifest.SourceRevision,
                BrokerDurableRecordStore.ComputeSha256(downloaded.ManifestBytes),
                independentlyVerified.SigningKeyId,
                independentlyVerified.Manifest.Sequence,
                timeProvider.GetUtcNow());
            downloaded = downloaded with { VerifiedRelease = independentlyVerified };
            string expectedInstallerPath = Path.Combine(
                staging,
                OpenBladeReleaseManifestCodec.InstallerFileName);
            if (!string.Equals(
                    Path.GetFullPath(downloaded.InstallerPath),
                    expectedInstallerPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The local development inbox returned an unauthorized installer path.");
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException
            or ArgumentException or NotSupportedException
            or OperationCanceledException
            or OpenBladeReleaseVerificationException)
        {
            logger.LogWarning(exception, "The unattended deployment failed closed before installer execution.");
            bool cleaned = await TryCleanPreflightStateAsync(request.RequestId, stagingCreated).ConfigureAwait(false);
            await RecordTransitionAsync(
                request.RequestId,
                cleaned ? BrokerTransitionKind.RollbackVerified : BrokerTransitionKind.Ambiguous,
                cleaned ? "preflight-cleaned" : "preflight-cleanup-unresolved",
                CancellationToken.None).ConfigureAwait(false);
            if (cleaned)
            {
                inFlightStore.ClearResolved(request.RequestId);
                return Reject(request.RequestId, "preflight-rejected");
            }

            return new BrokerResponse(
                BrokerRequestCodec.ProtocolVersion,
                request.RequestId,
                BrokerResponseStatus.Indeterminate,
                "preflight-cleanup-unresolved");
        }

        await RecordTransitionAsync(
            request.RequestId,
            BrokerTransitionKind.ReleaseVerified,
            "signed-local-dev-deployment-verified",
            cancellationToken).ConfigureAwait(false);
        await RecordTransitionAsync(
            request.RequestId,
            BrokerTransitionKind.InstallerVerified,
            "installer-size-hash-verified",
            cancellationToken).ConfigureAwait(false);

        BrokerInstallerExecutionResult execution;
        try
        {
            await using (FileStream installer = new(
                downloaded.InstallerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await OpenBladeSignedReleaseVerifier.VerifyInstallerAsync(
                    installer,
                    downloaded.VerifiedRelease,
                    cancellationToken).ConfigureAwait(false);
            }

            await acceptedSequenceStore.ConsumeAsync(
                downloaded.VerifiedRelease.Manifest.Sequence,
                cancellationToken).ConfigureAwait(false);

            ProcessStartInfo startInfo = BrokerInstallerProcessPlan.Create(
                Path.GetDirectoryName(downloaded.InstallerPath)
                    ?? throw new InvalidDataException("The fixed installer staging directory is unavailable."));
            await RecordTransitionAsync(
                request.RequestId,
                BrokerTransitionKind.InstallerStarted,
                "verified-installer-starting",
                cancellationToken).ConfigureAwait(false);
            execution = await processRunner.RunAsync(
                startInfo,
                processId => inFlightStore.BindInstallerProcess(
                    request.RequestId,
                    processId,
                    timeProvider.GetUtcNow()),
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogError(exception, "The verified installer launch became indeterminate.");
            execution = new BrokerInstallerExecutionResult(false, null, false);
        }

        bool installerProcessBindingFailed = execution.ProcessBindingFailure is not null;
        if (installerProcessBindingFailed)
        {
            logger.LogCritical(
                execution.ProcessBindingFailure,
                "The running installer was monitored but its process binding became indeterminate.");
        }

        bool installerResultBindingFailed = false;
        bool installerExitWasObserved = execution.ExitObserved && execution.ExitCode.HasValue;
        if (execution.ExitObserved && execution.ExitCode.HasValue)
        {
            try
            {
                inFlightStore.BindInstallerResult(
                    request.RequestId,
                    execution.ExitCode.Value,
                    timeProvider.GetUtcNow());
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or InvalidDataException)
            {
                logger.LogCritical(
                    exception,
                    "The observed installer result could not be durably bound to its request.");
                execution = new BrokerInstallerExecutionResult(false, null, execution.TimedOut);
                installerResultBindingFailed = true;
            }
        }
        else if (execution.ExitObserved)
        {
            logger.LogCritical("The installer reported an observed exit without an exit code.");
            execution = new BrokerInstallerExecutionResult(false, null, execution.TimedOut);
            installerResultBindingFailed = true;
        }

        string installerResultDetail = execution.ExitObserved
            ? "installer-exit-observed"
            : "installer-result-unknown";
        if (installerExitWasObserved && installerResultBindingFailed)
        {
            installerResultDetail = installerProcessBindingFailed
                ? "installer-exit-observed-process-binding-unresolved"
                : "installer-exit-observed-result-binding-unresolved";
        }

        await RecordTransitionAsync(
            request.RequestId,
            execution.ExitObserved
                ? BrokerTransitionKind.InstallerExited
                : BrokerTransitionKind.Ambiguous,
            installerResultDetail,
            CancellationToken.None).ConfigureAwait(false);

        if (installerResultBindingFailed || installerProcessBindingFailed)
        {
            return new BrokerResponse(
                BrokerRequestCodec.ProtocolVersion,
                request.RequestId,
                BrokerResponseStatus.Indeterminate,
                installerProcessBindingFailed
                    ? "installer-process-unresolved"
                    : "installer-result-unresolved");
        }

        BrokerPostInstallVerification postInstall;
        try
        {
            using CancellationTokenSource verificationDeadline = new(TimeSpan.FromSeconds(30));
            postInstall = await installedStateVerifier.VerifyAfterInstallAsync(
                before,
                downloaded.VerifiedRelease,
                verificationDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or OperationCanceledException)
        {
            logger.LogError(exception, "Post-install identity or health verification was unavailable.");
            postInstall = new BrokerPostInstallVerification(false, false, false);
        }

        BrokerDeploymentDisposition disposition = BrokerDeploymentClassifier.Classify(
            execution.ExitObserved,
            execution.ExitCode,
            postInstall.ExactTargetInstalled,
            postInstall.ServiceHealthy,
            postInstall.ExactPreviousStateRestored);
        if (disposition is BrokerDeploymentDisposition.Succeeded
            or BrokerDeploymentDisposition.RolledBack)
        {
            try
            {
                await paths.DeleteRequestStagingDirectoryAsync(request.RequestId, CancellationToken.None).ConfigureAwait(false);
                stagingCreated = false;
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or InvalidDataException)
            {
                logger.LogError(
                    exception,
                    "Resolved deployment staging could not be safely removed; the request remains unresolved.");
                disposition = BrokerDeploymentDisposition.Indeterminate;
            }
        }

        BrokerTerminalReport report = new(
            SchemaVersion: 1,
            RequestId: request.RequestId,
            InstallationId: enrollment.InstallationId,
            Sequence: downloaded.VerifiedRelease.Manifest.Sequence,
            DeploymentPolicyId: enrollment.DeploymentPolicyId,
            Disposition: disposition,
            ManifestSha256: BrokerDurableRecordStore.ComputeSha256(downloaded.ManifestBytes),
            SigningKeyId: downloaded.VerifiedRelease.SigningKeyId,
            PackageSha256: downloaded.VerifiedRelease.Manifest.InstallerSha256,
            PreviousVersion: before.Version,
            TargetVersion: downloaded.VerifiedRelease.Manifest.Version,
            InstallerExitCode: execution.ExitObserved ? execution.ExitCode : null,
            ServiceHealthy: postInstall.ServiceHealthy,
            CompletedAt: timeProvider.GetUtcNow());
        try
        {
            await durableRecords.CreateTerminalReportOnceAsync(report, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogCritical(exception, "The terminal deployment report could not be persisted.");
            return new BrokerResponse(
                BrokerRequestCodec.ProtocolVersion,
                request.RequestId,
                BrokerResponseStatus.Indeterminate,
                "terminal-report-unavailable");
        }

        if (disposition is not BrokerDeploymentDisposition.Indeterminate
            && execution.ExitObserved
            && !stagingCreated)
        {
            try
            {
                inFlightStore.ClearResolved(request.RequestId);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or InvalidDataException)
            {
                logger.LogCritical(
                    exception,
                    "The reconciled deployment latch could not be cleared.");
                return new BrokerResponse(
                    BrokerRequestCodec.ProtocolVersion,
                    request.RequestId,
                    BrokerResponseStatus.Indeterminate,
                    "unresolved-deployment");
            }
        }

        return disposition switch
        {
            BrokerDeploymentDisposition.Succeeded => new BrokerResponse(
                BrokerRequestCodec.ProtocolVersion,
                request.RequestId,
                BrokerResponseStatus.Succeeded,
                "install-succeeded"),
            BrokerDeploymentDisposition.RolledBack => new BrokerResponse(
                BrokerRequestCodec.ProtocolVersion,
                request.RequestId,
                BrokerResponseStatus.RolledBack,
                "install-rolled-back"),
            _ => new BrokerResponse(
                BrokerRequestCodec.ProtocolVersion,
                request.RequestId,
                BrokerResponseStatus.Indeterminate,
                "install-indeterminate"),
        };
    }

    private async Task RecordTransitionAsync(
        Guid requestId,
        BrokerTransitionKind kind,
        string detail,
        CancellationToken cancellationToken)
    {
        await durableRecords.AppendTransitionAsync(
            new BrokerTransition(
                1,
                requestId,
                kind,
                timeProvider.GetUtcNow(),
                detail),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryCleanPreflightStateAsync(Guid requestId, bool stagingCreated)
    {
        if (!stagingCreated)
        {
            return true;
        }

        try
        {
            await paths.DeleteRequestStagingDirectoryAsync(requestId, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogError(exception, "Rejected deployment staging could not be safely removed.");
            return false;
        }
    }

    private static BrokerResponse Reject(Guid requestId, string code) => new(
        BrokerRequestCodec.ProtocolVersion,
        requestId,
        BrokerResponseStatus.Rejected,
        code);
}
