using System.Diagnostics;
using System.IO.Compression;
using System.Management;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.FirmwareSmoke;

internal enum HyperBoostLifecycleScenario
{
    LowPower,
    UnexpectedExit,
}

internal sealed record PadlessHyperBoostLifecycleValidationBuildManifest(
    int SchemaVersion,
    Guid ValidationRunId,
    string SourceRevision,
    string ServiceAssemblySha256,
    string AdmissionManifestSha256)
{
    internal bool IsWellFormed =>
        SchemaVersion == 1
        && ValidationRunId != Guid.Empty
        && IsHex(SourceRevision, 40)
        && IsHex(ServiceAssemblySha256, 64)
        && IsHex(AdmissionManifestSha256, 64);

    private static bool IsHex(string? value, int length) =>
        value is not null
        && value.Length == length
        && value.All(Uri.IsHexDigit);
}

internal sealed record HyperBoostLifecycleServiceProcess(
    int ProcessId,
    long StartUtcTicks);

internal sealed record HyperBoostLifecycleJournalObservation(
    bool Exists,
    bool Valid,
    string? State,
    PerformancePreset? BaseMode,
    HyperBoostLifecycleRestoreIntent? RestoreIntent,
    bool? RequiresCoolingPad,
    string? Sha256)
{
    internal static HyperBoostLifecycleJournalObservation Missing { get; } =
        new(false, true, null, null, null, null, null);
}

internal sealed record HyperBoostLifecycleRestoreIntent(
    PerformancePreset Performance,
    CustomPerformanceSettings? CustomPerformance,
    GpuClockTuningSettings? GpuClockTuning,
    FanControlSettings FanControl)
{
    internal bool IsWellFormed =>
        Performance is PerformancePreset.Silent
            or PerformancePreset.Balanced
            or PerformancePreset.Performance
            or PerformancePreset.Custom
        && (Performance == PerformancePreset.Custom
            ? CustomPerformanceSettings.IsWellFormed(CustomPerformance)
            : CustomPerformance is null)
        && (GpuClockTuning is null
            || GpuClockTuningSettings.IsWellFormed(GpuClockTuning))
        && FanControl is not null
        && Enum.IsDefined(FanControl.Mode);
}

internal sealed record HyperBoostLifecycleRecoveryBaseline(
    PerformancePreset? Performance,
    FanMode Fans,
    bool HyperBoostActive,
    CustomPerformanceSettings? CustomPerformance = null,
    GpuClockOffsetSettings? GpuClockOffsets = null)
{
    internal bool IsWellFormed =>
        Performance is null
            or PerformancePreset.Silent
            or PerformancePreset.Balanced
            or PerformancePreset.Performance
            or PerformancePreset.Custom
        && Enum.IsDefined(Fans)
        && !HyperBoostActive
        && (CustomPerformance is null
            || CustomPerformanceSettings.IsWellFormed(CustomPerformance))
        && (GpuClockOffsets is null
            || GpuClockOffsetSettings.IsWellFormed(GpuClockOffsets));
}

internal sealed record HyperBoostLifecycleMarker(
    int SchemaVersion,
    HyperBoostLifecycleScenario Scenario,
    string TargetModel,
    string InstalledGenerationSha256,
    PerformancePreset BaseMode,
    bool RequiresCoolingPad,
    DateTimeOffset ArmedAtUtc,
    HyperBoostLifecycleServiceProcess ServiceProcess,
    string OwnershipJournalSha256,
    HyperBoostLifecycleRestoreIntent RestoreIntent)
{
    internal const int CurrentSchemaVersion = 2;

    internal bool IsWellFormed =>
        SchemaVersion == CurrentSchemaVersion
        && Enum.IsDefined(Scenario)
        && !string.IsNullOrWhiteSpace(TargetModel)
        && IsSha256(InstalledGenerationSha256)
        && BaseMode == PerformancePreset.Balanced
        && RestoreIntent is { IsWellFormed: true }
        && ArmedAtUtc != default
        && ServiceProcess.ProcessId > 0
        && ServiceProcess.StartUtcTicks > 0
        && IsSha256(OwnershipJournalSha256);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'A' and <= 'F');
}

