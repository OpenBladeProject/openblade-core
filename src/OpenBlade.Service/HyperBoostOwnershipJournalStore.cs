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

internal sealed record HyperBoostOwnershipJournal(
    HyperBoostOwnershipJournalState State,
    PerformancePreset BaseMode,
    DateTimeOffset UpdatedAtUtc)
{
    internal bool IsWellFormed =>
        Enum.IsDefined(State)
        && BaseMode is PerformancePreset.Silent
            or PerformancePreset.Balanced
            or PerformancePreset.Performance
        && UpdatedAtUtc != default;
}

internal readonly record struct HyperBoostOwnershipJournalLoadResult(
    HyperBoostOwnershipJournal? Journal,
    bool Invalid);

public interface IServiceOwnershipJournalCleaner
{
    Task ClearAsync(CancellationToken cancellationToken);
}

internal sealed record HyperBoostOwnershipJournalDocument(
    int SchemaVersion,
    HyperBoostOwnershipJournalState State,
    PerformancePreset BaseMode,
    DateTimeOffset UpdatedAtUtc);

internal interface IHyperBoostOwnershipJournalStore
    : IServiceOwnershipJournalCleaner
{
    Task<HyperBoostOwnershipJournalLoadResult> LoadAsync(
        CancellationToken cancellationToken);

    Task WriteAsync(
        HyperBoostOwnershipJournal journal,
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

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal sealed class HyperBoostOwnershipJournalStore : IHyperBoostOwnershipJournalStore
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
                var journal = document is null
                    ? null
                    : new HyperBoostOwnershipJournal(
                        document.State,
                        document.BaseMode,
                        document.UpdatedAtUtc);
                return document is not { SchemaVersion: CurrentSchemaVersion }
                    || journal is not { IsWellFormed: true }
                    ? new HyperBoostOwnershipJournalLoadResult(null, true)
                    : new HyperBoostOwnershipJournalLoadResult(journal, false);
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
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                new HyperBoostOwnershipJournalDocument(
                    CurrentSchemaVersion,
                    journal.State,
                    journal.BaseMode,
                    journal.UpdatedAtUtc),
                SerializerOptions);
            await AtomicFile.WriteAsync(path, json, cancellationToken)
                .ConfigureAwait(false);
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
}
