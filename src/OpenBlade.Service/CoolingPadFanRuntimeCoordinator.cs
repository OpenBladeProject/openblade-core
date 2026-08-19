using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal readonly record struct CoolingPadFanRuntimeSnapshot(
    DeviceFeatureAccess Access,
    CoolingPadControlOwnershipState Ownership,
    CoolingDemandLevel? AppliedDemand,
    CoolingPadFanTarget? AppliedTarget,
    string Reason)
{
    internal bool IsWellFormed =>
        Enum.IsDefined(Access)
        && Enum.IsDefined(Ownership)
        && !string.IsNullOrWhiteSpace(Reason)
        && (Ownership == CoolingPadControlOwnershipState.Owned
            ? Access == DeviceFeatureAccess.ReadWrite
                && AppliedDemand is { } demand
                && AppliedTarget == CoolingPadFanTarget.FromDemand(demand)
            : AppliedDemand is null && AppliedTarget is null)
        && (Access == DeviceFeatureAccess.ReadWrite
            || Ownership == CoolingPadControlOwnershipState.Unowned);
}

internal enum CoolingPadFanCrashRecoveryStatus
{
    None,
    Recovered,
    Blocked,
}

internal readonly record struct CoolingPadFanCrashRecoveryResult(
    CoolingPadFanCrashRecoveryStatus Status,
    string Message);

internal enum CoolingPadFanRetryOperation
{
    Acquire,
    Update,
    Handback,
    LifecycleHandback,
}

internal readonly record struct CoolingPadFanRetryLatch(
    CoolingPadFanRetryOperation Operation,
    CoolingPadFanTarget? Target);

internal interface ICoolingPadFanRuntimeCoordinator
{
    CoolingPadFanRuntimeSnapshot Snapshot { get; }

    Task ReconcileAsync(
        CoolingDemandLevel? demand,
        string reason,
        CancellationToken cancellationToken);

    Task HandbackAsync(
        string reason,
        CancellationToken cancellationToken);

    Task HandbackForLifecycleAsync(
        string reason,
        CancellationToken cancellationToken) =>
        HandbackAsync(reason, cancellationToken);

    Task InvalidateConnectionAsync(
        string reason,
        CancellationToken cancellationToken);

    Task<CoolingPadFanCrashRecoveryResult> RecoverAfterUnexpectedExitAsync(
        CancellationToken cancellationToken);
}

internal sealed class UnavailableCoolingPadFanRuntimeCoordinator
    : ICoolingPadFanRuntimeCoordinator
{
    private const string UnavailableReason =
        "The production cooling-pad fan backend is unavailable; no fan write was attempted.";

    internal static UnavailableCoolingPadFanRuntimeCoordinator Instance { get; } = new();

    private UnavailableCoolingPadFanRuntimeCoordinator()
    {
    }

    public CoolingPadFanRuntimeSnapshot Snapshot { get; } = new(
        DeviceFeatureAccess.ObservedOnly,
        CoolingPadControlOwnershipState.Unowned,
        AppliedDemand: null,
        AppliedTarget: null,
        UnavailableReason);

    public Task ReconcileAsync(
        CoolingDemandLevel? demand,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task HandbackAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task HandbackForLifecycleAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task InvalidateConnectionAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<CoolingPadFanCrashRecoveryResult> RecoverAfterUnexpectedExitAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CoolingPadFanCrashRecoveryResult(
            CoolingPadFanCrashRecoveryStatus.None,
            "No cooling-pad fan ownership journal is active."));
    }
}

