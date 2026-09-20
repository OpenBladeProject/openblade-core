using OpenBlade.Contracts;

namespace OpenBlade.Service;

// The runtime's operation gate serializes mutations and reconciliation. This
// lock also protects snapshots read by status publishers outside that gate.
internal sealed class PendingWriteConfirmationTracker
{
    private readonly object gate = new();
    private readonly Dictionary<DeviceSubsystem, PendingWriteConfirmation> entries = [];

    public bool HasPending
    {
        get
        {
            lock (gate)
            {
                return entries.Count > 0;
            }
        }
    }

    public void Set(DeviceSubsystem subsystem, PendingWriteConfirmation pending)
    {
        lock (gate)
        {
            entries[subsystem] = pending;
        }
    }

    public void Remove(DeviceSubsystem subsystem)
    {
        lock (gate)
        {
            entries.Remove(subsystem);
        }
    }

    public bool Blocks(DeviceSubsystem subsystem)
    {
        lock (gate)
        {
            return entries.ContainsKey(subsystem)
                || DeviceSubsystemOrder.IsThermal(subsystem)
                    && (entries.ContainsKey(DeviceSubsystem.Performance)
                        || entries.ContainsKey(DeviceSubsystem.Fans));
        }
    }

    public DeviceSubsystem[] SnapshotSubsystems()
    {
        lock (gate)
        {
            return [.. entries.Keys.Order()];
        }
    }

    public KeyValuePair<DeviceSubsystem, PendingWriteConfirmation>[] Snapshot()
    {
        lock (gate)
        {
            return [.. entries];
        }
    }

    public KeyValuePair<DeviceSubsystem, PendingWriteConfirmation>[] Clear()
    {
        lock (gate)
        {
            KeyValuePair<DeviceSubsystem, PendingWriteConfirmation>[] pending = [.. entries];
            entries.Clear();
            return pending;
        }
    }

    public IReadOnlySet<DeviceSubsystem> GetWriteExclusions(
        IEnumerable<DeviceSubsystem>? additionalExclusions = null)
    {
        HashSet<DeviceSubsystem> exclusions = additionalExclusions?.ToHashSet() ?? [];
        exclusions.UnionWith(Snapshot().Select(entry => entry.Key));
        if (exclusions.Contains(DeviceSubsystem.Performance)
            || exclusions.Contains(DeviceSubsystem.Fans))
        {
            exclusions.Add(DeviceSubsystem.Performance);
            exclusions.Add(DeviceSubsystem.Fans);
        }
        return exclusions;
    }
}

internal sealed record PendingWriteConfirmation(
    PowerSource PowerSource,
    DesiredDeviceState Target,
    ConfirmedDeviceState PreWriteState,
    bool? HyperBoostActive = null,
    PerformancePreset? HyperBoostBaseMode = null,
    PendingBatteryIntentCommit? BatteryIntentCommit = null)
{
    public WriteConfirmationExpectation Expectation => new(
        Target, PreWriteState, HyperBoostActive, HyperBoostBaseMode);

    public bool CanConfirm(PowerSource powerSource, ProfileAutomationState owner) =>
        PowerSource == powerSource
        && (BatteryIntentCommit is not { } intent || ReferenceEquals(intent.Owner, owner));

    public PendingConfirmationResult Match(
        DeviceSubsystem subsystem,
        ConfirmedDeviceState confirmed,
        PowerSource powerSource,
        ProfileAutomationState owner,
        GpuClockOffsetManagementState? managementState) =>
        CanConfirm(powerSource, owner)
            ? WriteConfirmationPolicy.Match(subsystem, Expectation, confirmed, managementState)
            : PendingConfirmationResult.Mismatch;
}

internal sealed record PendingBatteryIntentCommit(
    ProfileAutomationState Owner,
    bool FullChargeOnly);
