using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal sealed record CpuLivePowerBackendObservation(CpuLivePowerContext? Context, CpuPmRawSnapshot? Observation,
    CpuLivePowerFusedCap? FusedCap, CpuLivePowerCapability? Capability, CpuLivePowerTrustedRange? Range)
{
    internal CpuLivePowerRecoveryTicket? RecoveryTicket { get; init; }
}
internal interface ICpuLivePowerBackend
{
    Task<CpuLivePowerBackendObservation> ObserveAsync(CancellationToken token);
    Task<CpuLivePowerBackendObservation> ObserveAsync(CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic) => ObserveAsync(token);
    Task<CpuLivePowerBackendObservation> ObserveAsync(CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic,
        CpuLivePowerRecoveryTicket? recovery) => recovery is null ? ObserveAsync(token, diagnostic)
            : Task.FromResult(new CpuLivePowerBackendObservation(null, null, null, null, null));
}

internal enum CpuLivePowerBlockReason { None, InvalidJournal, HistoricalOwnership, ContextMismatch, ObservationUnavailable, StateChanged }
internal sealed record CpuLivePowerObservationResult(CpuLivePowerBlockReason Reason, CpuLivePowerPair? Observation)
{
    internal CpuLivePowerRecoveryTicket? RecoveryTicket { get; init; }
}

// Read-only expectations for one controller operation. No PPT setters, journal
// writers or restoration authority exist in this observer.
internal sealed class CpuLivePowerObserver(ICpuLivePowerBackend backend, ICpuLivePowerOwnershipJournalStore store,
    TimeSpan? operationTimeout = null)
{
    private readonly TimeSpan timeout = operationTimeout ?? TimeSpan.FromSeconds(10);
    internal sealed record Peer(CpuLivePowerContext Context, CpuLivePowerPair Expected, string? JournalHash)
    {
        internal CpuLivePowerPeerState State => new(Expected, null);
    }

    internal async Task<(CpuLivePowerObservationResult Result, Peer? Peer)> PreparePeerAsync(CancellationToken token)
    {
        var loaded = await LoadAsync(token).ConfigureAwait(false);
        if (JournalRejection(loaded) is { } rejected) { return (rejected, null); }
        var observed = await ObserveBackendAsync(token).ConfigureAwait(false);
        if (!Valid(observed, out var pair)) { return (new(CpuLivePowerBlockReason.ObservationUnavailable, null), null); }
        return (new(CpuLivePowerBlockReason.None, pair), new(observed!.Context!, pair!, loaded.Sha256));
    }

    internal async Task<CpuLivePowerObservationResult> ValidatePeerAsync(Peer peer, CancellationToken token)
    {
        var loaded = await LoadAsync(token).ConfigureAwait(false);
        if (JournalRejection(loaded) is { } rejected) { return rejected; }
        if (loaded.Sha256 != peer.JournalHash) { return new(CpuLivePowerBlockReason.StateChanged, null); }
        var observed = await ObserveBackendAsync(token).ConfigureAwait(false);
        if (observed?.Context is { } context && !peer.Context.MatchesHardwareContext(context))
        { return new(CpuLivePowerBlockReason.ContextMismatch, null); }
        if (!Valid(observed, out var pair)) { return new(CpuLivePowerBlockReason.ObservationUnavailable, null); }
        return new(pair == peer.Expected ? CpuLivePowerBlockReason.None : CpuLivePowerBlockReason.StateChanged, pair);
    }

    internal async Task<CpuLivePowerObservationResult> ObserveAsync(CancellationToken token,
        CpuSavedAdmissionDiagnostic? diagnostic = null, CpuLivePowerRecoveryTicket? recovery = null)
    {
        if (JournalRejection(await LoadAsync(token).ConfigureAwait(false)) is { } rejected) { return rejected; }
        var observed = await ObserveBackendAsync(token, diagnostic, recovery).ConfigureAwait(false);
        // Only explicit observation may return a terminal empty-window ticket.
        if (recovery is null && observed is { Context: null, Observation: null, RecoveryTicket: { } ticket })
        { return new(CpuLivePowerBlockReason.ObservationUnavailable, null) { RecoveryTicket = ticket }; }
        return Valid(observed, out var pair) ? new(CpuLivePowerBlockReason.None, pair)
            : new(CpuLivePowerBlockReason.ObservationUnavailable, null);
    }

    private static CpuLivePowerObservationResult? JournalRejection(CpuLivePowerJournalLoadResult loaded) =>
        loaded.Invalid ? new(CpuLivePowerBlockReason.InvalidJournal, null)
        : loaded.Journal is { State: not CpuLivePowerOwnershipState.Restored }
            ? new(CpuLivePowerBlockReason.HistoricalOwnership, null) : null;

    private async Task<CpuLivePowerJournalLoadResult> LoadAsync(CancellationToken token)
    {
        try
        {
            var loaded = await store.LoadAsync(token).ConfigureAwait(false);
            return loaded.Invalid || loaded.Journal is { IsWellFormed: false }
                || loaded.Journal is not null && loaded.Sha256 is null || loaded.Journal is null && loaded.Sha256 is not null
                ? new(null, true, null) : loaded;
        }
        catch { return new(null, true, null); }
    }

    private async Task<CpuLivePowerBackendObservation?> ObserveBackendAsync(CancellationToken token,
        CpuSavedAdmissionDiagnostic? diagnostic = null, CpuLivePowerRecoveryTicket? recovery = null)
    {
        using var stage = diagnostic?.BeginStage("power backend observation");
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var registration = cancellation.Token.Register(() =>
            diagnostic?.RecordObservationCancellation(token.IsCancellationRequested, powerObservation: true));
        cancellation.CancelAfter(timeout);
        Task<CpuLivePowerBackendObservation>? task = null;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            task = backend.ObserveAsync(cancellation.Token, diagnostic, recovery);
            var result = await task.WaitAsync(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException)
        { diagnostic?.RecordObservationCancellation(token.IsCancellationRequested, powerObservation: true); return null; }
        catch (Exception exception)
        { diagnostic?.Check(false, "power observation exception " + CpuQueryDiagnostic.FromException(CpuQueryStage.Approval, exception).Failure); return null; }
        finally
        {
            // Retain the initiating token until the actual worker terminates.
            if (task is { IsCompleted: false })
            {
                _ = task.ContinueWith(completed => { _ = completed.Exception; cancellation.Dispose(); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else { if (task is not null) { _ = task.Exception; } cancellation.Dispose(); }
        }
    }

    private static bool Valid(CpuLivePowerBackendObservation? observed, out CpuLivePowerPair? pair)
    {
        pair = CpuLivePowerSnapshot.TryCreate(observed?.Observation)?.Pair;
        return observed?.Context is { IsWellFormed: true } && pair is not null;
    }
}
