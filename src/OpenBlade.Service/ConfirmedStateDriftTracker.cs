using System.Collections.ObjectModel;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class ConfirmedStateDriftTracker
{
    private readonly HashSet<DeviceSubsystem> externalChanges = [];
    private PerformancePreset? performance;
    private CustomPerformanceSettings? customPerformance;
    private bool customPerformanceObserved;
    private GpuClockOffsetSettings? gpuClockOffsets;
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
        ObserveCustomPerformance(state);
        ObserveValue(
            state.GpuClockOffsets?.Current,
            ref gpuClockOffsets,
            DeviceSubsystem.Performance);
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

    public void AcknowledgeIntentionalWithoutReadback(DeviceSubsystem subsystem)
    {
        // Some setters (currently only host-rendered keyboard effects) can
        // independently verify transport/brightness but have no firmware GET for
        // their semantic value. A successful explicit reclaim clears an old
        // warning without inventing a new confirmed baseline.
        externalChanges.Remove(subsystem);
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

    private void ObserveCustomPerformance(ConfirmedDeviceState state)
    {
        if (state.Performance != PerformancePreset.Custom)
        {
            customPerformance = null;
            customPerformanceObserved = false;
            return;
        }

        if (customPerformanceObserved
            && customPerformance != state.CustomPerformance)
        {
            externalChanges.Add(DeviceSubsystem.Performance);
        }

        customPerformance = state.CustomPerformance;
        customPerformanceObserved = true;
    }
}
