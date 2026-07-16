using System.Collections.ObjectModel;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class ConfirmedStateDriftTracker
{
    private readonly HashSet<DeviceSubsystem> externalChanges = [];
    private PerformancePreset? performance;
    private FanMode? fans;
    private ConfirmedBatteryState? battery;
    private bool batteryObserved;
    private LightingSettings? lighting;

    public IReadOnlyList<DeviceSubsystem> ExternalChanges =>
        new ReadOnlyCollection<DeviceSubsystem>(
            externalChanges.OrderBy(subsystem => subsystem).ToArray());

    public void Observe(ConfirmedDeviceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ObserveValue(state.Performance, ref performance, DeviceSubsystem.Performance);
        ObserveValue(state.Fans, ref fans, DeviceSubsystem.Fans);
        ObserveBattery(state.Battery);
        ObserveValue(state.Lighting, ref lighting, DeviceSubsystem.Lighting);
    }

    public void ConfirmIntentional(DeviceSubsystem subsystem, bool matched)
    {
        if (matched)
        {
            externalChanges.Remove(subsystem);
        }
        else
        {
            externalChanges.Add(subsystem);
        }
    }

    private void ObserveValue<T>(
        T? observed,
        ref T? baseline,
        DeviceSubsystem subsystem)
        where T : struct
    {
        if (observed is null)
        {
            return;
        }

        if (baseline is not null && !EqualityComparer<T>.Default.Equals(
                baseline.Value,
                observed.Value))
        {
            externalChanges.Add(subsystem);
        }

        baseline = observed;
    }

    private void ObserveValue<T>(
        T? observed,
        ref T? baseline,
        DeviceSubsystem subsystem)
        where T : class
    {
        if (observed is null)
        {
            return;
        }

        if (baseline is not null && !EqualityComparer<T>.Default.Equals(baseline, observed))
        {
            externalChanges.Add(subsystem);
        }

        baseline = observed;
    }

    private void ObserveBattery(ConfirmedBatteryState? observed)
    {
        if (batteryObserved && !EqualityComparer<ConfirmedBatteryState?>.Default.Equals(
                battery,
                observed))
        {
            externalChanges.Add(DeviceSubsystem.Battery);
        }

        battery = observed;
        batteryObserved = true;
    }
}
