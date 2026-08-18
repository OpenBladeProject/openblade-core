using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal interface ICoolingPadFanSemanticBackend
{
    Task AcquireTargetAsync(
        CoolingPadFanTarget target,
        CancellationToken cancellationToken);

    Task UpdateTargetAsync(
        CoolingPadFanTarget target,
        CancellationToken cancellationToken);

    Task HandbackAsync(CancellationToken cancellationToken);

    Task HandbackForLifecycleAsync(CancellationToken cancellationToken) =>
        HandbackAsync(cancellationToken);
}

internal readonly record struct CoolingPadFanOwnershipSnapshot(
    CoolingPadControlOwnershipState Ownership,
    CoolingPadFanTarget? AppliedTarget)
{
    internal bool IsWellFormed =>
        Enum.IsDefined(Ownership)
        && (Ownership == CoolingPadControlOwnershipState.Owned
            ? AppliedTarget is not null
            : AppliedTarget is null);
}

/// <summary>
/// Tracks only state established by confirmed OpenBlade semantic writes. It
/// does not own desired fan intent and cannot infer ownership from discovery,
/// an opaque observation, or a target that happens to match the plan.
/// </summary>
internal sealed class CoolingPadFanOwnershipCoordinator
{
    private readonly ICoolingPadFanSemanticBackend backend;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object snapshotGate = new();
    private CoolingPadFanOwnershipSnapshot snapshot = new(
        CoolingPadControlOwnershipState.Unowned,
        AppliedTarget: null);

    internal CoolingPadFanOwnershipCoordinator(
        ICoolingPadFanSemanticBackend backend)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    internal CoolingPadFanOwnershipSnapshot Snapshot
    {
        get
        {
            lock (snapshotGate)
            {
                return snapshot;
            }
        }
    }

    internal void RestoreConfirmedOwnership(CoolingPadFanTarget target)
    {
        lock (snapshotGate)
        {
            if (snapshot.Ownership != CoolingPadControlOwnershipState.Unowned)
            {
                throw new InvalidOperationException(
                    "Cooling-pad ownership can be restored only from a clean process-local state.");
            }

            snapshot = new CoolingPadFanOwnershipSnapshot(
                CoolingPadControlOwnershipState.Owned,
                target);
        }
    }

    internal void RestoreIndeterminateOwnership()
    {
        lock (snapshotGate)
        {
            snapshot = new CoolingPadFanOwnershipSnapshot(
                CoolingPadControlOwnershipState.Indeterminate,
                AppliedTarget: null);
        }
    }

    internal async Task<bool> ApplyTargetAsync(
        CoolingPadFanTarget target,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CoolingPadFanOwnershipSnapshot current = Snapshot;
            ThrowIfIndeterminate(current);
            if (current is
                {
                    Ownership: CoolingPadControlOwnershipState.Owned,
                    AppliedTarget: { } applied,
                }
                && applied == target)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (current.Ownership == CoolingPadControlOwnershipState.Owned)
                {
                    await backend.UpdateTargetAsync(target, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await backend.AcquireTargetAsync(target, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState == WriteSubmissionState.KnownNotApplied)
            {
                throw;
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState == WriteSubmissionState.Indeterminate)
            {
                LatchIndeterminate();
                throw;
            }
            catch (Exception exception)
            {
                LatchIndeterminate();
                throw Indeterminate(
                    "The cooling-pad target outcome is indeterminate.",
                    exception);
            }

            SetSnapshot(new CoolingPadFanOwnershipSnapshot(
                CoolingPadControlOwnershipState.Owned,
                target));
            return true;
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<bool> HandbackAsync(CancellationToken cancellationToken)
        => await HandbackCoreAsync(
            lifecycleCleanup: false,
            cancellationToken).ConfigureAwait(false);

    internal async Task<bool> HandbackForLifecycleAsync(
        CancellationToken cancellationToken) => await HandbackCoreAsync(
            lifecycleCleanup: true,
            cancellationToken).ConfigureAwait(false);

    private async Task<bool> HandbackCoreAsync(
        bool lifecycleCleanup,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CoolingPadFanOwnershipSnapshot current = Snapshot;
            ThrowIfIndeterminate(current);
            if (current.Ownership == CoolingPadControlOwnershipState.Unowned)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (lifecycleCleanup)
                {
                    await backend.HandbackForLifecycleAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await backend.HandbackAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState == WriteSubmissionState.KnownNotApplied)
            {
                throw;
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState == WriteSubmissionState.Indeterminate)
            {
                LatchIndeterminate();
                throw;
            }
            catch (Exception exception)
            {
                LatchIndeterminate();
                throw Indeterminate(
                    "The cooling-pad handback outcome is indeterminate.",
                    exception);
            }

            SetSnapshot(new CoolingPadFanOwnershipSnapshot(
                CoolingPadControlOwnershipState.Unowned,
                AppliedTarget: null));
            return true;
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task InvalidateConnectionAsync(
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.Ownership == CoolingPadControlOwnershipState.Owned)
            {
                LatchIndeterminate();
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    private static void ThrowIfIndeterminate(
        CoolingPadFanOwnershipSnapshot current)
    {
        if (current.Ownership == CoolingPadControlOwnershipState.Indeterminate)
        {
            throw new InvalidOperationException(
                "Cooling-pad ownership is indeterminate; another fan write is not safe.");
        }
    }

    private void LatchIndeterminate() => SetSnapshot(
        new CoolingPadFanOwnershipSnapshot(
            CoolingPadControlOwnershipState.Indeterminate,
            AppliedTarget: null));

    private void SetSnapshot(CoolingPadFanOwnershipSnapshot value)
    {
        if (!value.IsWellFormed)
        {
            throw new ArgumentException(
                "The cooling-pad ownership snapshot is invalid.",
                nameof(value));
        }

        lock (snapshotGate)
        {
            snapshot = value;
        }
    }

    private static BladeWriteException Indeterminate(
        string message,
        Exception exception) => new(
            WriteSubmissionState.Indeterminate,
            message,
            exception);
}
