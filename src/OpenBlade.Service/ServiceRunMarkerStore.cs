using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Service;

public enum ServiceStartupKind
{
    Normal,
    UnexpectedExitRecovery,
}

internal sealed record ServiceRunMarkerDocument(
    int SchemaVersion,
    DateTimeOffset StartedAtUtc,
    TimeSpan UptimeAtStart)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RunId { get; init; }
}

public sealed class ServiceRunMarkerStore
{
    private const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            WriteIndented = true,
        };

    private readonly string path;
    private readonly Func<DateTimeOffset> getUtcNow;
    private readonly Func<TimeSpan> getSystemUptime;
    private readonly Func<string, CancellationToken, IDisposable>? acquireVerifiedWriterLease;
    private readonly Action? requireRetiredBackend;
    private readonly SemaphoreSlim gate = new(1, 1);
    private byte[]? ownedMarker;

    public ServiceRunMarkerStore(string path)
        : this(
            path,
            () => TimeProvider.System.GetUtcNow(),
            () => TimeSpan.FromMilliseconds(Environment.TickCount64))
    {
    }

    internal ServiceRunMarkerStore(
        string path,
        Func<DateTimeOffset> getUtcNow,
        Func<TimeSpan> getSystemUptime,
        Func<string, CancellationToken, IDisposable>? acquireVerifiedWriterLease = null,
        Action? requireRetiredBackend = null)
    {
        this.path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A service run-marker path is required.", nameof(path))
            : Path.GetFullPath(path);
        this.getUtcNow = getUtcNow
            ?? throw new ArgumentNullException(nameof(getUtcNow));
        this.getSystemUptime = getSystemUptime
            ?? throw new ArgumentNullException(nameof(getSystemUptime));
        if (requireRetiredBackend is not null && acquireVerifiedWriterLease is null)
        {
            throw new ArgumentException("Backend transition requires a verified writer lease.",
                nameof(requireRetiredBackend));
        }
        this.acquireVerifiedWriterLease = acquireVerifiedWriterLease;
        this.requireRetiredBackend = requireRetiredBackend;
    }

    internal bool RequiresBackendRetirement => requireRetiredBackend is not null;

    public async Task<ServiceStartupKind> BeginRunAsync(CancellationToken cancellationToken)
    {
        var (kind, _) = await BeginRunCoreAsync<object>(null, null, cancellationToken).ConfigureAwait(false);
        return kind;
    }

    internal Task<(ServiceStartupKind Kind, T? Configuration)> BeginTransitionAsync<T>(
        MachineConfigurationWriterBinding binding,
        Func<UserConfigurationWriterScope, CancellationToken, Task<T?>> loadConfiguration,
        CancellationToken cancellationToken) where T : class
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path)!);
        if (requireRetiredBackend is null || acquireVerifiedWriterLease is null
            || !binding.Matches(root, acquireVerifiedWriterLease)
            || !string.Equals(path, binding.RunMarkerPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Machine startup and run marker require the same enrolled writer.");
        }

        return BeginRunCoreAsync(binding, loadConfiguration, cancellationToken);
    }

    private async Task<(ServiceStartupKind Kind, T? Configuration)> BeginRunCoreAsync<T>(
        MachineConfigurationWriterBinding? binding,
        Func<UserConfigurationWriterScope, CancellationToken, Task<T?>>? loadConfiguration,
        CancellationToken cancellationToken) where T : class
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ServiceStartupKind startupKind;
            byte[] json;
            T? configuration = null;
            using (IDisposable? lease = binding is null ? AcquireWriterLease(cancellationToken) : null)
            await using (UserConfigurationWriterScope? scope = binding?.Acquire(cancellationToken))
            {
                bool hasLease = lease is not null || scope is not null;
                if (hasLease)
                {
                    if (ownedMarker is not null)
                    {
                        throw new InvalidOperationException("This store already owns a service run marker.");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    RequireAbsent(path + ".tmp");
                }

                DateTimeOffset startedAtUtc = getUtcNow();
                TimeSpan uptimeAtStart = getSystemUptime();
                if (requireRetiredBackend is null)
                {
                    startupKind = await ClassifyExistingMarkerAsync(
                        startedAtUtc,
                        uptimeAtStart,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // The caller verifies durable retirement for the exact outgoing
                    // generation. The writer lease only excludes concurrent writers.
                    requireRetiredBackend();
                    RequireAbsent(path);
                    cancellationToken.ThrowIfCancellationRequested();
                    startupKind = ServiceStartupKind.Normal;
                }

                if (scope is not null)
                {
                    // Keep proof, configuration writes and marker publication under
                    // the same native exclusion. Stores enter the scope individually.
                    BatteryConfigurationStore.CheckRecoveryRequired(binding!.MarkerPath);
                    configuration = await loadConfiguration!(scope, cancellationToken).ConfigureAwait(false);
                    if (configuration is null)
                    {
                        return (startupKind, null);
                    }
                }

                json = JsonSerializer.SerializeToUtf8Bytes(
                    new ServiceRunMarkerDocument(
                        CurrentSchemaVersion,
                        startedAtUtc,
                        uptimeAtStart)
                    {
                        RunId = hasLease ? Guid.NewGuid() : null,
                    },
                    SerializerOptions);
                await AtomicFile.WriteAsync(path, json, cancellationToken).ConfigureAwait(false);
            }

            if (acquireVerifiedWriterLease is not null)
            {
                ownedMarker = json;
            }
            return (startupKind, configuration);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ServiceStartupKind> ClassifyExistingMarkerAsync(
        DateTimeOffset nowUtc,
        TimeSpan currentUptime,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return ServiceStartupKind.Normal;
        }

        try
        {
            byte[] json = await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
            ServiceRunMarkerDocument? marker =
                JsonSerializer.Deserialize<ServiceRunMarkerDocument>(
                    json,
                    SerializerOptions);
            if (marker is null
                || marker.SchemaVersion != CurrentSchemaVersion
                || marker.StartedAtUtc == default
                || marker.UptimeAtStart < TimeSpan.Zero
                || currentUptime < TimeSpan.Zero)
            {
                return ServiceStartupKind.UnexpectedExitRecovery;
            }

            DateTimeOffset currentBootStartedAtUtc = nowUtc - currentUptime;
            bool uptimeProvesRestart = currentUptime < marker.UptimeAtStart;
            bool wallClockSupportsRestart =
                marker.StartedAtUtc < currentBootStartedAtUtc;
            return uptimeProvesRestart && wallClockSupportsRestart
                ? ServiceStartupKind.Normal
                : ServiceStartupKind.UnexpectedExitRecovery;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // An unreadable or implausible marker cannot prove that the prior
            // service instance belonged to an earlier boot. Fail closed.
            return ServiceStartupKind.UnexpectedExitRecovery;
        }
    }

    public async Task CompleteRunAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using IDisposable? lease = AcquireWriterLease(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (lease is not null)
            {
                if (ownedMarker is null)
                {
                    throw new InvalidOperationException("This store has no confirmed run marker to clear.");
                }

                RequireAbsent(path + ".tmp");
                byte[] currentMarker = await File.ReadAllBytesAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                if (!currentMarker.AsSpan().SequenceEqual(ownedMarker))
                {
                    throw new InvalidOperationException("The service run marker no longer belongs to this run.");
                }
                cancellationToken.ThrowIfCancellationRequested();
            }

            File.Delete(path);
            ownedMarker = null;
        }
        finally
        {
            gate.Release();
        }
    }

    private IDisposable? AcquireWriterLease(CancellationToken cancellationToken)
    {
        if (acquireVerifiedWriterLease is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return acquireVerifiedWriterLease(Path.GetDirectoryName(path)!, cancellationToken)
            ?? throw new InvalidOperationException("A verified machine writer lease is required.");
    }

    private static void RequireAbsent(string evidencePath)
    {
        try
        {
            _ = File.GetAttributes(evidencePath);
        }
        catch (FileNotFoundException)
        {
            return;
        }

        throw new InvalidOperationException("Uncleared service run-marker evidence must be preserved.");
    }
}
