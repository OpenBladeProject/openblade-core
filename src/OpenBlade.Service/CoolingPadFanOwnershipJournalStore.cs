using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal enum CoolingPadFanOwnershipJournalState
{
    AcquirePending,
    OwnedConfirmed,
    UpdatePending,
    HandbackPending,
    Indeterminate,
}

internal sealed record CoolingPadFanOwnershipJournal(
    CoolingPadFanOwnershipJournalState State,
    CoolingPadFanTarget Target,
    DateTimeOffset UpdatedAtUtc)
{
    internal bool IsWellFormed =>
        Enum.IsDefined(State)
        && Target.Rpm is >= CoolingPadFanTarget.MinimumRpm
            and <= CoolingPadFanTarget.MaximumRpm
        && Target.Rpm % CoolingPadFanTarget.StepRpm == 0
        && UpdatedAtUtc != default;
}

internal readonly record struct CoolingPadFanOwnershipJournalLoadResult(
    CoolingPadFanOwnershipJournal? Journal,
    bool Invalid);

internal sealed record CoolingPadFanOwnershipJournalDocument(
    int SchemaVersion,
    CoolingPadFanOwnershipJournalState State,
    int TargetRpm,
    DateTimeOffset UpdatedAtUtc);

internal interface ICoolingPadFanOwnershipJournalStore
    : IServiceOwnershipJournalCleaner
{
    Task<CoolingPadFanOwnershipJournalLoadResult> LoadAsync(
        CancellationToken cancellationToken);

    Task WriteAsync(
        CoolingPadFanOwnershipJournal journal,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableCoolingPadFanOwnershipJournalStore
    : ICoolingPadFanOwnershipJournalStore
{
    internal static UnavailableCoolingPadFanOwnershipJournalStore Instance { get; } =
        new();

    private UnavailableCoolingPadFanOwnershipJournalStore()
    {
    }

    public Task<CoolingPadFanOwnershipJournalLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new CoolingPadFanOwnershipJournalLoadResult(null, false));
    }

    public Task WriteAsync(
        CoolingPadFanOwnershipJournal journal,
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

internal sealed class CoolingPadFanOwnershipJournalStore
    : ICoolingPadFanOwnershipJournalStore
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

    internal CoolingPadFanOwnershipJournalStore(string path)
    {
        this.path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException(
                "A cooling-pad fan ownership-journal path is required.",
                nameof(path))
            : Path.GetFullPath(path);
    }

    public async Task<CoolingPadFanOwnershipJournalLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return new CoolingPadFanOwnershipJournalLoadResult(null, false);
            }

            try
            {
                byte[] json = await File.ReadAllBytesAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                CoolingPadFanOwnershipJournalDocument? document =
                    JsonSerializer.Deserialize<CoolingPadFanOwnershipJournalDocument>(
                        json,
                        SerializerOptions);
                CoolingPadFanOwnershipJournal? journal = document is null
                    ? null
                    : new CoolingPadFanOwnershipJournal(
                        document.State,
                        new CoolingPadFanTarget(document.TargetRpm),
                        document.UpdatedAtUtc);
                return document is not { SchemaVersion: CurrentSchemaVersion }
                    || journal is not { IsWellFormed: true }
                    ? new CoolingPadFanOwnershipJournalLoadResult(null, true)
                    : new CoolingPadFanOwnershipJournalLoadResult(journal, false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new CoolingPadFanOwnershipJournalLoadResult(null, true);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task WriteAsync(
        CoolingPadFanOwnershipJournal journal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (!journal.IsWellFormed)
        {
            throw new ArgumentException(
                "The cooling-pad fan ownership journal is invalid.",
                nameof(journal));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                new CoolingPadFanOwnershipJournalDocument(
                    CurrentSchemaVersion,
                    journal.State,
                    journal.Target.Rpm,
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

internal sealed class CompositeServiceOwnershipJournalCleaner(
    IReadOnlyList<IServiceOwnershipJournalCleaner> cleaners)
    : IServiceOwnershipJournalCleaner
{
    private readonly IReadOnlyList<IServiceOwnershipJournalCleaner> cleaners =
        cleaners is { Count: > 0 }
            ? cleaners
            : throw new ArgumentException(
                "At least one ownership-journal cleaner is required.",
                nameof(cleaners));

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        foreach (IServiceOwnershipJournalCleaner cleaner in cleaners)
        {
            await cleaner.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
