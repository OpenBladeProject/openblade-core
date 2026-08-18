using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal readonly record struct CoolingPadLightingRuntimeSnapshot(
    DeviceFeatureAccess Access,
    bool Enabled,
    CoolingPadLightingSyncState State,
    string Reason);

internal interface ICoolingPadLightingRuntimeCoordinator
{
    event Action<CoolingPadLightingRuntimeSnapshot>? SnapshotChanged;

    CoolingPadLightingRuntimeSnapshot Snapshot { get; }

    Task SetEnabledAsync(bool value, CancellationToken cancellationToken);

    Task AcknowledgeRecoveryAsync(CancellationToken cancellationToken);

    Task SetConnectedAsync(bool value, CancellationToken cancellationToken);

    Task SetSuppressedAsync(bool value, CancellationToken cancellationToken);

    Task SetDelegatedAsync(bool value, CancellationToken cancellationToken);

    Task InvalidateAsync(CancellationToken cancellationToken);
}

internal sealed class ProtocolCoolingPadLightingRuntimeCoordinator
    : ICoolingPadLightingRuntimeCoordinator
{
    private readonly ICoolingPadLightingCoordinator coordinator;

    internal ProtocolCoolingPadLightingRuntimeCoordinator(
        ICoolingPadLightingCoordinator coordinator)
    {
        this.coordinator = coordinator
            ?? throw new ArgumentNullException(nameof(coordinator));
        this.coordinator.SnapshotChanged += OnSnapshotChanged;
    }

    public event Action<CoolingPadLightingRuntimeSnapshot>? SnapshotChanged;

    public CoolingPadLightingRuntimeSnapshot Snapshot
    {
        get
        {
            CoolingPadLightingCoordinatorSnapshot current = coordinator.Snapshot;
            return new CoolingPadLightingRuntimeSnapshot(
                current.Access,
                current.Enabled,
                current.State,
                current.Reason);
        }
    }

    public Task SetEnabledAsync(bool value, CancellationToken cancellationToken) =>
        coordinator.SetEnabledAsync(value, cancellationToken);

    public Task AcknowledgeRecoveryAsync(CancellationToken cancellationToken) =>
        coordinator.AcknowledgeRecoveryAsync(cancellationToken);

    public Task SetConnectedAsync(bool value, CancellationToken cancellationToken) =>
        coordinator.SetConnectedAsync(value, cancellationToken);

    public Task SetSuppressedAsync(bool value, CancellationToken cancellationToken) =>
        coordinator.SetSuppressedAsync(value, cancellationToken);

    public Task SetDelegatedAsync(bool value, CancellationToken cancellationToken) =>
        coordinator.SetDelegatedAsync(value, cancellationToken);

    public Task InvalidateAsync(CancellationToken cancellationToken) =>
        coordinator.InvalidateAsync(cancellationToken);

    private void OnSnapshotChanged(CoolingPadLightingCoordinatorSnapshot snapshot) =>
        SnapshotChanged?.Invoke(new CoolingPadLightingRuntimeSnapshot(
            snapshot.Access,
            snapshot.Enabled,
            snapshot.State,
            snapshot.Reason));
}