internal sealed record HyperBoostLifecycleArmResult(
    bool Success,
    bool ValidationAttempted,
    bool HyperBoostConfirmed,
    bool DurableOwnershipConfirmed,
    bool MarkerCreated,
    bool CleanupAttempted,
    bool FinalRestorationConfirmed,
    bool ManualRecoveryRequired,
    string Message,
    HyperBoostLifecycleMarker? Marker);

internal sealed record HyperBoostLifecycleVerifyResult(
    bool Success,
    bool ExactInstalledGeneration,
    bool ExpectedProcessBoundary,
    bool ExactRestoreIntentConfirmed,
    bool JournalRetired,
    int RecoveryAttemptsObserved,
    int RecoveryCompletionsObserved,
    bool StableSecondObservation,
    string Message,
    HyperBoostLifecycleMarker? Marker);

internal sealed record HyperBoostLifecycleDiagnosticResult(
    DateTimeOffset Timestamp,
    string Subsystem,
    string Action,
    string Status,
    string? Detail);

internal sealed class HyperBoostLifecycleValidationRunner
{
    internal const string OwnershipJournalFileName = "hyperboost-ownership.json";
    private const string OwnedConfirmedJournalState = "OwnedConfirmed";
    private static readonly TimeSpan ObservationDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ArmFailureCleanupTimeout = TimeSpan.FromSeconds(20);
    private const int MaximumObservationAttempts = 12;

    private readonly IFirmwareSmokeClient client;
    private readonly Func<CancellationToken, Task<FirmwareSmokeSnapshot>> captureSnapshot;
    private readonly Func<FirmwareSmokeSnapshot, FirmwareSmokeSnapshot, bool>
        validateInstalledGeneration;
    private readonly Func<HyperBoostLifecycleServiceProcess?> captureServiceProcess;
    private readonly Func<CancellationToken,
        Task<HyperBoostLifecycleJournalObservation>> observeJournal;
    private readonly Func<CancellationToken,
        Task<IReadOnlyList<HyperBoostLifecycleDiagnosticResult>>> readDiagnostics;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeProvider timeProvider;

    internal HyperBoostLifecycleValidationRunner(
        IFirmwareSmokeClient client,
        FirmwareSmokeSnapshotCollector collector)
        : this(
            client,
            collector.CaptureAsync,
            ValidateInstalledGeneration,
            CaptureInstalledServiceProcess,
            ObserveInstalledOwnershipJournalAsync,
            token => ReadDiagnosticsAsync(client, token),
            static (duration, token) => Task.Delay(duration, token),
            TimeProvider.System)
    {
    }

    internal HyperBoostLifecycleValidationRunner(
        IFirmwareSmokeClient client,
        Func<CancellationToken, Task<FirmwareSmokeSnapshot>> captureSnapshot,
        Func<FirmwareSmokeSnapshot, FirmwareSmokeSnapshot, bool>
            validateInstalledGeneration,
        Func<HyperBoostLifecycleServiceProcess?> captureServiceProcess,
        Func<CancellationToken, Task<HyperBoostLifecycleJournalObservation>>
            observeJournal,
        Func<CancellationToken,
            Task<IReadOnlyList<HyperBoostLifecycleDiagnosticResult>>> readDiagnostics,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeProvider timeProvider)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.captureSnapshot = captureSnapshot
            ?? throw new ArgumentNullException(nameof(captureSnapshot));
        this.validateInstalledGeneration = validateInstalledGeneration
            ?? throw new ArgumentNullException(nameof(validateInstalledGeneration));
        this.captureServiceProcess = captureServiceProcess
            ?? throw new ArgumentNullException(nameof(captureServiceProcess));
        this.observeJournal = observeJournal
            ?? throw new ArgumentNullException(nameof(observeJournal));
        this.readDiagnostics = readDiagnostics
            ?? throw new ArgumentNullException(nameof(readDiagnostics));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
        this.timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    internal async Task<HyperBoostLifecycleArmResult> ArmAsync(
        FirmwareSmokeSnapshot baseline,
        HyperBoostLifecycleScenario scenario,
        string markerPath,
        CancellationToken cancellationToken) => await ArmAsync(
            baseline,
            scenario,
            markerPath,
            validationBuild: null,
            cancellationToken).ConfigureAwait(false);

