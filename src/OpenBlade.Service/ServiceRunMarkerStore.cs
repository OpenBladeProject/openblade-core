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
    private readonly SemaphoreSlim gate = new(1, 1);

    public ServiceRunMarkerStore(string path)
    {
        this.path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A service run-marker path is required.", nameof(path))
            : Path.GetFullPath(path);
    }

    public async Task<ServiceStartupKind> BeginRunAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool previousRunWasInterrupted = File.Exists(path);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                new ServiceRunMarkerDocument(
                    CurrentSchemaVersion,
                    DateTimeOffset.UtcNow),
                SerializerOptions);
            await AtomicFile.WriteAsync(path, json, cancellationToken)
                .ConfigureAwait(false);
            return previousRunWasInterrupted
                ? ServiceStartupKind.UnexpectedExitRecovery
                : ServiceStartupKind.Normal;
        }
        finally
        {
            gate.Release();
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
