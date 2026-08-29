using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal enum HyperBoostOwnershipJournalState
{
    EnablePending,
    OwnedConfirmed,
    RestorePending,
    Relinquished,
}

internal enum HyperBoostRestoreRetryDisposition
{
    None,
    DeferredPreSubmission,
}

internal enum HyperBoostRestoreRetryTarget
{
    None,
    ExactJournalIntent,
    CurrentPowerDestination,
}

internal sealed record HyperBoostRestoreIntent(
    PerformancePreset Performance,
    CustomPerformanceSettings? CustomPerformance,
    GpuClockTuningSettings? GpuClockTuning,
    FanControlSettings FanControl)
{
    internal static HyperBoostRestoreIntent BalancedAutomatic { get; } = new(
        PerformancePreset.Balanced,
        CustomPerformance: null,
        GpuClockTuning: null,
        FanControlSettings.FromLegacy(FanMode.Automatic));

    internal bool IsWellFormed =>
        Performance is (PerformancePreset.BatterySaver
            or PerformancePreset.BatteryBalanced
            or PerformancePreset.Silent
            or PerformancePreset.Balanced
            or PerformancePreset.Performance
            or PerformancePreset.Custom)
        && (Performance == PerformancePreset.Custom
            ? CustomPerformanceSettings.IsWellFormed(CustomPerformance)
            : CustomPerformance is null)
        && (GpuClockTuning is null
            || GpuClockTuningSettings.IsWellFormed(GpuClockTuning))
        && FanControl is not null
        && Enum.IsDefined(FanControl.Mode);
}

internal sealed record HyperBoostRecoveryBaseline(
    PerformancePreset? Performance,
    FanMode Fans,
    bool HyperBoostActive,
    CustomPerformanceSettings? CustomPerformance = null,
    GpuClockOffsetSettings? GpuClockOffsets = null)
{
    internal bool IsWellFormed =>
        (Performance is null
            or PerformancePreset.BatterySaver
            or PerformancePreset.BatteryBalanced
            or PerformancePreset.Silent
            or PerformancePreset.Balanced
            or PerformancePreset.Performance
            or PerformancePreset.Custom)
        && Enum.IsDefined(Fans)
        && !HyperBoostActive
        && (CustomPerformance is null
            || CustomPerformanceSettings.IsWellFormed(CustomPerformance))
        && (GpuClockOffsets is null
            || GpuClockOffsetSettings.IsWellFormed(GpuClockOffsets));
}

internal sealed record HyperBoostOwnershipJournal(
    HyperBoostOwnershipJournalState State,
    PerformancePreset BaseMode,
    bool RequiresCoolingPad,
    DateTimeOffset UpdatedAtUtc)
{
    internal HyperBoostRestoreIntent RestoreIntent { get; init; } =
        HyperBoostRestoreIntent.BalancedAutomatic;

    internal HyperBoostRecoveryBaseline? RecoveryBaseline { get; init; }

    internal HyperBoostRestoreRetryDisposition RestoreRetryDisposition { get; init; }

    internal HyperBoostRestoreRetryTarget RestoreRetryTarget { get; init; }

    internal HyperBoostOwnershipJournal(
        HyperBoostOwnershipJournalState state,
        PerformancePreset baseMode,
        DateTimeOffset updatedAtUtc)
        : this(state, baseMode, RequiresCoolingPad: true, updatedAtUtc)
    {
    }

    internal bool IsWellFormed =>
        Enum.IsDefined(State)
        && BaseMode is (PerformancePreset.Silent
            or PerformancePreset.Balanced
            or PerformancePreset.Performance)
        && RestoreIntent is { IsWellFormed: true }
        && Enum.IsDefined(RestoreRetryDisposition)
        && Enum.IsDefined(RestoreRetryTarget)
        && (RecoveryBaseline is null
            || State == HyperBoostOwnershipJournalState.RestorePending
                && RecoveryBaseline.IsWellFormed)
        && (RestoreRetryDisposition == HyperBoostRestoreRetryDisposition.None
            ? RestoreRetryTarget == HyperBoostRestoreRetryTarget.None
            : State == HyperBoostOwnershipJournalState.RestorePending
                && RecoveryBaseline is { IsWellFormed: true }
                && RestoreRetryTarget is
                    (HyperBoostRestoreRetryTarget.ExactJournalIntent
                    or HyperBoostRestoreRetryTarget.CurrentPowerDestination))
        && UpdatedAtUtc != default;
}

internal readonly record struct HyperBoostOwnershipJournalLoadResult(
    HyperBoostOwnershipJournal? Journal,
    bool Invalid,
    bool AllowsExplicitRecoveryBaselineCapture = false,
    string? Sha256 = null);

