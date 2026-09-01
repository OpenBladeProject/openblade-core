using System.IO.Compression;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.FirmwareSmoke;

internal sealed record ThermalExternalDriftValidationBuildManifest(
    int SchemaVersion,
    Guid ValidationRunId,
    string SourceRevision,
    string ServiceAssemblySha256,
    string ProtocolAssemblySha256,
    string ProductionFixtureSha256)
{
    internal bool IsWellFormed =>
        SchemaVersion == 1
        && ValidationRunId != Guid.Empty
        && IsHex(SourceRevision, 40)
        && IsHex(ServiceAssemblySha256, 64)
        && IsHex(ProtocolAssemblySha256, 64)
        && IsHex(ProductionFixtureSha256, 64);

    private static bool IsHex(string? value, int length) =>
        value is not null
        && value.Length == length
        && value.All(Uri.IsHexDigit);
}

internal sealed record ThermalExternalDriftValidationResult(
    bool Success,
    bool InjectionAttempted,
    bool InjectionConfirmed,
    bool SameServiceProcess,
    bool MaintainAttemptObserved,
    bool ConvergenceConfirmed,
    bool NoReplayObserved,
    bool RecoveryAttempted,
    bool FinalRestorationConfirmed,
    bool ManualRecoveryRequired,
    Guid ValidationRunId,
    string SourceRevision,
    string ServiceAssemblySha256,
    string ProtocolAssemblySha256,
    string ProductionFixtureSha256,
    HyperBoostLifecycleServiceProcess? ServiceProcess,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    PerformancePreset? BaselinePerformance,
    PerformancePreset? InjectedPerformance,
    PerformancePreset? FinalPerformance,
    FanMode? FinalFans,
    int MaintainAttemptCount,
    string Message);

