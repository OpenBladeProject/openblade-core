using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal sealed record ThermalConvergenceInFlight(
    Guid TransitionAttemptId,
    IReadOnlyList<ThermalComponent> DeclaredComponents,
    ConfirmedDeviceState PreWriteObservation,
    DesiredDeviceState DesiredTarget,
    bool ReconciliationRequired);

internal sealed record ThermalConvergenceTombstone(
    PowerSource Destination,
    ThermalConvergenceContradiction Contradiction,
    IReadOnlyList<ThermalComponent> AffectedComponents,
    string? PerformanceDesiredRevision,
    string? FanDesiredRevision,
    string PolicyRevision,
    ThermalTransitionDisposition TerminalDisposition);

internal sealed record ThermalConvergenceBudget(
    PowerSource Destination,
    ThermalComponent Component,
    string DesiredRevision,
    string PolicyRevision,
    int ConsumedClaims,
    int MaximumClaims);

internal sealed record ThermalConvergenceLedgerState(
    string ActiveUserSid,
    Guid DeviceAuthorityEpoch,
    string? DeviceFingerprint,
    long IntentGeneration,
    DesiredDeviceState? EffectiveIntent,
    string? PerformanceDesiredRevision,
    string? FanDesiredRevision,
    string? PolicyRevision,
    PowerSource? SettledDestination,
    ThermalConvergenceContradiction Contradiction,
    ThermalConvergenceInFlight? InFlight,
    IReadOnlyList<ThermalConvergenceTombstone> Tombstones,
    ThermalTransitionDisposition? TerminalDisposition,
    IReadOnlyList<ThermalComponent> TerminalAffectedComponents,
    IReadOnlyList<ThermalConvergenceBudget> Budgets)
{
    internal bool IsEmpty =>
        DeviceAuthorityEpoch == Guid.Empty
        && DeviceFingerprint is null
        && IntentGeneration == 0
        && EffectiveIntent is null
        && PerformanceDesiredRevision is null
        && FanDesiredRevision is null
        && PolicyRevision is null
        && SettledDestination is null
        && Contradiction == ThermalConvergenceContradiction.None
        && InFlight is null
        && Tombstones.Count == 0
        && TerminalDisposition is null
        && TerminalAffectedComponents.Count == 0
        && Budgets.Count == 0;

    internal bool RequiresReadOnlyReconciliation =>
        InFlight?.ReconciliationRequired == true;

    internal static ThermalConvergenceLedgerState Empty(string activeUserSid) => new(
        activeUserSid,
        Guid.Empty,
        DeviceFingerprint: null,
        IntentGeneration: 0,
        EffectiveIntent: null,
        PerformanceDesiredRevision: null,
        FanDesiredRevision: null,
        PolicyRevision: null,
        SettledDestination: null,
        ThermalConvergenceContradiction.None,
        InFlight: null,
        Tombstones: [],
        TerminalDisposition: null,
        TerminalAffectedComponents: [],
        Budgets: []);
}

internal sealed record ThermalConvergenceStateDocument(
    int SchemaVersion,
    ThermalConvergenceLedgerState State);

internal sealed record ThermalConvergenceLoadResult(
    ThermalConvergenceLedgerState State,
    ConfigurationLoadDisposition Disposition,
    ConfigurationQuarantineReason QuarantineReason,
    string? Sha256)
{
    internal bool IsAuthoritative =>
        Disposition == ConfigurationLoadDisposition.Admitted;
}

internal readonly record struct ThermalConvergenceMutationResult(
    bool Applied,
    string? Sha256 = null);