/// <summary>
/// Service-side semantic orchestration for an already admitted backend. The
/// Windows composition deliberately does not construct this type while the
/// production backend gate remains closed.
/// </summary>
internal sealed class AdmittedCoolingPadFanRuntimeCoordinator
    : ICoolingPadFanRuntimeCoordinator
{
    private readonly CoolingPadFanOwnershipCoordinator ownershipCoordinator;
    private readonly ICoolingPadFanOwnershipJournalStore journalStore;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object snapshotGate = new();
    private CoolingPadFanRetryLatch? retryLatch;
    private CoolingPadFanRetryLatch? exhaustionRetry;
    private CoolingPadFanRuntimeSnapshot snapshot = new(
        DeviceFeatureAccess.ReadWrite,
        CoolingPadControlOwnershipState.Unowned,
        AppliedDemand: null,
        AppliedTarget: null,
        "No OpenBlade cooling-pad fan write has succeeded in this service process.");

    private AdmittedCoolingPadFanRuntimeCoordinator(
        ICoolingPadFanSemanticBackend backend,
        ICoolingPadFanOwnershipJournalStore? journalStore = null,
        TimeProvider? timeProvider = null)
    {
        ownershipCoordinator = new CoolingPadFanOwnershipCoordinator(
            backend ?? throw new ArgumentNullException(nameof(backend)));
        this.journalStore = journalStore
            ?? UnavailableCoolingPadFanOwnershipJournalStore.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal static AdmittedCoolingPadFanRuntimeCoordinator CreateForTesting(
        ICoolingPadFanSemanticBackend backend,
        ICoolingPadFanOwnershipJournalStore? journalStore = null,
        TimeProvider? timeProvider = null) => new(backend, journalStore, timeProvider);

    internal static AdmittedCoolingPadFanRuntimeCoordinator CreateForProduction(
        ICoolingPadFanSemanticBackend backend,
        ICoolingPadFanOwnershipJournalStore journalStore)
    {
        CoolingPadFanControlEvidenceGates evidence =
            CoolingPadFanControlEvidenceGates.Current;
        CoolingPadFanControlImplementationGates implementation =
            CoolingPadFanControlImplementationGates.Current;
        if (!evidence.WriteAdmitted || !implementation.WriteAvailable)
        {
            throw new InvalidOperationException(
                "Production cooling-pad fan orchestration is not admitted by the current evidence and implementation gates.");
        }

        return new AdmittedCoolingPadFanRuntimeCoordinator(
            backend,
            journalStore ?? throw new ArgumentNullException(nameof(journalStore)),
            TimeProvider.System);
    }

    public CoolingPadFanRuntimeSnapshot Snapshot
    {
        get
        {
            lock (snapshotGate)
            {
                return snapshot;
            }
        }
    }

    public async Task ReconcileAsync(
        CoolingDemandLevel? demand,
        string reason,
        CancellationToken cancellationToken)
    {
        ValidateReason(reason);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (demand is null)
            {
                await HandbackCoreAsync(
                    reason,
                    lifecycleCleanup: false,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            CoolingPadFanTarget target = CoolingPadFanTarget.FromDemand(demand.Value);
            CoolingPadFanOwnershipSnapshot before = ownershipCoordinator.Snapshot;
            if (before is
                {
                    Ownership: CoolingPadControlOwnershipState.Owned,
                    AppliedTarget: { } applied,
                }
                && applied == target)
            {
                SetSnapshot(Snapshot with
                {
                    AppliedDemand = demand,
                    Reason = reason,
                });
                return;
            }

            CoolingPadFanRetryOperation operation =
                before.Ownership == CoolingPadControlOwnershipState.Owned
                    ? CoolingPadFanRetryOperation.Update
                    : CoolingPadFanRetryOperation.Acquire;
            var requested = new CoolingPadFanRetryLatch(operation, target);
            if (retryLatch == requested)
            {
                SetSnapshot(Snapshot with
                {
                    Reason =
                        $"{reason} The previous definitively-not-applied operation was not retried; change the intent or reconnect the pad to re-arm it.",
                });
                return;
            }
            bool retryingAfterExhaustion = exhaustionRetry == requested;
            retryLatch = null;
            exhaustionRetry = null;

            try
            {
                await WriteJournalAsync(
                        before.Ownership == CoolingPadControlOwnershipState.Owned
                            ? CoolingPadFanOwnershipJournalState.UpdatePending
                            : CoolingPadFanOwnershipJournalState.AcquirePending,
                        target,
                        cancellationToken)
                    .ConfigureAwait(false);
                await ownershipCoordinator.ApplyTargetAsync(target, cancellationToken)
                    .ConfigureAwait(false);

                SetSnapshot(new CoolingPadFanRuntimeSnapshot(
                    DeviceFeatureAccess.ReadWrite,
                    CoolingPadControlOwnershipState.Owned,
                    demand,
                    target,
                    reason));
                await WriteJournalAsync(
                        CoolingPadFanOwnershipJournalState.OwnedConfirmed,
                        target,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RecordRetryFailure(
                    requested,
                    retryingAfterExhaustion,
                    exception);
                await RecordWriteFailureAsync(before, target, exception)
                    .ConfigureAwait(false);
                PublishFailure(reason, exception);
                throw;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task HandbackAsync(
        string reason,
        CancellationToken cancellationToken)
        => await HandbackAsync(
            reason,
            lifecycleCleanup: false,
            cancellationToken).ConfigureAwait(false);

    public async Task HandbackForLifecycleAsync(
        string reason,
        CancellationToken cancellationToken) => await HandbackAsync(
            reason,
            lifecycleCleanup: true,
            cancellationToken).ConfigureAwait(false);

    private async Task HandbackAsync(
        string reason,
        bool lifecycleCleanup,
        CancellationToken cancellationToken)
    {
        ValidateReason(reason);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await HandbackCoreAsync(
                reason,
                lifecycleCleanup,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task InvalidateConnectionAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        ValidateReason(reason);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            retryLatch = null;
            exhaustionRetry = null;
            CoolingPadFanOwnershipSnapshot before = ownershipCoordinator.Snapshot;
            if (before is
                {
                    Ownership: CoolingPadControlOwnershipState.Owned,
                    AppliedTarget: { } target,
                })
            {
                await WriteJournalAsync(
                        CoolingPadFanOwnershipJournalState.Indeterminate,
                        target,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            await ownershipCoordinator.InvalidateConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            CoolingPadFanOwnershipSnapshot ownership = ownershipCoordinator.Snapshot;
            SetSnapshot(new CoolingPadFanRuntimeSnapshot(
                DeviceFeatureAccess.ReadWrite,
                ownership.Ownership,
                AppliedDemand: null,
                AppliedTarget: null,
                reason));
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<CoolingPadFanCrashRecoveryResult>
        RecoverAfterUnexpectedExitAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CoolingPadFanOwnershipJournalLoadResult loaded =
                await journalStore.LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (loaded.Invalid)
            {
                return BlockCrashRecovery(
                    "The cooling-pad fan ownership journal is invalid. No automatic recovery write was issued.");
            }
            if (loaded.Journal is not { } journal)
            {
                return new CoolingPadFanCrashRecoveryResult(
                    CoolingPadFanCrashRecoveryStatus.None,
                    "No cooling-pad fan ownership journal was found.");
            }
            if (journal.State != CoolingPadFanOwnershipJournalState.OwnedConfirmed)
            {
                ownershipCoordinator.RestoreIndeterminateOwnership();
                SetSnapshot(new CoolingPadFanRuntimeSnapshot(
                    DeviceFeatureAccess.ReadWrite,
                    CoolingPadControlOwnershipState.Indeterminate,
                    AppliedDemand: null,
                    AppliedTarget: null,
                    "Cooling-pad fan ownership was pending or indeterminate at the unexpected exit."));
                return new CoolingPadFanCrashRecoveryResult(
                    CoolingPadFanCrashRecoveryStatus.Blocked,
                    "Cooling-pad fan ownership was not durably confirmed before the unexpected exit. No automatic recovery write was issued.");
            }

            ownershipCoordinator.RestoreConfirmedOwnership(journal.Target);
            SetSnapshot(new CoolingPadFanRuntimeSnapshot(
                DeviceFeatureAccess.ReadWrite,
                CoolingPadControlOwnershipState.Owned,
                journal.Target.ToDemand(),
                journal.Target,
                "Recovered confirmed cooling-pad fan ownership from the machine journal."));
            try
            {
                await HandbackCoreAsync(
                        "Cooling-pad fan control was handed back after an unexpected service exit.",
                        lifecycleCleanup: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new CoolingPadFanCrashRecoveryResult(
                    CoolingPadFanCrashRecoveryStatus.Recovered,
                    "OpenBlade issued one confirmed cooling-pad fan handback after the unexpected exit.");
            }
            catch (Exception exception)
            {
                return new CoolingPadFanCrashRecoveryResult(
                    CoolingPadFanCrashRecoveryStatus.Blocked,
                    $"Cooling-pad fan crash recovery did not complete and was not retried: {exception.Message}");
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task HandbackCoreAsync(
        string reason,
        bool lifecycleCleanup,
        CancellationToken cancellationToken)
    {
        CoolingPadFanOwnershipSnapshot before = ownershipCoordinator.Snapshot;
        if (before.Ownership == CoolingPadControlOwnershipState.Unowned)
        {
            retryLatch = null;
            exhaustionRetry = null;
            await journalStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            SetSnapshot(new CoolingPadFanRuntimeSnapshot(
                DeviceFeatureAccess.ReadWrite,
                CoolingPadControlOwnershipState.Unowned,
                AppliedDemand: null,
                AppliedTarget: null,
                reason));
            return;
        }
        if (before is not
            {
                Ownership: CoolingPadControlOwnershipState.Owned,
                AppliedTarget: { } ownedTarget,
            })
        {
            throw new InvalidOperationException(
                "Cooling-pad ownership is indeterminate; handback is not safe.");
        }

        CoolingPadFanRetryOperation operation = lifecycleCleanup
            ? CoolingPadFanRetryOperation.LifecycleHandback
            : CoolingPadFanRetryOperation.Handback;
        var requested = new CoolingPadFanRetryLatch(operation, ownedTarget);
        if (retryLatch == requested)
        {
            SetSnapshot(Snapshot with
            {
                Reason =
                    $"{reason} The previous definitively-not-applied handback was not retried; a reviewed lifecycle or connection transition must re-arm it.",
            });
            return;
        }
        bool retryingAfterExhaustion = exhaustionRetry == requested;
        retryLatch = null;
        exhaustionRetry = null;

        try
        {
            await WriteJournalAsync(
                    CoolingPadFanOwnershipJournalState.HandbackPending,
                    ownedTarget,
                    cancellationToken)
                .ConfigureAwait(false);
            if (lifecycleCleanup)
            {
                await ownershipCoordinator.HandbackForLifecycleAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ownershipCoordinator.HandbackAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            SetSnapshot(new CoolingPadFanRuntimeSnapshot(
                DeviceFeatureAccess.ReadWrite,
                CoolingPadControlOwnershipState.Unowned,
                AppliedDemand: null,
                AppliedTarget: null,
                reason));
            await journalStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RecordRetryFailure(
                requested,
                retryingAfterExhaustion,
                exception);
            await RecordWriteFailureAsync(before, ownedTarget, exception)
                .ConfigureAwait(false);
            PublishFailure(reason, exception);
            throw;
        }
    }

    private CoolingPadFanCrashRecoveryResult BlockCrashRecovery(string message)
    {
        ownershipCoordinator.RestoreIndeterminateOwnership();
        SetSnapshot(new CoolingPadFanRuntimeSnapshot(
            DeviceFeatureAccess.ReadWrite,
            CoolingPadControlOwnershipState.Indeterminate,
            AppliedDemand: null,
            AppliedTarget: null,
            message));
        return new CoolingPadFanCrashRecoveryResult(
            CoolingPadFanCrashRecoveryStatus.Blocked,
            message);
    }

    private Task WriteJournalAsync(
        CoolingPadFanOwnershipJournalState state,
        CoolingPadFanTarget target,
        CancellationToken cancellationToken) => journalStore.WriteAsync(
        new CoolingPadFanOwnershipJournal(
            state,
            target,
            timeProvider.GetUtcNow()),
        cancellationToken);

    private async Task RecordWriteFailureAsync(
        CoolingPadFanOwnershipSnapshot before,
        CoolingPadFanTarget attemptedTarget,
        Exception exception)
    {
        try
        {
            if (IsIndeterminate(exception))
            {
                await WriteJournalAsync(
                        CoolingPadFanOwnershipJournalState.Indeterminate,
                        attemptedTarget,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else if (exception is BladeWriteException
            {
                SubmissionState: WriteSubmissionState.KnownNotApplied,
            }
                     && before is
                     {
                         Ownership: CoolingPadControlOwnershipState.Owned,
                         AppliedTarget: { } previousTarget,
                     })
            {
                await WriteJournalAsync(
                        CoolingPadFanOwnershipJournalState.OwnedConfirmed,
                        previousTarget,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else if (exception is BladeWriteException
            {
                SubmissionState: WriteSubmissionState.KnownNotApplied,
            })
            {
                await journalStore.ClearAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Preserve the hardware failure. A pending journal is deliberately
            // fail-closed during the next unexpected-exit recovery.
        }
    }

    private static bool IsIndeterminate(Exception exception) =>
        exception is BladeWriteException
        {
            SubmissionState: WriteSubmissionState.Indeterminate,
        };

    private static bool IsKnownNotApplied(Exception exception) =>
        exception is BladeWriteException
        {
            SubmissionState: WriteSubmissionState.KnownNotApplied,
        };

    private static bool IsPreSubmissionExhaustion(Exception exception) =>
        exception is BladeWriteException
        {
            SubmissionState: WriteSubmissionState.KnownNotApplied,
            FailureKind: HidSessionFailureKind.Exhausted,
        };

    private void RecordRetryFailure(
        CoolingPadFanRetryLatch requested,
        bool retryingAfterExhaustion,
        Exception exception)
    {
        if (!IsKnownNotApplied(exception))
        {
            return;
        }

        if (IsPreSubmissionExhaustion(exception) && !retryingAfterExhaustion)
        {
            // The provider retires the exhausted session before this exception
            // escapes. Permit one later control tick to use a replacement
            // session; never replay inside the original submission call.
            exhaustionRetry = requested;
            return;
        }

        retryLatch = requested;
    }

    private void PublishFailure(string reason, Exception exception)
    {
        CoolingPadFanOwnershipSnapshot ownership = ownershipCoordinator.Snapshot;
        CoolingPadFanRuntimeSnapshot previous = Snapshot;
        SetSnapshot(ownership.Ownership switch
        {
            CoolingPadControlOwnershipState.Owned => previous with
            {
                Reason = $"{reason} OpenBlade still owns the last confirmed target, but recovery state did not fully commit: {exception.Message}",
            },
            CoolingPadControlOwnershipState.Indeterminate => new(
                DeviceFeatureAccess.ReadWrite,
                CoolingPadControlOwnershipState.Indeterminate,
                AppliedDemand: null,
                AppliedTarget: null,
                $"{reason} The outcome is indeterminate: {exception.Message}"),
            _ => new CoolingPadFanRuntimeSnapshot(
                DeviceFeatureAccess.ReadWrite,
                CoolingPadControlOwnershipState.Unowned,
                AppliedDemand: null,
                AppliedTarget: null,
                $"{reason} No OpenBlade-owned target changed: {exception.Message}"),
        });
    }

    private void SetSnapshot(CoolingPadFanRuntimeSnapshot value)
    {
        if (!value.IsWellFormed)
        {
            throw new ArgumentException(
                "The cooling-pad fan runtime snapshot is invalid.",
                nameof(value));
        }

        lock (snapshotGate)
        {
            snapshot = value;
        }
    }

    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "A cooling-pad fan operation reason is required.",
                nameof(reason));
        }
    }
}
