using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed record PersistentOverrideState(
    PerformancePreset? Performance,
    FanMode? Fans,
    LightingSettings? Lighting,
    CustomPerformanceSettings? CustomPerformance = null,
    FanControlSettings? FanControl = null,
    GpuClockTuningSettings? GpuClockTuning = null)
{
    public static PersistentOverrideState Empty { get; } = new(null, null, null);
}

public sealed record PersistentOverrideLoadResult(
    PersistentOverrideState Overrides,
    bool Recovered,
    ConfigurationLoadDisposition Disposition = ConfigurationLoadDisposition.Admitted,
    ConfigurationQuarantineReason QuarantineReason = ConfigurationQuarantineReason.None);

internal sealed record PersistentOverrideDocument(
    int SchemaVersion,
    PersistentOverrideState Overrides);

internal sealed record SchemaTwoPersistentOverrideState(
    PerformancePreset? Performance,
    FanMode? Fans,
    LightingSettings? Lighting,
    CustomPerformanceSettings? CustomPerformance);

internal sealed record SchemaTwoPersistentOverrideDocument(
    int SchemaVersion,
    SchemaTwoPersistentOverrideState Overrides);

internal sealed record LegacyPersistentOverrideState(
    PerformancePreset? Performance,
    FanMode? Fans,
    LightingSettings? Lighting);

internal sealed record LegacyPersistentOverrideDocument(
    int SchemaVersion,
    LegacyPersistentOverrideState Overrides);

public sealed class PersistentOverrideStore
{
    private static readonly ConcurrentDictionary<string, Lazy<SemaphoreSlim>> PathGates =
        new(StringComparer.OrdinalIgnoreCase);
    // Sequence is an additive LightingSettings subtype, not an override-envelope change.
    // Keeping schema 6 lets older builds retain documents that use known lighting kinds.
    private const int CurrentSchemaVersion = 6;
    private const int SchemaFiveVersion = 5;
    private const int SchemaFourVersion = 4;
    private const int SchemaThreeVersion = 3;
    private const int SchemaTwoVersion = 2;
    private const int LegacySchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly string path;
    private readonly Func<CancellationToken, Task>? beforeWrite;
    private readonly SemaphoreSlim gate;
    private bool preservationRequired;

    public PersistentOverrideStore(string path)
        : this(path, beforeWrite: null)
    {
    }

