using System.Text.Json;

namespace OpenBlade.Service;

public enum ServiceStartupKind
{
    Normal,
    UnexpectedExitRecovery,
}

internal sealed record ServiceRunMarkerDocument(
    int SchemaVersion,
    DateTimeOffset StartedAtUtc);

public sealed class ServiceRunMarkerStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            WriteIndented = true,
        };

    private readonly string path;
    private readonly Func<DateTimeOffset> getUtcNow;
    private readonly Func<TimeSpan> getSystemUptime;
    private readonly SemaphoreSlim gate = new(1, 1);

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
        Func<TimeSpan> getSystemUptime)
    {
        this.path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A service run-marker path is required.", nameof(path))
            : Path.GetFullPath(path);
        this.getUtcNow = getUtcNow
            ?? throw new ArgumentNullException(nameof(getUtcNow));
        this.getSystemUptime = getSystemUptime
            ?? throw new ArgumentNullException(nameof(getSystemUptime));
    }

    public async Task<ServiceStartupKind> BeginRunAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset startedAtUtc = getUtcNow();
            ServiceStartupKind startupKind = await ClassifyExistingMarkerAsync(
                startedAtUtc,
                cancellationToken).ConfigureAwait(false);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                new ServiceRunMarkerDocument(
                    CurrentSchemaVersion,
                    startedAtUtc),
                SerializerOptions);
            await AtomicFile.WriteAsync(path, json, cancellationToken)
                .ConfigureAwait(false);
            return startupKind;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ServiceStartupKind> ClassifyExistingMarkerAsync(
        DateTimeOffset nowUtc,
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
                || marker.StartedAtUtc == default)
            {
                return ServiceStartupKind.UnexpectedExitRecovery;
            }

            TimeSpan uptime = getSystemUptime();
            if (uptime < TimeSpan.Zero)
            {
                return ServiceStartupKind.UnexpectedExitRecovery;
            }

            DateTimeOffset currentBootStartedAtUtc = nowUtc - uptime;
            return marker.StartedAtUtc < currentBootStartedAtUtc
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
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
        }
        finally
        {
            gate.Release();
        }
    }
}
