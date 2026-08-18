using System.Diagnostics;
using System.IO.Compression;
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

internal sealed record HyperBoostLifecycleServiceProcess(
    int ProcessId,
    long StartUtcTicks);

internal sealed record HyperBoostLifecycleJournalObservation(
    bool Exists,
    bool Valid,
    string? State,
    PerformancePreset? BaseMode,
    bool? RequiresCoolingPad,
    string? Sha256)
{
    internal static HyperBoostLifecycleJournalObservation Missing { get; } =
        new(false, true, null, null, null, null);
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
    string OwnershipJournalSha256)
{
    internal const int CurrentSchemaVersion = 1;

    internal bool IsWellFormed =>
        SchemaVersion == CurrentSchemaVersion
        && Enum.IsDefined(Scenario)
        && !string.IsNullOrWhiteSpace(TargetModel)
        && IsSha256(InstalledGenerationSha256)
        && BaseMode is PerformancePreset.Silent
            or PerformancePreset.Balanced
            or PerformancePreset.Performance
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
    bool ExactBaseAndAutomaticConfirmed,
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
                out PerformancePreset baseMode,
                out bool requiresCoolingPad,
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
            ControlOperationResult applied = await client.SetHyperBoostAsync(
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
                        baseMode,
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
                RequiresCoolingPad: var journalRequiresPad,
                Sha256: not null,
            } && journalBase == baseMode
                && journalRequiresPad == requiresCoolingPad;
            if (!durableOwnership)
            {
                return await FailArmAndRestoreIfNeededAsync(
                        baseMode,
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
                journal.Sha256!);
            try
            {
                await CreateMarkerAsync(markerPath, marker, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await FailArmAndRestoreIfNeededAsync(
                        baseMode,
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
                    await InspectAndRestoreAfterArmFailureAsync(baseMode)
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
                    await InspectAndRestoreAfterArmFailureAsync(baseMode)
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
        InspectAndRestoreAfterArmFailureAsync(PerformancePreset baseMode)
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
                return (false, IsExactRestoredState(inspected, baseMode));
            }

            return (true, await RestoreOnceAsync(baseMode, timeout.Token)
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
        bool exactFinal = IsExactRestoredState(first, marker.BaseMode)
            && IsExactRestoredState(second, marker.BaseMode);
        bool stable = first.ConfirmedState?.HyperBoostActive == false
            && first.ConfirmedState?.Performance == second.ConfirmedState?.Performance
            && first.ConfirmedState?.Fans == second.ConfirmedState?.Fans;
        HyperBoostLifecycleJournalObservation journal =
            await observeJournal(cancellationToken).ConfigureAwait(false);
        bool journalRetired = !journal.Exists;

        IReadOnlyList<HyperBoostLifecycleDiagnosticResult> diagnostics =
            await readDiagnostics(cancellationToken).ConfigureAwait(false);
        string boundary = scenario == HyperBoostLifecycleScenario.LowPower
            ? "before low power"
            : "before unexpected-exit recovery";
        int attempts = diagnostics.Count(entry =>
            entry.Timestamp >= marker.ArmedAtUtc
            && entry.Subsystem == "Service"
            && entry.Action == "Reconcile"
            && entry.Detail?.Contains(
                $"Attempting to restore OpenBlade-owned HyperBoost to {PerformanceLabel(marker.BaseMode)} {boundary}.",
                StringComparison.Ordinal) == true);
        int completions = diagnostics.Count(entry =>
            entry.Timestamp >= marker.ArmedAtUtc
            && entry.Subsystem == "Service"
            && entry.Action == "Reconcile"
            && entry.Detail?.Contains(
                $"OpenBlade-owned HyperBoost was restored to {PerformanceLabel(marker.BaseMode)} {boundary}.",
                StringComparison.Ordinal) == true);
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
        PerformancePreset baseMode,
        bool hyperBoostConfirmed,
        string message,
        CancellationToken cancellationToken)
    {
        bool cleanupAttempted = hyperBoostConfirmed;
        bool restored = !hyperBoostConfirmed || await RestoreOnceAsync(
                baseMode,
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
        PerformancePreset baseMode,
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
                return IsExactRestoredState(current, baseMode);
            }

            ControlOperationResult result = await client.SetHyperBoostAsync(
                    enabled: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return IsAccepted(result)
                && await WaitForStateAsync(
                        candidate => IsExactRestoredState(candidate, baseMode),
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
        out PerformancePreset baseMode,
        out bool requiresCoolingPad,
        out string failure)
    {
        baseMode = status.ConfirmedState?.Performance ?? PerformancePreset.Balanced;
        requiresCoolingPad = false;
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
                Performance: { } confirmedBase,
                Fans: FanMode.Automatic,
            }
            || status.Capabilities?.HyperBoostAccess
                != DeviceFeatureAccess.ReadWrite
            || (status.Capabilities.HyperBoostBaseModes?.AccessFor(confirmedBase)
                ?? (confirmedBase == PerformancePreset.Balanced
                    ? status.Capabilities.HyperBoostAccess
                    : null)) != DeviceFeatureAccess.ReadWrite
            || status.ActivePowerSource != PowerSource.PluggedIn)
        {
            failure =
                "The exact installed generation, verified inactive base, Automatic fans, and writable HyperBoost capability are required.";
            return false;
        }

        baseMode = confirmedBase;
        requiresCoolingPad = status.CoolingPad is
        {
            Connection: CoolingPadConnectionState.PresentExact,
            DesiredFanIntent.Mode: CoolingPadFanMode.Automatic,
        };
        if (!requiresCoolingPad)
        {
            failure =
                "Installed-service lifecycle validation requires the exact cooling pad with saved Auto intent.";
            return false;
        }

        return true;
    }

    internal static bool ValidateInstalledGeneration(
        FirmwareSmokeSnapshot baseline,
        FirmwareSmokeSnapshot current) =>
        FirmwareSmokeAssessment.CompareUnchangedInstalledGeneration(
                baseline,
                current)
            .Succeeded;

    private static bool IsExactRestoredState(
        ServiceStatusSnapshot status,
        PerformancePreset baseMode) =>
        status.DeviceVerified
        && !status.WritesBlocked
        && status.UnconfirmedWrites.Count == 0
        && status.ConfirmedState is
        {
            HyperBoostActive: false,
            Performance: var restored,
            Fans: FanMode.Automatic,
        }
        && restored == baseMode;

    private static bool IsAccepted(ControlOperationResult result) =>
        result.Status is ControlOperationStatus.Applied
            or ControlOperationStatus.Stored;

    private static string PerformanceLabel(PerformancePreset baseMode) =>
        baseMode == PerformancePreset.Silent ? "Quiet" : baseMode.ToString();

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

            try
            {
                return new HyperBoostLifecycleServiceProcess(
                    processes[0].Id,
                    processes[0].StartTime.ToUniversalTime().Ticks);
            }
            catch
            {
                return null;
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool IsProcessAlive(HyperBoostLifecycleServiceProcess identity)
    {
        try
        {
            using Process process = Process.GetProcessById(identity.ProcessId);
            return !process.HasExited
                && process.StartTime.ToUniversalTime().Ticks == identity.StartUtcTicks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

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
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            int schemaVersion = root.GetProperty("schemaVersion").GetInt32();
            int state = root.GetProperty("state").GetInt32();
            int baseMode = root.GetProperty("baseMode").GetInt32();
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
            bool valid = schemaVersion == 1
                && stateName is not null
                && parsedBase is PerformancePreset.Silent
                    or PerformancePreset.Balanced
                    or PerformancePreset.Performance;
            return new HyperBoostLifecycleJournalObservation(
                true,
                valid,
                stateName,
                valid ? parsedBase : null,
                requiresPad,
                Convert.ToHexString(SHA256.HashData(bytes)));
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
                null);
        }
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
