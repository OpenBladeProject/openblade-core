using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal sealed record LaptopControlReadback(bool? StartupAnimation, FunctionKeyPrimaryMode? FunctionKeyPrimary, bool? GamingMode)
{
    internal bool IsKnown(OneShotLaptopControl control) => control switch
    {
        OneShotLaptopControl.StartupAnimation => StartupAnimation.HasValue,
        OneShotLaptopControl.FunctionKeyPrimary => FunctionKeyPrimary.HasValue,
        OneShotLaptopControl.GamingMode => GamingMode.HasValue,
        _ => false,
    };

    internal bool Matches(ApplyOneShotLaptopControl requested) => requested.Control switch
    {
        OneShotLaptopControl.StartupAnimation => StartupAnimation == requested.Enabled,
        OneShotLaptopControl.FunctionKeyPrimary => FunctionKeyPrimary == requested.FunctionKeyMode,
        OneShotLaptopControl.GamingMode => GamingMode == requested.Enabled,
        _ => false,
    };
}

internal sealed record LaptopControlApplyOutcome(
    ControlOperationResult Result, LaptopControlReadback Readback, bool StopFurtherWrites,
    bool WriteAttempted = false);

internal sealed record LaptopControlBatchOutcome(
    bool FullyApplied, bool StopFurtherWrites, bool AnyConfirmedWrites, bool AnyWritesAttempted, string? Message);

internal sealed class LaptopControlExecutor
{
    private static readonly LaptopControlReadback Unknown = new(null, null, null);
    private readonly LaptopControlPendingStore? store;
    private volatile LaptopControlPendingLoad loaded;
    private sealed record KnownNonSubmission(string IntentSha256, LaptopControlPendingDocument PriorDocument);
    private KnownNonSubmission? knownNonSubmission;

    internal LaptopControlExecutor(LaptopControlPendingStore? store)
    {
        this.store = store;
        loaded = store?.Load() ?? new(null, null, null);
    }

    internal bool RequiresReconciliation => loaded.RequiresReconciliation;
    internal string PendingMessage => loaded.Failure
        ?? (loaded.Document?.Entries.Any(entry => entry.ReconciliationRequired
            && entry.Requested.Control == OneShotLaptopControl.MicrophoneMuteIndicator) == true
            ? "The microphone indicator write is unconfirmed. Its firmware state cannot be read; control writes remain paused."
            : "OpenBlade must read the pending laptop-control state before further control writes.");

    internal void Refresh() => loaded = store?.Load() ?? loaded;

    internal async Task<IReadOnlyList<LaptopControlReadback>> ReconcileAsync(
        IBladeDevice device, string? identity, CancellationToken token)
    {
        Refresh();
        var observed = new List<LaptopControlReadback>();
        if (knownNonSubmission is not null)
        {
            await ClearKnownNonSubmissionAsync(token).ConfigureAwait(false);
            // Cleanup is storage-only. Do not reinterpret or retry the command
            // in the same reconciliation operation, even if cleanup succeeded.
            return observed;
        }
        if (loaded.Failure is not null || loaded.Document is not { } document
            || !IdentityMatches(document, identity))
        {
            return observed;
        }
        var entries = document.Entries.ToList();
        foreach (LaptopControlPendingEntry pending in document.Entries.Where(entry => entry.ReconciliationRequired))
        {
            // The microphone indicator is deliberately write-only. No getter or
            // process restart can establish its outcome after possible submission.
            LaptopControlReadback readback = await ReadAsync(device, pending.Requested.Control, token).ConfigureAwait(false);
            if (!readback.IsKnown(pending.Requested.Control))
            {
                continue;
            }
            observed.Add(readback);
            entries.Remove(pending);
            if (!readback.Matches(pending.Requested))
            {
                // A definite differing value resolves uncertainty, not the saved
                // request. Retain a bounded tombstone to prevent automatic replay.
                entries.Add(pending with { ReconciliationRequired = false });
            }
        }
        if (!entries.SequenceEqual(document.Entries))
        {
            await SaveAsync(document with { Entries = [.. entries] }, token).ConfigureAwait(false);
        }
        return observed;
    }