internal readonly record struct HyperBoostOwnershipJournalMutationResult(
    bool Applied,
    string? Sha256 = null);

public interface IServiceOwnershipJournalCleaner
{
    Task ClearAsync(CancellationToken cancellationToken);
}

internal sealed record HyperBoostOwnershipJournalDocument(
    int SchemaVersion,
    HyperBoostOwnershipJournalState State,
    PerformancePreset BaseMode,
    DateTimeOffset UpdatedAtUtc,
    bool? RequiresCoolingPad = null,
    PerformancePreset? RestorePerformance = null,
    CustomPerformanceSettings? RestoreCustomPerformance = null,
    GpuClockTuningSettings? RestoreGpuClockTuning = null,
    FanControlSettings? RestoreFanControl = null,
    HyperBoostRecoveryBaseline? RecoveryBaseline = null,
    HyperBoostRestoreRetryDisposition? RestoreRetryDisposition = null,
    HyperBoostRestoreRetryTarget? RestoreRetryTarget = null);

internal interface IHyperBoostOwnershipJournalStore
    : IServiceOwnershipJournalCleaner
{
    Task<HyperBoostOwnershipJournalLoadResult> LoadAsync(
        CancellationToken cancellationToken);

    Task WriteAsync(
        HyperBoostOwnershipJournal journal,
        CancellationToken cancellationToken);

    Task<HyperBoostOwnershipJournalMutationResult> TryReplaceAsync(
        HyperBoostOwnershipJournal expectedJournal,
        string? expectedSha256,
        HyperBoostOwnershipJournal replacementJournal,
        CancellationToken cancellationToken);

    Task<bool> TryClearAsync(
        HyperBoostOwnershipJournal expectedJournal,
        string? expectedSha256,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableHyperBoostOwnershipJournalStore
    : IHyperBoostOwnershipJournalStore
{
    internal static UnavailableHyperBoostOwnershipJournalStore Instance { get; } = new();

    private UnavailableHyperBoostOwnershipJournalStore()
    {
    }

    public Task<HyperBoostOwnershipJournalLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HyperBoostOwnershipJournalLoadResult(null, false));
    }

    public Task WriteAsync(
        HyperBoostOwnershipJournal journal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<HyperBoostOwnershipJournalMutationResult> TryReplaceAsync(
        HyperBoostOwnershipJournal expectedJournal,
        string? expectedSha256,
        HyperBoostOwnershipJournal replacementJournal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HyperBoostOwnershipJournalMutationResult(false));
    }

    public Task<bool> TryClearAsync(
        HyperBoostOwnershipJournal expectedJournal,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal sealed class HyperBoostOwnershipJournalStore : IHyperBoostOwnershipJournalStore
{
    private const int CurrentSchemaVersion = 6;
    private const int RetryDispositionSchemaVersion = 5;
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            WriteIndented = true,
        };

    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);

    internal HyperBoostOwnershipJournalStore(string path)
    {
        this.path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException(
                "A HyperBoost ownership-journal path is required.",
                nameof(path))
            : Path.GetFullPath(path);
    }

    public async Task<HyperBoostOwnershipJournalLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return new HyperBoostOwnershipJournalLoadResult(null, false);
            }

            try
            {
                byte[] json = await File.ReadAllBytesAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                HyperBoostOwnershipJournalDocument? document =
                    JsonSerializer.Deserialize<HyperBoostOwnershipJournalDocument>(
                        json,
                        SerializerOptions);
                bool recognizedSchema = document is
                { SchemaVersion: 1, RequiresCoolingPad: null }
                    or { SchemaVersion: 2, RequiresCoolingPad: not null }
                    or
                {
                    SchemaVersion: 3 or 4 or RetryDispositionSchemaVersion
                        or CurrentSchemaVersion,
                    RequiresCoolingPad: not null,
                    RestorePerformance: not null,
                    RestoreFanControl: not null,
                };
                HyperBoostRestoreIntent restoreIntent = document is
                {
                    SchemaVersion: 3 or 4 or RetryDispositionSchemaVersion
                        or CurrentSchemaVersion,
                    RestorePerformance: { } restorePerformance,
                    RestoreFanControl: { } restoreFanControl,
                }
                    ? new HyperBoostRestoreIntent(
                        restorePerformance,
                        document.RestoreCustomPerformance,
                        document.RestoreGpuClockTuning,
                        restoreFanControl)
                    : document is null
                        ? HyperBoostRestoreIntent.BalancedAutomatic
                        : new HyperBoostRestoreIntent(
                            document.BaseMode,
                            CustomPerformance: null,
                            GpuClockTuning: null,
                            FanControlSettings.FromLegacy(FanMode.Automatic));
                var journal = document is null
                    ? null
                    : new HyperBoostOwnershipJournal(
                        document.State,
                        document.BaseMode,
                        document.SchemaVersion == 1
                            ? true
                            : document.RequiresCoolingPad!.Value,
                        document.UpdatedAtUtc)
                    {
                        RestoreIntent = restoreIntent,
                        RecoveryBaseline = document.SchemaVersion
                            is 4 or RetryDispositionSchemaVersion
                                or CurrentSchemaVersion
                                ? document.RecoveryBaseline
                                : null,
                        RestoreRetryDisposition = document.SchemaVersion
                            is RetryDispositionSchemaVersion
                                or CurrentSchemaVersion
                                ? document.RestoreRetryDisposition
                                    ?? HyperBoostRestoreRetryDisposition.None
                                : HyperBoostRestoreRetryDisposition.None,
                        RestoreRetryTarget = document.SchemaVersion
                            == CurrentSchemaVersion
                                ? document.RestoreRetryTarget
                                    ?? HyperBoostRestoreRetryTarget.None
                                : HyperBoostRestoreRetryTarget.None,
                    };
                return !recognizedSchema
                    || journal is not { IsWellFormed: true }
                    ? new HyperBoostOwnershipJournalLoadResult(null, true)
                    : new HyperBoostOwnershipJournalLoadResult(
                        journal,
                        Invalid: false,
                        AllowsExplicitRecoveryBaselineCapture:
                            document!.SchemaVersion is 3 or 4
                                or RetryDispositionSchemaVersion
                                or CurrentSchemaVersion
                            && journal.State
                                == HyperBoostOwnershipJournalState.RestorePending
                            && journal.RecoveryBaseline is null,
                        Sha256: Convert.ToHexString(SHA256.HashData(json)));
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new HyperBoostOwnershipJournalLoadResult(null, true);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task WriteAsync(
        HyperBoostOwnershipJournal journal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (!journal.IsWellFormed)
        {
            throw new ArgumentException(
                "The HyperBoost ownership journal is invalid.",
                nameof(journal));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] json = Serialize(journal);
            await AtomicFile.WriteAsync(path, json, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<HyperBoostOwnershipJournalMutationResult> TryReplaceAsync(
        HyperBoostOwnershipJournal expectedJournal,
        string? expectedSha256,
        HyperBoostOwnershipJournal replacementJournal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedJournal);
        ArgumentNullException.ThrowIfNull(replacementJournal);
        if (!expectedJournal.IsWellFormed)
        {
            throw new ArgumentException(
                "The expected HyperBoost ownership journal is invalid.",
                nameof(expectedJournal));
        }
        if (!replacementJournal.IsWellFormed)
        {
            throw new ArgumentException(
                "The replacement HyperBoost ownership journal is invalid.",
                nameof(replacementJournal));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (expectedSha256 is null || !File.Exists(path))
            {
                return new HyperBoostOwnershipJournalMutationResult(false);
            }

            byte[] current = await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
            string currentSha256 = Convert.ToHexString(SHA256.HashData(current));
            if (!StringComparer.Ordinal.Equals(currentSha256, expectedSha256))
            {
                return new HyperBoostOwnershipJournalMutationResult(false);
            }

            byte[] replacement = Serialize(replacementJournal);
            await AtomicFile.WriteAsync(path, replacement, cancellationToken)
                .ConfigureAwait(false);
            return new HyperBoostOwnershipJournalMutationResult(
                Applied: true,
                Convert.ToHexString(SHA256.HashData(replacement)));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> TryClearAsync(
        HyperBoostOwnershipJournal expectedJournal,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedJournal);
        if (!expectedJournal.IsWellFormed)
        {
            throw new ArgumentException(
                "The expected HyperBoost ownership journal is invalid.",
                nameof(expectedJournal));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (expectedSha256 is null || !File.Exists(path))
            {
                return false;
            }

            byte[] current = await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
            string currentSha256 = Convert.ToHexString(SHA256.HashData(current));
            if (!StringComparer.Ordinal.Equals(currentSha256, expectedSha256))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
        }
        finally
        {
            gate.Release();
        }
    }

    private static byte[] Serialize(HyperBoostOwnershipJournal journal) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new HyperBoostOwnershipJournalDocument(
                CurrentSchemaVersion,
                journal.State,
                journal.BaseMode,
                journal.UpdatedAtUtc,
                journal.RequiresCoolingPad,
                journal.RestoreIntent.Performance,
                journal.RestoreIntent.CustomPerformance,
                journal.RestoreIntent.GpuClockTuning,
                journal.RestoreIntent.FanControl,
                journal.RecoveryBaseline,
                journal.RestoreRetryDisposition,
                journal.RestoreRetryTarget),
            SerializerOptions);
}
