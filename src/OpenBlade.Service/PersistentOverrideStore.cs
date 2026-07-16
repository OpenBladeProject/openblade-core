using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed record PersistentOverrideState(
    PerformancePreset? Performance,
    FanMode? Fans,
    LightingSettings? Lighting)
{
    public static PersistentOverrideState Empty { get; } = new(null, null, null);
}

public sealed record PersistentOverrideLoadResult(
    PersistentOverrideState Overrides,
    bool Recovered);

internal sealed record PersistentOverrideDocument(
    int SchemaVersion,
    PersistentOverrideState Overrides);

public sealed class PersistentOverrideStore
{
    private const int CurrentSchemaVersion = 1;
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
                PersistentOverrideState overrides = Deserialize(json);
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

        if (overrides.Fans is { } fans && !Enum.IsDefined(fans))
        {
            throw new InvalidDataException("The persistent fan override is undefined.");
        }

        if (overrides.Lighting is { } lighting
            && (!Enum.IsDefined(lighting.Effect) || lighting.Brightness > 100))
        {
            throw new InvalidDataException("The persistent lighting override is invalid.");
        }
    }

    private static PersistentOverrideState Deserialize(ReadOnlySpan<byte> json)
    {
        BatteryConfigurationStore.RejectDuplicateMembers(json);
        PersistentOverrideDocument? document = JsonSerializer.Deserialize<PersistentOverrideDocument>(
            json,
            SerializerOptions);
        if (document is null || document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("The persistent override schema is unsupported.");
        }

        Validate(document.Overrides);
        return document.Overrides;
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