    internal async Task<HyperBoostLifecycleArmResult> ArmAsync(
        FirmwareSmokeSnapshot baseline,
        HyperBoostLifecycleScenario scenario,
        string markerPath,
        PadlessHyperBoostLifecycleValidationBuildManifest? validationBuild,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ValidateMarkerPath(markerPath);
        if (!Enum.IsDefined(scenario))
        {
            throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        if (File.Exists(Path.GetFullPath(markerPath)))
        {
            return ArmFailure("The private lifecycle marker already exists.");
        }

        FirmwareSmokeSnapshot current = await captureSnapshot(cancellationToken)
            .ConfigureAwait(false);
        ServiceStatusSnapshot status = await client.ObserveStateAsync(
                LiveReadingSensors.None,
                cancellationToken)
            .ConfigureAwait(false);
        HyperBoostLifecycleServiceProcess? serviceProcess = captureServiceProcess();
        bool greenInstalledGeneration = validateInstalledGeneration(
            baseline,
            current);
        if (!TryValidateArmPreflight(
                baseline,
                current,
                status,
                serviceProcess,
                greenInstalledGeneration,
                validationBuild,
                out PerformancePreset baseMode,
                out HyperBoostLifecycleRestoreIntent restoreIntent,
                out bool requiresCoolingPad,
                out bool useValidationRequest,
                out string failure))
        {
            return ArmFailure(failure);
        }

        DateTimeOffset armedAtUtc = timeProvider.GetUtcNow();
        bool applyAttempted = false;
        bool hyperBoostConfirmed = false;
        bool cleanupAttempted = false;
        bool finalRestorationConfirmed = false;
        try
        {
            applyAttempted = true;
            ControlOperationResult applied = useValidationRequest
                ? await client.ValidatePadlessHyperBoostLifecycleAsync(
                        scenario == HyperBoostLifecycleScenario.LowPower
                            ? PadlessHyperBoostLifecycleScenario.LowPower
                            : PadlessHyperBoostLifecycleScenario.UnexpectedExit,
                        validationBuild!.ValidationRunId,
                        validationBuild.SourceRevision,
                        validationBuild.ServiceAssemblySha256,
                        validationBuild.AdmissionManifestSha256,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await client.SetHyperBoostAsync(
                        enabled: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            hyperBoostConfirmed = IsAccepted(applied)
                && await WaitForStateAsync(
                        candidate => candidate.ConfirmedState is
                        {
                            HyperBoostActive: true,
                            Performance: null,
                            Fans: FanMode.Automatic,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!hyperBoostConfirmed)
            {
                ServiceStatusSnapshot inspected = await client.ObserveStateAsync(
                        LiveReadingSensors.None,
                        cancellationToken)
                    .ConfigureAwait(false);
                hyperBoostConfirmed = inspected.ConfirmedState?.HyperBoostActive == true;
                return await FailArmAndRestoreIfNeededAsync(
                        restoreIntent,
                        hyperBoostConfirmed,
                        "Typed service activation was not independently confirmed.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            HyperBoostLifecycleJournalObservation journal =
                await observeJournal(cancellationToken).ConfigureAwait(false);
            bool durableOwnership = journal is
            {
                Exists: true,
                Valid: true,
                State: OwnedConfirmedJournalState,
                BaseMode: var journalBase,
                RestoreIntent: var journalRestoreIntent,
                RequiresCoolingPad: var journalRequiresPad,
                Sha256: not null,
            } && journalBase == baseMode
                && journalRestoreIntent == restoreIntent
                && journalRequiresPad == requiresCoolingPad;
            if (!durableOwnership)
            {
                return await FailArmAndRestoreIfNeededAsync(
                        restoreIntent,
                        hyperBoostConfirmed,
                        "Durable service ownership was not confirmed after activation.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var marker = new HyperBoostLifecycleMarker(
                HyperBoostLifecycleMarker.CurrentSchemaVersion,
                scenario,
                current.TargetModel,
                InstalledGenerationSha256(current),
                baseMode,
                requiresCoolingPad,
                armedAtUtc,
                serviceProcess!,
                journal.Sha256!,
                restoreIntent);
            try
            {
                await CreateMarkerAsync(markerPath, marker, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await FailArmAndRestoreIfNeededAsync(
                        restoreIntent,
                        hyperBoostConfirmed,
                        $"The private lifecycle marker could not be created: {exception.Message}",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new HyperBoostLifecycleArmResult(
                Success: true,
                ValidationAttempted: true,
                HyperBoostConfirmed: true,
                DurableOwnershipConfirmed: true,
                MarkerCreated: true,
                CleanupAttempted: false,
                FinalRestorationConfirmed: false,
                ManualRecoveryRequired: false,
                scenario == HyperBoostLifecycleScenario.LowPower
                    ? "Service-owned HyperBoost is armed. Cross one Windows sleep/resume boundary, then run the verify phase with the same marker."
                    : "Service-owned HyperBoost is armed. Terminate only the OpenBlade Service unexpectedly, allow it to restart, then run the verify phase with the same marker.",
                marker);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (applyAttempted)
            {
                (bool active, bool restored) =
                    await InspectAndRestoreAfterArmFailureAsync(restoreIntent)
                    .ConfigureAwait(false);
                hyperBoostConfirmed |= active;
                cleanupAttempted = active;
                finalRestorationConfirmed = restored;
            }
            throw;
        }
        catch (Exception exception)
        {
            if (applyAttempted)
            {
                (bool active, bool restored) =
                    await InspectAndRestoreAfterArmFailureAsync(restoreIntent)
                    .ConfigureAwait(false);
                hyperBoostConfirmed |= active;
                cleanupAttempted = active;
                finalRestorationConfirmed = restored;
            }
            return new HyperBoostLifecycleArmResult(
                false,
                true,
                hyperBoostConfirmed,
                false,
                false,
                cleanupAttempted,
                finalRestorationConfirmed,
                applyAttempted && !finalRestorationConfirmed,
                $"Lifecycle arming failed: {exception.Message}",
                null);
        }
    }

    private async Task<(bool HyperBoostActive, bool Restored)>
        InspectAndRestoreAfterArmFailureAsync(
            HyperBoostLifecycleRestoreIntent restoreIntent)
    {
        using var timeout = new CancellationTokenSource(ArmFailureCleanupTimeout);
        try
        {
            ServiceStatusSnapshot inspected = await client.ObserveStateAsync(
                    LiveReadingSensors.None,
                    timeout.Token)
                .ConfigureAwait(false);
            bool active = inspected.ConfirmedState?.HyperBoostActive == true;
            if (!active)
            {
                return (false, IsExactRestoredState(inspected, restoreIntent));
            }

            return (true, await RestoreOnceAsync(restoreIntent, timeout.Token)
                .ConfigureAwait(false));
        }
        catch
        {
            return (false, false);
        }
    }

    internal async Task<HyperBoostLifecycleVerifyResult> VerifyAsync(
        FirmwareSmokeSnapshot baseline,
        HyperBoostLifecycleScenario scenario,
        string markerPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ValidateMarkerPath(markerPath);
        HyperBoostLifecycleMarker marker;
        try
        {
            marker = await FirmwareSmokeFiles.ReadJsonAsync<HyperBoostLifecycleMarker>(
                    markerPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return VerifyFailure($"The private lifecycle marker is unavailable: {exception.Message}");
        }

        if (!marker.IsWellFormed || marker.Scenario != scenario)
        {
            return VerifyFailure(
                "The private lifecycle marker is malformed or belongs to another scenario.",
                marker);
        }

        FirmwareSmokeSnapshot current = await captureSnapshot(cancellationToken)
            .ConfigureAwait(false);
        bool exactGeneration = validateInstalledGeneration(baseline, current)
            && string.Equals(
            InstalledGenerationSha256(current),
            marker.InstalledGenerationSha256,
            StringComparison.Ordinal);
        HyperBoostLifecycleServiceProcess? serviceProcess = captureServiceProcess();
        bool processBoundary = serviceProcess is not null
            && (scenario == HyperBoostLifecycleScenario.LowPower
                ? serviceProcess == marker.ServiceProcess
                : serviceProcess != marker.ServiceProcess
                    && !IsProcessAlive(marker.ServiceProcess));

        ServiceStatusSnapshot first = await client.ObserveStateAsync(
                LiveReadingSensors.None,
                cancellationToken)
            .ConfigureAwait(false);
        ServiceStatusSnapshot second = await client.ObserveStateAsync(
                LiveReadingSensors.None,
                cancellationToken)
            .ConfigureAwait(false);
        bool exactFinal = IsExactRestoredState(first, marker.RestoreIntent)
            && IsExactRestoredState(second, marker.RestoreIntent);
        bool stable = first.ConfirmedState?.HyperBoostActive == false
            && first.ConfirmedState?.Performance == second.ConfirmedState?.Performance
            && first.ConfirmedState?.Fans == second.ConfirmedState?.Fans;
        HyperBoostLifecycleJournalObservation journal =
            await observeJournal(cancellationToken).ConfigureAwait(false);
        bool journalRetired = !journal.Exists;

        IReadOnlyList<HyperBoostLifecycleDiagnosticResult> diagnostics =
            await readDiagnostics(cancellationToken).ConfigureAwait(false);
        string[] boundaries = scenario == HyperBoostLifecycleScenario.LowPower
            ? [
                "before low power",
                "before display-off safety boundary",
            ]
            : ["before unexpected-exit recovery"];
        int attempts = diagnostics.Count(entry =>
            entry.Timestamp >= marker.ArmedAtUtc
            && entry.Subsystem == "Service"
            && entry.Action == "Reconcile"
            && boundaries.Any(boundary =>
                entry.Detail?.Contains(
                    $"Attempting to exit OpenBlade-owned HyperBoost and restore its entry-time profile {boundary}.",
                    StringComparison.Ordinal) == true));
        int completions = diagnostics.Count(entry =>
            entry.Timestamp >= marker.ArmedAtUtc
            && entry.Subsystem == "Service"
            && entry.Action == "Reconcile"
            && boundaries.Any(boundary =>
                entry.Detail?.Contains(
                    $"OpenBlade-owned HyperBoost exited and its entry-time profile was restored {boundary}.",
                    StringComparison.Ordinal) == true));
        bool success = exactGeneration
            && processBoundary
            && exactFinal
            && stable
            && journalRetired
            && attempts == 1
            && completions == 1
            && baseline.TargetModel == current.TargetModel;
        return new HyperBoostLifecycleVerifyResult(
            success,
            exactGeneration,
            processBoundary,
            exactFinal,
            journalRetired,
            attempts,
            completions,
            stable,
            success
                ? "The installed service completed one owned HyperBoost lifecycle restoration and retired its journal."
                : "The installed lifecycle evidence is incomplete. No recovery write was issued by FirmwareSmoke.",
            marker);
    }

    private async Task<HyperBoostLifecycleArmResult> FailArmAndRestoreIfNeededAsync(
        HyperBoostLifecycleRestoreIntent restoreIntent,
        bool hyperBoostConfirmed,
        string message,
        CancellationToken cancellationToken)
    {
        bool cleanupAttempted = hyperBoostConfirmed;
        bool restored = !hyperBoostConfirmed || await RestoreOnceAsync(
                restoreIntent,
                cancellationToken)
            .ConfigureAwait(false);
        return new HyperBoostLifecycleArmResult(
            false,
            true,
            hyperBoostConfirmed,
            false,
            false,
            cleanupAttempted,
            restored,
            hyperBoostConfirmed && !restored,
            message,
            null);
    }

    private async Task<bool> RestoreOnceAsync(
        HyperBoostLifecycleRestoreIntent restoreIntent,
        CancellationToken cancellationToken)
    {
        try
        {
            ServiceStatusSnapshot current = await client.ObserveStateAsync(
                    LiveReadingSensors.None,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current.ConfirmedState?.HyperBoostActive != true)
            {
                return IsExactRestoredState(current, restoreIntent);
            }

            ControlOperationResult result = await client.SetHyperBoostAsync(
                    enabled: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return IsAccepted(result)
                && await WaitForStateAsync(
                        candidate => IsExactRestoredState(candidate, restoreIntent),
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> WaitForStateAsync(
        Func<ServiceStatusSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < MaximumObservationAttempts; attempt++)
        {
            ServiceStatusSnapshot status = await client.ObserveStateAsync(
                    LiveReadingSensors.None,
                    cancellationToken)
                .ConfigureAwait(false);
            if (predicate(status))
            {
                return true;
            }

            if (attempt + 1 < MaximumObservationAttempts)
            {
                await delay(ObservationDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private static bool TryValidateArmPreflight(
        FirmwareSmokeSnapshot baseline,
        FirmwareSmokeSnapshot current,
        ServiceStatusSnapshot status,
        HyperBoostLifecycleServiceProcess? serviceProcess,
        bool greenInstalledGeneration,
        PadlessHyperBoostLifecycleValidationBuildManifest? validationBuild,
        out PerformancePreset baseMode,
        out HyperBoostLifecycleRestoreIntent restoreIntent,
        out bool requiresCoolingPad,
        out bool useValidationRequest,
        out string failure)
    {
        baseMode = PerformancePreset.Balanced;
        restoreIntent = new HyperBoostLifecycleRestoreIntent(
            PerformancePreset.Balanced,
            CustomPerformance: null,
            GpuClockTuning: null,
            FanControlSettings.Default);
        requiresCoolingPad = false;
        useValidationRequest = false;
        failure = string.Empty;
        if (!greenInstalledGeneration
            || serviceProcess is null
            || current.Runtime.ServiceProcessCount != 1
            || baseline.TargetModel != current.TargetModel
            || !baseline.Runtime.InstalledComponents.SequenceEqual(
                current.Runtime.InstalledComponents)
            || !status.DeviceVerified
            || status.WritesBlocked
            || status.UnconfirmedWrites.Count > 0
            || status.ConfirmedState is not
            {
                HyperBoostActive: false,
                Performance: var confirmedPerformance,
                Fans: { } confirmedFans,
            }
            || status.Capabilities?.HyperBoostAccess
                != DeviceFeatureAccess.ReadWrite
            || status.Capabilities.HyperBoostBaseModes?.Balanced
                != DeviceFeatureAccess.ReadWrite
            || status.DesiredState.Performance.Value is not (PerformancePreset.Silent
                or PerformancePreset.Balanced
                or PerformancePreset.Performance
                or PerformancePreset.Custom)
            || !status.Capabilities.PerformancePresets.Contains(
                status.DesiredState.Performance.Value)
            || !status.Capabilities.FanModes.Contains(confirmedFans)
            || !IsManualFanMode(confirmedFans)
                && confirmedPerformance != status.DesiredState.Performance.Value
            || IsManualFanMode(confirmedFans)
                && confirmedPerformance is not null
                && confirmedPerformance != status.DesiredState.Performance.Value
            || status.ActivePowerSource != PowerSource.PluggedIn)
        {
            failure =
                "The exact installed generation, verified inactive full-power profile, admitted fan intent, and writable Balanced HyperBoost transition are required.";
            return false;
        }

        FanControlSettings fanControl = status.DesiredState.FanControl
            ?? FanControlSettings.FromLegacy(confirmedFans);
        restoreIntent = new HyperBoostLifecycleRestoreIntent(
            status.DesiredState.Performance.Value,
            status.DesiredState.Performance.Value == PerformancePreset.Custom
                ? status.DesiredState.CustomPerformance
                : null,
            status.DesiredState.GpuClockTuning,
            fanControl);
        if (!restoreIntent.IsWellFormed
            || status.DesiredState.Fans.Value != confirmedFans
            || fanControl.Mode != confirmedFans)
        {
            failure =
                "The confirmed performance and fan state must match one well-formed entry-time profile intent.";
            return false;
        }

        bool padAbsentAdmitted =
            (status.Capabilities.HyperBoostWithoutCoolingPadAccess
                    == DeviceFeatureAccess.ReadWrite
                || validationBuild?.IsWellFormed == true
                    && status.Capabilities.HyperBoostWithoutCoolingPadAccess
                        == DeviceFeatureAccess.ObservedOnly)
            && status.PadlessHyperBoostEligibility
                == PadlessHyperBoostEligibilityState.CoolingPadAbsent;
        if (!padAbsentAdmitted)
        {
            failure =
                "Installed-service lifecycle validation requires the admitted Balanced transition and confirmed physical pad absence.";
            return false;
        }

        requiresCoolingPad = false;
        useValidationRequest = status.Capabilities.HyperBoostWithoutCoolingPadAccess
                != DeviceFeatureAccess.ReadWrite;
        return true;
    }

    internal static bool ValidateInstalledGeneration(
        FirmwareSmokeSnapshot baseline,
        FirmwareSmokeSnapshot current)
    {
        FirmwareSmokeReport report =
            FirmwareSmokeAssessment.CompareUnchangedInstalledGeneration(
                baseline,
                current);
        if (report.Succeeded)
        {
            return true;
        }

        // Restoring an owned HyperBoost session can legitimately latch the fan
        // channel as externally changed when the restarted service first
        // observes firmware Automatic. The lifecycle verifier independently
        // requires the exact restored base/fan pair, a retired ownership
        // journal, and one matching recovery attempt/completion. Permit only
        // that expected diagnostic while retaining every generation, identity,
        // capability, and health comparison in the report.
        return current.ExternalChanges.Count == 1
            && string.Equals(
                current.ExternalChanges[0],
                DeviceSubsystem.Fans.ToString(),
                StringComparison.Ordinal)
            && report.Checks
                .Where(check => check.Outcome == FirmwareSmokeOutcome.Failed)
                .All(check => string.Equals(
                    check.Id,
                    "device.external-changes",
                    StringComparison.Ordinal));
    }

    private static bool IsExactRestoredState(
        ServiceStatusSnapshot status,
        HyperBoostLifecycleRestoreIntent restoreIntent) =>
        status.DeviceVerified
        && !status.WritesBlocked
        && status.UnconfirmedWrites.Count == 0
        && status.ConfirmedState is
        {
            HyperBoostActive: false,
            Performance: var restored,
            Fans: var restoredFans,
        }
        && (IsManualFanMode(restoreIntent.FanControl.Mode)
            ? restored is null || restored == restoreIntent.Performance
            : restored == restoreIntent.Performance)
        && restoredFans == restoreIntent.FanControl.Mode
        && (IsManualFanMode(restoreIntent.FanControl.Mode)
            || restoreIntent.Performance != PerformancePreset.Custom
            || status.ConfirmedState.CustomPerformance
                == restoreIntent.CustomPerformance)
        && status.DesiredState.Performance.Value == restoreIntent.Performance
        && status.DesiredState.Fans.Value == restoreIntent.FanControl.Mode
        && status.DesiredState.CustomPerformance == restoreIntent.CustomPerformance
        && status.DesiredState.GpuClockTuning == restoreIntent.GpuClockTuning
        && (status.DesiredState.FanControl
                ?? FanControlSettings.FromLegacy(status.DesiredState.Fans.Value))
            == restoreIntent.FanControl;

    private static bool IsManualFanMode(FanMode mode) =>
        mode is FanMode.Fixed or FanMode.ManualCurve or FanMode.Maximum;

    private static bool IsAccepted(ControlOperationResult result) =>
        result.Status is ControlOperationStatus.Applied
            or ControlOperationStatus.Stored;

    private static string InstalledGenerationSha256(FirmwareSmokeSnapshot snapshot)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                snapshot.TargetModel,
                snapshot.ServiceVersion,
                snapshot.Runtime.InstalledComponents,
            },
            FirmwareSmokeFiles.JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static HyperBoostLifecycleServiceProcess? CaptureInstalledServiceProcess()
    {
        Process[] processes = Process.GetProcessesByName("OpenBlade.Service");
        try
        {
            if (processes.Length != 1)
            {
                return null;
            }

            return CaptureProcessIdentity(processes[0].Id);
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    internal static HyperBoostLifecycleServiceProcess? CaptureProcessIdentity(
        int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, CreationDate FROM Win32_Process WHERE ProcessId = {processId}");
            using ManagementObjectCollection matches = searcher.Get();
            HyperBoostLifecycleServiceProcess? identity = null;
            foreach (ManagementObject match in matches)
            {
                using (match)
                {
                    if (identity is not null
                        || Convert.ToInt32(match["ProcessId"]) != processId
                        || match["CreationDate"] is not string creationDate)
                    {
                        return null;
                    }

                    DateTime started = ManagementDateTimeConverter.ToDateTime(
                        creationDate);
                    identity = new HyperBoostLifecycleServiceProcess(
                        processId,
                        started.ToUniversalTime().Ticks);
                }
            }

            return identity;
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsProcessAlive(HyperBoostLifecycleServiceProcess identity) =>
        CaptureProcessIdentity(identity.ProcessId) == identity;

    private static async Task<HyperBoostLifecycleJournalObservation>
        ObserveInstalledOwnershipJournalAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenBlade",
            OwnershipJournalFileName);
        if (!File.Exists(path))
        {
            return HyperBoostLifecycleJournalObservation.Missing;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
            return ParseOwnershipJournal(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new HyperBoostLifecycleJournalObservation(
                true,
                false,
                null,
                null,
                null,
                null,
                null);
        }
    }

    internal static HyperBoostLifecycleJournalObservation ParseOwnershipJournal(
        byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        int schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        int state = root.GetProperty("state").GetInt32();
        int baseMode = root.GetProperty("baseMode").GetInt32();
        DateTimeOffset updatedAtUtc = root.GetProperty("updatedAtUtc").GetDateTimeOffset();
        bool requiresPad = root.GetProperty("requiresCoolingPad").GetBoolean();
        string? stateName = state switch
        {
            0 => "EnablePending",
            1 => OwnedConfirmedJournalState,
            2 => "RestorePending",
            3 => "Relinquished",
            _ => null,
        };
        var parsedBase = (PerformancePreset)baseMode;
        HyperBoostLifecycleRestoreIntent? restoreIntent = schemaVersion switch
        {
            2 => new HyperBoostLifecycleRestoreIntent(
                parsedBase,
                CustomPerformance: null,
                GpuClockTuning: null,
                FanControlSettings.FromLegacy(FanMode.Automatic)),
            3 or 4 => new HyperBoostLifecycleRestoreIntent(
                (PerformancePreset)root.GetProperty("restorePerformance").GetInt32(),
                root.TryGetProperty("restoreCustomPerformance", out JsonElement custom)
                    && custom.ValueKind != JsonValueKind.Null
                        ? custom.Deserialize<CustomPerformanceSettings>(
                            FirmwareSmokeFiles.JsonOptions)
                        : null,
                root.TryGetProperty("restoreGpuClockTuning", out JsonElement tuning)
                    && tuning.ValueKind != JsonValueKind.Null
                        ? tuning.Deserialize<GpuClockTuningSettings>(
                            FirmwareSmokeFiles.JsonOptions)
                        : null,
                root.GetProperty("restoreFanControl")
                    .Deserialize<FanControlSettings>(FirmwareSmokeFiles.JsonOptions)!),
            _ => null,
        };
        HyperBoostLifecycleRecoveryBaseline? recoveryBaseline =
            schemaVersion == 4
            && root.TryGetProperty("recoveryBaseline", out JsonElement recovery)
            && recovery.ValueKind != JsonValueKind.Null
                ? recovery.Deserialize<HyperBoostLifecycleRecoveryBaseline>(
                    FirmwareSmokeFiles.JsonOptions)
                : null;
        bool valid = schemaVersion is 2 or 3 or 4
            && stateName is not null
            && parsedBase == PerformancePreset.Balanced
            && updatedAtUtc != default
            && restoreIntent is { IsWellFormed: true }
            && (recoveryBaseline is null
                || stateName == "RestorePending"
                    && recoveryBaseline.IsWellFormed);
        return new HyperBoostLifecycleJournalObservation(
            true,
            valid,
            stateName,
            valid ? parsedBase : null,
            valid ? restoreIntent : null,
            requiresPad,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

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
            .ConfigureAwait(false) ?? [];
    }

    private static async Task CreateMarkerAsync(
        string path,
        HyperBoostLifecycleMarker marker,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        marker,
                        FirmwareSmokeFiles.JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // The private temporary marker is never evidence by itself.
            }
        }
    }

    private static void ValidateMarkerPath(string markerPath)
    {
        if (string.IsNullOrWhiteSpace(markerPath))
        {
            throw new ArgumentException(
                "A private lifecycle marker path is required.",
                nameof(markerPath));
        }
    }

    private static HyperBoostLifecycleArmResult ArmFailure(string message) => new(
        false,
        false,
        false,
        false,
        false,
        false,
        false,
        false,
        message,
        null);

    private static HyperBoostLifecycleVerifyResult VerifyFailure(
        string message,
        HyperBoostLifecycleMarker? marker = null) => new(
        false,
        false,
        false,
        false,
        false,
        0,
        0,
        false,
        message,
        marker);
}