    internal async Task<LaptopControlApplyOutcome> ExecuteAsync(
        IBladeDevice device, string? identity, ApplyOneShotLaptopControl requested,
        bool explicitRequest, Func<bool> writeAllowed, CancellationToken token)
    {
        Refresh();
        if (store is null || string.IsNullOrWhiteSpace(identity) || RequiresReconciliation)
        {
            return Blocked(store is null ? "Laptop-control recovery storage is unavailable."
                : string.IsNullOrWhiteSpace(identity) ? "The exact laptop identity is unavailable for recovery binding."
                : PendingMessage);
        }
        LaptopControlPendingDocument document = loaded.Document ?? new(1, Environment.MachineName, identity, []);
        if (document.Entries.Length > 0 && !IdentityMatches(document, identity))
        {
            return Blocked("The laptop identity does not match the retained laptop-control recovery record.");
        }
        if (!explicitRequest && document.Entries.Any(entry => entry.Requested == requested))
        {
            return new(new(ControlOperationStatus.Blocked, "The previous laptop-control request was not confirmed and will not be reapplied automatically. Use the explicit laptop control to request it again."), Unknown, false);
        }
        bool writeOnly = requested.Control == OneShotLaptopControl.MicrophoneMuteIndicator;
        if (!writeAllowed())
        {
            return Blocked("Laptop-control write authority changed before submission.");
        }
        LaptopControlPendingEntry[] retained = [.. document.Entries.Where(entry => entry.Requested.Control != requested.Control)];
        LaptopControlPendingDocument priorDocument = document;
        document = new(1, Environment.MachineName, identity,
            [.. retained, new(requested, ReconciliationRequired: true)]);
        try
        {
            if (!await SaveAsync(document, token).ConfigureAwait(false))
            {
                string failure = PendingMessage;
                await RememberNonSubmissionAsync(document, priorDocument).ConfigureAwait(false);
                return Blocked(failure);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await RememberNonSubmissionAsync(document, priorDocument).ConfigureAwait(false);
            throw;
        }
        if (!writeAllowed() || token.IsCancellationRequested)
        {
            await RememberNonSubmissionAsync(document, priorDocument).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return Blocked("Laptop-control write authority changed before submission.");
        }
        try
        {
            await device.ApplyOneShotLaptopControlAsync(requested, token).ConfigureAwait(false);
        }
        catch (CapabilityUnavailableException exception)
        {
            await RememberNonSubmissionAsync(document, priorDocument).ConfigureAwait(false);
            return Blocked(exception.Message);
        }
        catch (BladeWriteException exception) when (exception.SubmissionState == WriteSubmissionState.KnownNotApplied)
        {
            await RememberNonSubmissionAsync(document, priorDocument).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return Blocked(exception.Message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(new(ControlOperationStatus.Failed, $"The laptop-control write is unconfirmed: {exception.Message}"), Unknown, true, true);
        }

        LaptopControlReadback readback = await ReadAsync(device, requested.Control, token).ConfigureAwait(false);
        if (!writeOnly && !readback.IsKnown(requested.Control))
        {
            return new(new(ControlOperationStatus.Failed, PendingMessage), readback, true, true);
        }
        bool matches = writeOnly || readback.Matches(requested);
        LaptopControlPendingEntry[] completed = matches ? retained
            : [.. retained, new(requested, ReconciliationRequired: false)];
        if (!await SaveAsync(document with { Entries = completed }, token).ConfigureAwait(false))
        {
            return new(new(ControlOperationStatus.Failed, PendingMessage), readback, true, true);
        }
        return new(new(matches ? ControlOperationStatus.Applied : ControlOperationStatus.Failed,
            matches ? "The laptop control was applied." : "The laptop control did not match the requested setting."), readback, false, true);
    }

    internal static async Task<LaptopControlReadback> ReadAsync(
        IBladeDevice device, OneShotLaptopControl control, CancellationToken token)
    {
        try
        {
            return control switch
            {
                OneShotLaptopControl.StartupAnimation => new(await device.ReadStartupAnimationAsync(token).ConfigureAwait(false), null, null),
                OneShotLaptopControl.FunctionKeyPrimary => new(null, await device.ReadFunctionKeyPrimaryAsync(token).ConfigureAwait(false), null),
                OneShotLaptopControl.GamingMode => new(null, null, await device.ReadGamingModeAsync(token).ConfigureAwait(false)),
                _ => Unknown,
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unknown;
        }
    }

    private async Task<bool> SaveAsync(LaptopControlPendingDocument document, CancellationToken token)
    {
        try
        {
            bool saved = await store!.TrySaveAsync(loaded.Sha256, document, token).ConfigureAwait(false);
            Refresh();
            return saved;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Refresh();
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            loaded = loaded with { Failure = $"Laptop-control recovery could not be saved: {exception.Message}" };
            return false;
        }
    }

    private async Task RememberNonSubmissionAsync(
        LaptopControlPendingDocument intent, LaptopControlPendingDocument priorDocument)
    {
        // Only this live executor knows that submission never began. Bind that
        // evidence to the exact bytes we intended to write; restart deliberately
        // loses it and must reconcile the persisted record normally.
        knownNonSubmission = new(LaptopControlPendingStore.DocumentHash(intent), priorDocument);
        await ClearKnownNonSubmissionAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ClearKnownNonSubmissionAsync(CancellationToken token)
    {
        Refresh();
        KnownNonSubmission? known = knownNonSubmission;
        if (known is null)
        {
            return;
        }
        if (loaded.Failure is not null)
        {
            return;
        }
        if (!string.Equals(loaded.Sha256, known.IntentSha256, StringComparison.Ordinal))
        {
            // A replacement record belongs to another operation. Never clear it
            // using evidence about an older, even superficially similar request.
            knownNonSubmission = null;
            return;
        }
        if (await SaveAsync(known.PriorDocument, token).ConfigureAwait(false))
        {
            knownNonSubmission = null;
        }
    }

    // Protected machine-local storage supplies machine scope. Machine is
    // diagnostic provenance: renaming the PC must not prevent fresh exact-model
    // readback from resolving a pending control.
    private static bool IdentityMatches(LaptopControlPendingDocument document, string? identity) =>
        string.Equals(document.Identity, identity, StringComparison.Ordinal);

    private static LaptopControlApplyOutcome Blocked(string message) => new(new(ControlOperationStatus.Blocked, message), Unknown, true);
}