internal sealed class ThermalConvergenceStateStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumAggregateClaims = 4;
    private static readonly ConcurrentDictionary<string, Lazy<SemaphoreSlim>> PathGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };

    private readonly string path;
    private readonly string activeUserSid;
    private readonly SemaphoreSlim gate;

    internal ThermalConvergenceStateStore(string path, SecurityIdentifier activeUserSid)
    {
        this.path = System.IO.Path.GetFullPath(
            string.IsNullOrWhiteSpace(path)
                ? throw new ArgumentException(
                    "A thermal convergence-state path is required.",
                    nameof(path))
                : path);
        ArgumentNullException.ThrowIfNull(activeUserSid);
        if (!activeUserSid.IsAccountSid())
        {
            throw new ArgumentException(
                "A Windows account SID is required.",
                nameof(activeUserSid));
        }

        this.activeUserSid = activeUserSid.Value;
        gate = PathGates.GetOrAdd(
            this.path,
            static _ => new Lazy<SemaphoreSlim>(
                static () => new SemaphoreSlim(1, 1),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    internal string Path => path;

    internal async Task<ThermalConvergenceLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? json = await ReadIfPresentAsync(cancellationToken)
                .ConfigureAwait(false);
            return json is null
                ? AdmittedEmpty()
                : Read(json);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<ThermalConvergenceMutationResult> TrySaveAsync(
        string? expectedSha256,
        ThermalConvergenceLedgerState state,
        CancellationToken cancellationToken)
    {
        ValidateState(state, activeUserSid, persisted: true);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? current = await ReadIfPresentAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                if (expectedSha256 is not null)
                {
                    return new ThermalConvergenceMutationResult(false);
                }
            }
            else
            {
                ThermalConvergenceLoadResult loaded = Read(current);
                if (!loaded.IsAuthoritative)
                {
                    throw new ConfigurationQuarantinedException(
                        "The thermal convergence record is preserved in quarantine.",
                        loaded.QuarantineReason);
                }
                if (expectedSha256 is null
                    || !StringComparer.Ordinal.Equals(
                        expectedSha256,
                        loaded.Sha256))
                {
                    return new ThermalConvergenceMutationResult(false);
                }
            }

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                new ThermalConvergenceStateDocument(CurrentSchemaVersion, state),
                SerializerOptions);
            await AtomicFile.WriteAsync(path, json, cancellationToken)
                .ConfigureAwait(false);
            return new ThermalConvergenceMutationResult(
                Applied: true,
                Convert.ToHexString(SHA256.HashData(json)));
        }
        finally
        {
            gate.Release();
        }
    }

    private ThermalConvergenceLoadResult Read(ReadOnlySpan<byte> json)
    {
        try
        {
            BatteryConfigurationStore.RejectDuplicateMembers(json);
            ThermalConvergenceStateDocument? document =
                JsonSerializer.Deserialize<ThermalConvergenceStateDocument>(
                    json,
                    SerializerOptions);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion)
            {
                return Quarantined(
                    ContainsFutureSchema(json)
                        ? ConfigurationQuarantineReason.FutureSchema
                        : ConfigurationQuarantineReason.InvalidDocument);
            }

            ValidateState(document.State, activeUserSid, persisted: true);
            return new ThermalConvergenceLoadResult(
                document.State,
                ConfigurationLoadDisposition.Admitted,
                ConfigurationQuarantineReason.None,
                Convert.ToHexString(SHA256.HashData(json)));
        }
        catch (Exception exception) when (BatteryConfigurationStore.IsRecoverable(exception))
        {
            DiagnosticTrace.TraceException(nameof(ThermalConvergenceStateStore), nameof(Read), exception);
            return Quarantined(
                ContainsFutureSchema(json)
                    ? ConfigurationQuarantineReason.FutureSchema
                    : ConfigurationQuarantineReason.InvalidDocument);
        }
    }

    private ThermalConvergenceLoadResult AdmittedEmpty() => new(
        ThermalConvergenceLedgerState.Empty(activeUserSid),
        ConfigurationLoadDisposition.Admitted,
        ConfigurationQuarantineReason.None,
        Sha256: null);

    private ThermalConvergenceLoadResult Quarantined(
        ConfigurationQuarantineReason reason) => new(
        ThermalConvergenceLedgerState.Empty(activeUserSid),
        ConfigurationLoadDisposition.Quarantined,
        reason,
        Sha256: null);

    private static void ValidateState(
        ThermalConvergenceLedgerState? state,
        string activeUserSid,
        bool persisted)
    {
        if (state is null
            || !string.Equals(
                state.ActiveUserSid,
                activeUserSid,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The thermal convergence record belongs to a different user.");
        }
        if (state.Tombstones is null
            || state.TerminalAffectedComponents is null
            || state.Budgets is null)
        {
            throw new InvalidDataException(
                "The thermal convergence record is missing required evidence collections.");
        }
        if (state.IsEmpty)
        {
            if (persisted)
            {
                throw new InvalidDataException(
                    "An empty thermal convergence record must not be persisted.");
            }
            return;
        }
        if (state.DeviceAuthorityEpoch == Guid.Empty
            || !IsBoundedIdentifier(state.DeviceFingerprint, 256)
            || state.IntentGeneration <= 0
            || state.EffectiveIntent is null
            || !IsRevision(state.PerformanceDesiredRevision)
            || !IsRevision(state.FanDesiredRevision)
            || !IsRevision(state.PolicyRevision)
            || state.SettledDestination is not { } destination
            || !IsSupportedDestination(destination)
            || !IsContradiction(state.Contradiction))
        {
            throw new InvalidDataException(
                "The thermal convergence record is incomplete or invalid.");
        }

        ValidateDesired(state.EffectiveIntent);
        ValidateInFlight(state.InFlight, state);
        ValidateComponents(state.TerminalAffectedComponents, allowEmpty: true);
        if (state.TerminalDisposition is { } terminal && !Enum.IsDefined(terminal))
        {
            throw new InvalidDataException(
                "The thermal convergence terminal disposition is undefined.");
        }
        if ((state.TerminalDisposition is null)
            != (state.TerminalAffectedComponents.Count == 0))
        {
            throw new InvalidDataException(
                "Thermal terminal evidence must include both disposition and components.");
        }

        ValidateTombstones(state.Tombstones);
        ValidateBudgets(state.Budgets);
    }

    private static void ValidateInFlight(
        ThermalConvergenceInFlight? inFlight,
        ThermalConvergenceLedgerState state)
    {
        if (inFlight is null)
        {
            return;
        }
        if (inFlight.TransitionAttemptId == Guid.Empty
            || inFlight.PreWriteObservation is null
            || inFlight.DesiredTarget is null
            || !inFlight.ReconciliationRequired)
        {
            throw new InvalidDataException(
                "The in-flight thermal convergence reservation is invalid.");
        }
        ValidateComponents(inFlight.DeclaredComponents, allowEmpty: false);
        ValidateObserved(inFlight.PreWriteObservation);
        ValidateDesired(inFlight.DesiredTarget);
        if (inFlight.DesiredTarget != state.EffectiveIntent)
        {
            throw new InvalidDataException(
                "The in-flight thermal target does not match the reserved intent snapshot.");
        }
        ThermalConvergenceContradiction declared = ComponentsToContradiction(
            inFlight.DeclaredComponents);
        if ((declared & state.Contradiction) != state.Contradiction)
        {
            throw new InvalidDataException(
                "The in-flight reservation did not declare every contradicted component.");
        }
    }

    private static void ValidateTombstones(
        IReadOnlyList<ThermalConvergenceTombstone>? tombstones)
    {
        if (tombstones is null)
        {
            throw new InvalidDataException("Thermal convergence tombstones are missing.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ThermalConvergenceTombstone tombstone in tombstones)
        {
            if (tombstone is null
                || !IsSupportedDestination(tombstone.Destination)
                || tombstone.Contradiction == ThermalConvergenceContradiction.None
                || !IsContradiction(tombstone.Contradiction)
                || !IsRevision(tombstone.PolicyRevision)
                || !Enum.IsDefined(tombstone.TerminalDisposition))
            {
                throw new InvalidDataException("A thermal convergence tombstone is invalid.");
            }
            ValidateComponents(tombstone.AffectedComponents, allowEmpty: false);
            if (ComponentsToContradiction(tombstone.AffectedComponents)
                != tombstone.Contradiction)
            {
                throw new InvalidDataException(
                    "A thermal convergence tombstone has mismatched affected components.");
            }
            if (tombstone.Contradiction.HasFlag(ThermalConvergenceContradiction.Performance)
                    != IsRevision(tombstone.PerformanceDesiredRevision)
                || tombstone.Contradiction.HasFlag(ThermalConvergenceContradiction.FanTopology)
                    != IsRevision(tombstone.FanDesiredRevision))
            {
                throw new InvalidDataException(
                    "A thermal convergence tombstone has mismatched component revisions.");
            }

            string key = string.Join(
                '|',
                tombstone.Destination,
                (int)tombstone.Contradiction,
                tombstone.PerformanceDesiredRevision,
                tombstone.FanDesiredRevision,
                tombstone.PolicyRevision);
            if (!keys.Add(key))
            {
                throw new InvalidDataException(
                    "A thermal convergence tombstone is duplicated.");
            }
        }
    }

    private static void ValidateBudgets(
        IReadOnlyList<ThermalConvergenceBudget>? budgets)
    {
        if (budgets is null)
        {
            throw new InvalidDataException("Thermal convergence budgets are missing.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ThermalConvergenceBudget budget in budgets)
        {
            if (budget is null
                || !IsSupportedDestination(budget.Destination)
                || !Enum.IsDefined(budget.Component)
                || !IsRevision(budget.DesiredRevision)
                || !IsRevision(budget.PolicyRevision)
                || budget.MaximumClaims != 1
                || budget.ConsumedClaims < 0
                || budget.ConsumedClaims > budget.MaximumClaims)
            {
                throw new InvalidDataException("A thermal convergence budget is invalid.");
            }
            string key = string.Join(
                '|',
                budget.Destination,
                budget.Component,
                budget.DesiredRevision,
                budget.PolicyRevision);
            if (!keys.Add(key))
            {
                throw new InvalidDataException(
                    "A thermal convergence budget is duplicated.");
            }
        }
        foreach (IGrouping<string, ThermalConvergenceBudget> group in budgets.GroupBy(
            budget => string.Join(
                '|',
                budget.Destination,
                budget.PolicyRevision),
            StringComparer.Ordinal))
        {
            if (group.Sum(budget => budget.ConsumedClaims) > MaximumAggregateClaims)
            {
                throw new InvalidDataException(
                    "A thermal convergence revision exceeded its aggregate claim cap.");
            }
        }
    }

    private static void ValidateObserved(ConfirmedDeviceState observed)
    {
        if (observed.Performance is { } performance && !Enum.IsDefined(performance)
            || observed.Fans is { } fans && !Enum.IsDefined(fans)
            || observed.CustomPerformance is not null
                && !CustomPerformanceSettings.IsWellFormed(observed.CustomPerformance)
            || observed.GpuClockOffsets is { IsWellFormed: false })
        {
            throw new InvalidDataException(
                "The pre-write thermal observation is invalid.");
        }
    }

    private static void ValidateDesired(DesiredDeviceState desired)
    {
        if (desired.Performance is null
            || !Enum.IsDefined(desired.Performance.Value)
            || !Enum.IsDefined(desired.Performance.Source)
            || desired.Fans is null
            || !Enum.IsDefined(desired.Fans.Value)
            || !Enum.IsDefined(desired.Fans.Source)
            || desired.CustomPerformance is not null
                && !CustomPerformanceSettings.IsWellFormed(desired.CustomPerformance)
            || desired.GpuClockTuning is not null
                && !GpuClockTuningSettings.IsWellFormed(desired.GpuClockTuning)
            || desired.FanControl is null
            || desired.FanControl.Mode != desired.Fans.Value)
        {
            throw new InvalidDataException(
                "The effective thermal intent snapshot is invalid.");
        }
    }

    private static void ValidateComponents(
        IReadOnlyList<ThermalComponent>? components,
        bool allowEmpty)
    {
        if (components is null
            || !allowEmpty && components.Count == 0
            || components.Any(component => !Enum.IsDefined(component))
            || components.Distinct().Count() != components.Count
            || !components.SequenceEqual(components.OrderBy(component => component)))
        {
            throw new InvalidDataException(
                "Thermal components must be unique, defined, and sorted.");
        }
    }

    private static ThermalConvergenceContradiction ComponentsToContradiction(
        IReadOnlyList<ThermalComponent> components) => components.Aggregate(
            ThermalConvergenceContradiction.None,
            static (result, component) => result | component switch
            {
                ThermalComponent.Performance => ThermalConvergenceContradiction.Performance,
                ThermalComponent.Fans => ThermalConvergenceContradiction.FanTopology,
                _ => ThermalConvergenceContradiction.None,
            });

    private static bool IsSupportedDestination(PowerSource destination) =>
        Enum.IsDefined(destination) && destination != PowerSource.ExternalUnknown;

    private static bool IsContradiction(ThermalConvergenceContradiction contradiction) =>
        (contradiction & ~(
            ThermalConvergenceContradiction.Performance
            | ThermalConvergenceContradiction.FanTopology)) == 0;

    private static bool IsRevision(string? value) =>
        Guid.TryParseExact(value, "D", out Guid revision) && revision != Guid.Empty;

    private static bool IsBoundedIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl);

    private static bool ContainsFutureSchema(ReadOnlySpan<byte> json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(property =>
                    property.NameEquals("schemaVersion")
                    && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out int schemaVersion)
                    && schemaVersion > CurrentSchemaVersion);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<byte[]?> ReadIfPresentAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