internal sealed class ThermalExternalDriftValidationRunner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ConvergenceTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NoReplayWindow = TimeSpan.FromSeconds(20);

    private readonly IFirmwareSmokeClient client;
    private readonly Func<CancellationToken, Task<ServiceStatusSnapshot>> getStatus;
    private readonly Func<HyperBoostLifecycleServiceProcess?> captureServiceProcess;
    private readonly Func<CancellationToken,
        Task<IReadOnlyList<HyperBoostLifecycleDiagnosticResult>>> readDiagnostics;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeProvider timeProvider;

    internal ThermalExternalDriftValidationRunner(IFirmwareSmokeClient client)
        : this(
            client,
            client.GetStatusAsync,
            HyperBoostLifecycleValidationRunner.CaptureInstalledServiceProcess,
            token => ReadDiagnosticsAsync(client, token),
            static (duration, token) => Task.Delay(duration, token),
            TimeProvider.System)
    {
    }

    internal ThermalExternalDriftValidationRunner(
        IFirmwareSmokeClient client,
        Func<CancellationToken, Task<ServiceStatusSnapshot>> getStatus,
        Func<HyperBoostLifecycleServiceProcess?> captureServiceProcess,
        Func<CancellationToken,
            Task<IReadOnlyList<HyperBoostLifecycleDiagnosticResult>>> readDiagnostics,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeProvider timeProvider)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.getStatus = getStatus ?? throw new ArgumentNullException(nameof(getStatus));
        this.captureServiceProcess = captureServiceProcess
            ?? throw new ArgumentNullException(nameof(captureServiceProcess));
        this.readDiagnostics = readDiagnostics
            ?? throw new ArgumentNullException(nameof(readDiagnostics));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    internal async Task<ThermalExternalDriftValidationResult> RunAsync(
        ThermalExternalDriftValidationBuildManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.IsWellFormed)
        {
            throw new InvalidDataException(
                "The thermal external-drift validation-build manifest is malformed.");
        }

        DateTimeOffset startedAt = timeProvider.GetUtcNow();
        ServiceStatusSnapshot baseline = await getStatus(cancellationToken)
            .ConfigureAwait(false);
        HyperBoostLifecycleServiceProcess? serviceProcess = captureServiceProcess();
        string? baselineBlock = BaselineBlockReason(baseline, serviceProcess);
        if (baselineBlock is not null)
        {
            return Result(
                success: false,
                injectionAttempted: false,
                injectionConfirmed: false,
                sameServiceProcess: serviceProcess is not null,
                maintainAttemptObserved: false,
                convergenceConfirmed: false,
                noReplayObserved: false,
                recoveryAttempted: false,
                finalRestorationConfirmed: BaselineRestored(baseline, baseline),
                manualRecoveryRequired: false,
                manifest,
                serviceProcess,
                startedAt,
                baseline,
                injectedPerformance: null,
                final: baseline,
                maintainAttemptCount: 0,
                baselineBlock);
        }

        PerformancePreset candidate = baseline.DesiredState.Performance.Value ==
            PerformancePreset.Balanced
                ? PerformancePreset.Silent
                : PerformancePreset.Balanced;
        ControlOperationResult injection = await client
            .ValidateThermalExternalDriftAsync(
                manifest.ValidationRunId,
                manifest.SourceRevision,
                manifest.ServiceAssemblySha256,
                manifest.ProtocolAssemblySha256,
                manifest.ProductionFixtureSha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (injection.Status != ControlOperationStatus.Applied)
        {
            ServiceStatusSnapshot inspected = await getStatus(cancellationToken)
                .ConfigureAwait(false);
            bool sameProcess = serviceProcess is not null
                && captureServiceProcess() == serviceProcess;
            return Result(
                success: false,
                injectionAttempted: true,
                injectionConfirmed: false,
                sameProcess,
                maintainAttemptObserved: false,
                convergenceConfirmed: false,
                noReplayObserved: false,
                recoveryAttempted: false,
                finalRestorationConfirmed: BaselineRestored(baseline, inspected),
                manualRecoveryRequired: inspected.UnconfirmedWrites.Count > 0,
                manifest,
                serviceProcess,
                startedAt,
                baseline,
                candidate,
                inspected,
                maintainAttemptCount: 0,
                injection.Message);
        }

        ServiceStatusSnapshot final = baseline;
        bool sameServiceProcess = true;
        bool convergenceConfirmed = false;
        long convergenceDeadline = timeProvider.GetTimestamp()
            + ToTimestampDelta(ConvergenceTimeout);
        while (timeProvider.GetTimestamp() < convergenceDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (serviceProcess is null || captureServiceProcess() != serviceProcess)
            {
                sameServiceProcess = false;
                break;
            }

            final = await getStatus(cancellationToken).ConfigureAwait(false);
            if (BaselineRestored(baseline, final)
                && final.ThermalConvergence is
                {
                    State: ThermalConvergenceState.Matched,
                    Reason: ThermalConvergenceReason.None,
                }
                && final.ExternalChanges.Count == 0
                && final.UnconfirmedWrites.Count == 0)
            {
                convergenceConfirmed = true;
                break;
            }
            await delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<HyperBoostLifecycleDiagnosticResult> diagnostics =
            await readDiagnostics(cancellationToken).ConfigureAwait(false);
        int maintainAttemptCount = diagnostics.Count(result =>
            result.Timestamp >= startedAt
            && result.Detail?.StartsWith(
                "Maintain thermal convergence attempt ",
                StringComparison.Ordinal) == true);
        bool injectionDiagnostic = diagnostics.Any(result =>
            result.Timestamp >= startedAt
            && result.TransitionId == manifest.ValidationRunId
            && result.Detail?.Contains(
                "confirmed one Service-owned synthetic performance mutation",
                StringComparison.Ordinal) == true);
        bool maintainAttemptObserved = maintainAttemptCount == 1;

        bool noReplayObserved = false;
        if (sameServiceProcess
            && convergenceConfirmed
            && injectionDiagnostic
            && maintainAttemptObserved)
        {
            long noReplayDeadline = timeProvider.GetTimestamp()
                + ToTimestampDelta(NoReplayWindow);
            noReplayObserved = true;
            while (timeProvider.GetTimestamp() < noReplayDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (captureServiceProcess() != serviceProcess)
                {
                    sameServiceProcess = false;
                    noReplayObserved = false;
                    break;
                }
                final = await getStatus(cancellationToken).ConfigureAwait(false);
                if (!BaselineRestored(baseline, final)
                    || final.ExternalChanges.Count > 0
                    || final.UnconfirmedWrites.Count > 0
                    || final.ThermalConvergence is not
                    {
                        State: ThermalConvergenceState.Matched,
                        Reason: ThermalConvergenceReason.None,
                    })
                {
                    noReplayObserved = false;
                    break;
                }
                await delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            if (noReplayObserved)
            {
                diagnostics = await readDiagnostics(cancellationToken)
                    .ConfigureAwait(false);
                maintainAttemptCount = diagnostics.Count(result =>
                    result.Timestamp >= startedAt
                    && result.Detail?.StartsWith(
                        "Maintain thermal convergence attempt ",
                        StringComparison.Ordinal) == true);
                noReplayObserved = maintainAttemptCount == 1;
            }
        }

        bool recoveryAttempted = false;
        if (!convergenceConfirmed
            && sameServiceProcess
            && CanUseExplicitRecovery(baseline, candidate, final))
        {
            recoveryAttempted = true;
            _ = await client.ReapplyChangedSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            final = await getStatus(cancellationToken).ConfigureAwait(false);
        }

        bool restored = BaselineRestored(baseline, final);
        bool success = sameServiceProcess
            && injectionDiagnostic
            && convergenceConfirmed
            && maintainAttemptObserved
            && noReplayObserved
            && restored
            && !recoveryAttempted;
        string message = success
            ? $"Confirmed genuine background performance drift {baseline.DesiredState.Performance.Value} -> {candidate} -> {baseline.DesiredState.Performance.Value} in one unchanged Service process with one Maintain attempt and no replay."
            : !sameServiceProcess
                ? "The Service process changed; this run cannot distinguish background convergence from startup reconciliation."
                : recoveryAttempted && restored
                    ? "Background convergence did not complete in time. The ordinary explicit Reapply path restored the confirmed baseline, so the validation failed without leaving drift."
                    : restored
                        ? "The baseline is restored, but exact background convergence evidence is incomplete."
                        : "The validation stopped without confirmed restoration. Do not issue another raw write; inspect read-only state and recover through the ordinary explicit Service path only when freshly admitted.";
        return Result(
            success,
            injectionAttempted: true,
            injectionConfirmed: injectionDiagnostic,
            sameServiceProcess,
            maintainAttemptObserved,
            convergenceConfirmed,
            noReplayObserved,
            recoveryAttempted,
            finalRestorationConfirmed: restored,
            manualRecoveryRequired: !restored,
            manifest,
            serviceProcess,
            startedAt,
            baseline,
            candidate,
            final,
            maintainAttemptCount,
            message);
    }

    private static string? BaselineBlockReason(
        ServiceStatusSnapshot status,
        HyperBoostLifecycleServiceProcess? serviceProcess)
    {
        if (serviceProcess is null)
        {
            return "Exactly one stable OpenBlade Service process is required.";
        }
        if (!status.DeviceVerified
            || status.WritesBlocked
            || status.Firmware?.ModelNumber != "RZ09-0581")
        {
            return "The exact writable RZ09-0581 Service state is not admitted.";
        }
        if (status.ActivePowerSource != PowerSource.PluggedIn
            || status.PotentialControllers.Count > 0
            || status.ControllerDetectionWarning is not null)
        {
            return "Confirmed full power and fresh competing-controller absence are required.";
        }
        if (status.ExternalChanges.Count > 0
            || status.UnconfirmedWrites.Count > 0
            || status.HyperBoostRestorePending
            || status.ConfirmedState?.HyperBoostActive == true)
        {
            return "External, unconfirmed, and HyperBoost state must be settled before validation.";
        }
        if (status.ThermalConvergence is not
            {
                Policy: ThermalControlPolicy.MaintainDesiredState,
                State: ThermalConvergenceState.Matched,
                Reason: ThermalConvergenceReason.None,
            })
        {
            return "Maintain desired state must be selected and freshly matched.";
        }
        FanMode desiredFans = status.DesiredState.FanControl?.Mode
            ?? status.DesiredState.Fans.Value;
        return status.DesiredState.Performance.Value is not (
                PerformancePreset.Balanced or PerformancePreset.Silent)
            || desiredFans != FanMode.Automatic
            || status.ConfirmedState is not
            {
                Performance: PerformancePreset.Balanced or PerformancePreset.Silent,
                Fans: FanMode.Automatic,
                HyperBoostActive: false,
            }
            || status.ConfirmedState.Performance
                != status.DesiredState.Performance.Value
                ? "Validation requires a confirmed matching Balanced or Silent baseline with Automatic fans."
                : null;
    }

    private static bool BaselineRestored(
        ServiceStatusSnapshot baseline,
        ServiceStatusSnapshot current) =>
        current.ConfirmedState is
        {
            Performance: { } performance,
            Fans: FanMode.Automatic,
            HyperBoostActive: false,
        }
        && performance == baseline.DesiredState.Performance.Value;

    private static bool CanUseExplicitRecovery(
        ServiceStatusSnapshot baseline,
        PerformancePreset candidate,
        ServiceStatusSnapshot current) =>
        current.DeviceVerified
        && !current.WritesBlocked
        && current.ActivePowerSource == PowerSource.PluggedIn
        && current.PotentialControllers.Count == 0
        && current.ControllerDetectionWarning is null
        && current.UnconfirmedWrites.Count == 0
        && current.ExternalChanges.Contains(DeviceSubsystem.Performance)
        && current.ConfirmedState is
        {
            Performance: var observed,
            Fans: FanMode.Automatic,
            HyperBoostActive: false,
        }
        && observed == candidate
        && baseline.DesiredState.Performance.Value is
            PerformancePreset.Balanced or PerformancePreset.Silent;

    private long ToTimestampDelta(TimeSpan duration) =>
        (long)(duration.TotalSeconds * timeProvider.TimestampFrequency);

    private ThermalExternalDriftValidationResult Result(
        bool success,
        bool injectionAttempted,
        bool injectionConfirmed,
        bool sameServiceProcess,
        bool maintainAttemptObserved,
        bool convergenceConfirmed,
        bool noReplayObserved,
        bool recoveryAttempted,
        bool finalRestorationConfirmed,
        bool manualRecoveryRequired,
        ThermalExternalDriftValidationBuildManifest manifest,
        HyperBoostLifecycleServiceProcess? serviceProcess,
        DateTimeOffset startedAt,
        ServiceStatusSnapshot baseline,
        PerformancePreset? injectedPerformance,
        ServiceStatusSnapshot final,
        int maintainAttemptCount,
        string message) => new(
            success,
            injectionAttempted,
            injectionConfirmed,
            sameServiceProcess,
            maintainAttemptObserved,
            convergenceConfirmed,
            noReplayObserved,
            recoveryAttempted,
            finalRestorationConfirmed,
            manualRecoveryRequired,
            manifest.ValidationRunId,
            manifest.SourceRevision,
            manifest.ServiceAssemblySha256,
            manifest.ProtocolAssemblySha256,
            manifest.ProductionFixtureSha256,
            serviceProcess,
            startedAt,
            timeProvider.GetUtcNow(),
            baseline.ConfirmedState?.Performance,
            injectedPerformance,
            final.ConfirmedState?.Performance,
            final.ConfirmedState?.Fans,
            maintainAttemptCount,
            message);

    private static async Task<IReadOnlyList<HyperBoostLifecycleDiagnosticResult>>
        ReadDiagnosticsAsync(
        IFirmwareSmokeClient client,
        CancellationToken cancellationToken)
    {
        DiagnosticArchive diagnostic = await client.ExportDiagnosticsAsync(
                liveReadingsEnabled: false,
                cancellationToken)
            .ConfigureAwait(false);
        using var stream = new MemoryStream(diagnostic.ZipBytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        ZipArchiveEntry? entry = archive.GetEntry("command-results.json");
        if (entry is null)
        {
            return [];
        }

        await using Stream content = entry.Open();
        return await JsonSerializer.DeserializeAsync<
                HyperBoostLifecycleDiagnosticResult[]>(
                content,
                FirmwareSmokeFiles.JsonOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? [];
    }
}
