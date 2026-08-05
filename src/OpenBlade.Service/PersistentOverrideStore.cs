using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed record PersistentOverrideState(
    PerformancePreset? Performance,
    FanMode? Fans,
    LightingSettings? Lighting,
    CustomPerformanceSettings? CustomPerformance = null,
    FanControlSettings? FanControl = null)
{
    public static PersistentOverrideState Empty { get; } = new(null, null, null);
}

public sealed record PersistentOverrideLoadResult(
    PersistentOverrideState Overrides,
    bool Recovered);

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
    private const int CurrentSchemaVersion = 5;
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
    private readonly SemaphoreSlim gate = new(1, 1);

    public PersistentOverrideStore(string path)
        : this(path, beforeWrite: null)
    {
    }

    internal PersistentOverrideStore(
        string path,
        Func<CancellationToken, Task>? beforeWrite)
    {
        this.path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("An override path is required.", nameof(path))
            : path;
        this.beforeWrite = beforeWrite;
    }

    public async Task<PersistentOverrideLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
            {
                return new PersistentOverrideLoadResult(
                    PersistentOverrideState.Empty,
                    Recovered: false);
            }

            try
            {
                byte[] json = await File.ReadAllBytesAsync(path, cancellationToken);
                PersistentOverrideState overrides = Deserialize(json, out bool migrated);
                if (migrated)
                {
                    await WriteCoreAsync(overrides, cancellationToken);
                }
                return new PersistentOverrideLoadResult(overrides, Recovered: false);
            }
            catch (Exception exception)
                when (BatteryConfigurationStore.IsRecoverable(exception))
            {
                await WriteCoreAsync(PersistentOverrideState.Empty, cancellationToken);
                return new PersistentOverrideLoadResult(
                    PersistentOverrideState.Empty,
                    Recovered: true);
            }
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
        overrides = NormalizeLegacyFanControl(NormalizeLegacyLighting(overrides));
        Validate(overrides);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await WriteCoreAsync(overrides, cancellationToken);
        }
        finally
        {
            gate.Release();
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
        if (schemaVersion is CurrentSchemaVersion or SchemaFourVersion or SchemaThreeVersion)
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
            NormalizeLegacyFanControl(NormalizeLegacyLighting(overrides));
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