    internal PersistentOverrideStore(
        string path,
        Func<CancellationToken, Task>? beforeWrite)
    {
        this.path = Path.GetFullPath(string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("An override path is required.", nameof(path))
            : path);
        this.beforeWrite = beforeWrite;
        gate = PathGates.GetOrAdd(
            this.path,
            static _ => new Lazy<SemaphoreSlim>(
                static () => new SemaphoreSlim(1, 1),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public async Task<PersistentOverrideLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            PersistentOverrideInspection inspection = await LoadCoreAsync(
                persistMigration: true,
                cancellationToken);
            preservationRequired = inspection.Result.Disposition ==
                ConfigurationLoadDisposition.Quarantined;
            return inspection.Result;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<PersistentOverrideInspection> InspectAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(
                    persistMigration: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        PersistentOverrideState overrides,
        CancellationToken cancellationToken)
    {
        overrides = NormalizeLegacyGpuClockTuning(
            NormalizeLegacyFanControl(NormalizeLegacyLighting(overrides)));
        Validate(overrides);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (preservationRequired)
            {
                throw new ConfigurationQuarantinedException(
                    "The override document is preserved in quarantine and cannot be changed until a validated reload.",
                    ConfigurationQuarantineReason.InvalidDocument);
            }

            byte[]? existing = await ReadIfPresentAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                try
                {
                    _ = Deserialize(existing, out _);
                }
                catch (Exception exception)
                    when (BatteryConfigurationStore.IsRecoverable(exception))
                {
                    throw new ConfigurationQuarantinedException(
                        "The existing override document is preserved in quarantine.",
                        ContainsFutureSchema(existing)
                            ? ConfigurationQuarantineReason.FutureSchema
                            : ConfigurationQuarantineReason.InvalidDocument);
                }
            }

            await WriteCoreAsync(overrides, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PersistentOverrideInspection> LoadCoreAsync(
        bool persistMigration,
        CancellationToken cancellationToken)
    {
        byte[]? json = await ReadIfPresentAsync(cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return new PersistentOverrideInspection(
                new PersistentOverrideLoadResult(
                    PersistentOverrideState.Empty,
                    Recovered: false),
                WasEmpty: true);
        }

        try
        {
            PersistentOverrideState overrides = Deserialize(json, out bool migrated);
            if (migrated && persistMigration)
            {
                await WriteCoreAsync(overrides, cancellationToken).ConfigureAwait(false);
            }
            return new PersistentOverrideInspection(
                new PersistentOverrideLoadResult(overrides, Recovered: false),
                WasEmpty: false);
        }
        catch (Exception exception)
            when (BatteryConfigurationStore.IsRecoverable(exception))
        {
            return new PersistentOverrideInspection(
                new PersistentOverrideLoadResult(
                    PersistentOverrideState.Empty,
                    Recovered: true,
                    ConfigurationLoadDisposition.Quarantined,
                    ContainsFutureSchema(json)
                        ? ConfigurationQuarantineReason.FutureSchema
                        : ConfigurationQuarantineReason.InvalidDocument),
                WasEmpty: false);
        }
    }

    private async Task<byte[]?> ReadIfPresentAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
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

    private static bool ContainsFutureSchema(ReadOnlySpan<byte> json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return document.RootElement.EnumerateObject().Any(property =>
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

    internal static void Validate(PersistentOverrideState? overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        if (overrides.Performance is { } performance && !Enum.IsDefined(performance))
        {
            throw new InvalidDataException("The persistent performance override is undefined.");
        }

        if (overrides.Performance == PerformancePreset.Custom
            ? !CustomPerformanceSettings.IsWellFormed(overrides.CustomPerformance)
            : overrides.CustomPerformance is not null)
        {
            throw new InvalidDataException(
                "The persistent custom performance override is invalid.");
        }

        if (overrides.GpuClockTuning is not null
            && (!GpuClockTuningSettings.IsWellFormed(overrides.GpuClockTuning)
                || overrides.CustomPerformance?.GpuClockOffsets is not null))
        {
            throw new InvalidDataException(
                "The persistent GPU clock tuning override is invalid.");
        }

        if (overrides.Fans is { } fans && !Enum.IsDefined(fans))
        {
            throw new InvalidDataException("The persistent fan override is undefined.");
        }

        if (overrides.Fans is null
            ? overrides.FanControl is not null
            : overrides.FanControl is null || overrides.FanControl.Mode != overrides.Fans)
        {
            throw new InvalidDataException(
                "The persistent fan override details do not match its mode.");
        }

        if (overrides.Lighting is { } lighting
            && !LightingSettingsValidation.IsWellFormed(lighting))
        {
            throw new InvalidDataException("The persistent lighting override is invalid.");
        }
    }

    private static PersistentOverrideState Deserialize(
        ReadOnlySpan<byte> json,
        out bool migrated)
    {
        BatteryConfigurationStore.RejectDuplicateMembers(json);
        using JsonDocument parsed = JsonDocument.Parse(json.ToArray());
        if (!parsed.RootElement.TryGetProperty("schemaVersion", out JsonElement versionElement)
            || !versionElement.TryGetInt32(out int schemaVersion))
        {
            throw new InvalidDataException("The persistent override schema is unsupported.");
        }


        PersistentOverrideState overrides;
        if (schemaVersion is CurrentSchemaVersion
            or SchemaFiveVersion
            or SchemaFourVersion
            or SchemaThreeVersion)
        {
            PersistentOverrideDocument? document =
                JsonSerializer.Deserialize<PersistentOverrideDocument>(json, SerializerOptions);
            overrides = document?.Overrides
                ?? throw new InvalidDataException("The persistent override document is missing.");
        }
        else if (schemaVersion == SchemaTwoVersion)
        {
            SchemaTwoPersistentOverrideDocument? document =
                JsonSerializer.Deserialize<SchemaTwoPersistentOverrideDocument>(
                    json,
                    SerializerOptions);
            SchemaTwoPersistentOverrideState legacy = document?.Overrides
                ?? throw new InvalidDataException(
                    "The schema-two override document is missing.");
            overrides = new PersistentOverrideState(
                legacy.Performance,
                legacy.Fans,
                legacy.Lighting,
                legacy.CustomPerformance,
                legacy.Fans is { } fans ? FanControlSettings.FromLegacy(fans) : null);
        }
        else if (schemaVersion == LegacySchemaVersion)
        {
            LegacyPersistentOverrideDocument? document =
                JsonSerializer.Deserialize<LegacyPersistentOverrideDocument>(
                    json,
                    SerializerOptions);
            LegacyPersistentOverrideState legacy = document?.Overrides
                ?? throw new InvalidDataException("The legacy override document is missing.");
            overrides = new PersistentOverrideState(
                legacy.Performance,
                legacy.Fans,
                legacy.Lighting);
        }
        else
        {
            throw new InvalidDataException("The persistent override schema is unsupported.");
        }

        PersistentOverrideState normalized =
            NormalizeLegacyGpuClockTuning(
                NormalizeLegacyFanControl(NormalizeLegacyLighting(overrides)));
        migrated = schemaVersion != CurrentSchemaVersion || normalized != overrides;
        overrides = normalized;
        Validate(overrides);
        return overrides;
    }

    internal static PersistentOverrideState NormalizeLegacyLighting(
        PersistentOverrideState overrides) =>
        overrides.Lighting is null
            ? overrides
            : overrides with
            {
                Lighting = LegacyLightingVisibilityNormalizer.Normalize(overrides.Lighting),
            };

    internal static PersistentOverrideState NormalizeLegacyFanControl(
        PersistentOverrideState overrides) =>
        overrides.Fans is { } fans && overrides.FanControl is null
            ? overrides with { FanControl = FanControlSettings.FromLegacy(fans) }
            : overrides;

    internal static PersistentOverrideState NormalizeLegacyGpuClockTuning(
        PersistentOverrideState overrides)
    {
        if (overrides.GpuClockTuning is not null
            || overrides.Performance != PerformancePreset.Custom
            || overrides.CustomPerformance?.GpuClockOffsets is not { } legacyOffsets)
        {
            return overrides;
        }

        return overrides with
        {
            CustomPerformance = overrides.CustomPerformance with
            {
                GpuClockOffsets = null,
            },
            GpuClockTuning = new GpuClockTuningSettings(
                Enabled: true,
                legacyOffsets),
        };
    }

    private async Task WriteCoreAsync(
        PersistentOverrideState overrides,
        CancellationToken cancellationToken)
    {
        Validate(overrides);
        if (beforeWrite is not null)
        {
            await beforeWrite(cancellationToken);
        }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new PersistentOverrideDocument(CurrentSchemaVersion, overrides),
            SerializerOptions);
        await AtomicFile.WriteAsync(path, json, cancellationToken);
    }
}

internal sealed record PersistentOverrideInspection(
    PersistentOverrideLoadResult Result,
    bool WasEmpty);
