using System.Collections.ObjectModel;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class ConfirmedStateDriftTracker
{
    private readonly HashSet<DeviceSubsystem> externalChanges = [];
    private IReadOnlyList<DeviceSubsystem>? externalChangesSnapshot;
    private PerformancePreset? performance;
    private CustomPerformanceSettings? customPerformance;
    private GpuClockOffsetSettings? gpuClockOffsets;
    private FanMode? fans;
    private ConfirmedBatteryState? battery;
    private bool? temporaryFullCharge;
    private LightingSettings? lighting;
    private readonly Func<bool>? retireCompletedTemporaryFullCharge;

    public ConfirmedStateDriftTracker(Func<bool>? retireCompletedTemporaryFullCharge = null) =>
        this.retireCompletedTemporaryFullCharge = retireCompletedTemporaryFullCharge;

    public IReadOnlyList<DeviceSubsystem> ExternalChanges =>
        externalChangesSnapshot ??= new ReadOnlyCollection<DeviceSubsystem>(
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
            RemoveExternalChange(subsystem);
        }
        else
        {
            AddExternalChange(subsystem);
        }
    }

    public void AcknowledgeIntentionalWithoutReadback(DeviceSubsystem subsystem)
    {
        // Some setters (currently only host-rendered keyboard effects) can
        // independently verify transport/brightness but have no firmware GET for
        // their semantic value. A successful explicit reclaim clears an old
        // warning without inventing a new confirmed baseline.
        RemoveExternalChange(subsystem);
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
            AddExternalChange(subsystem);
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
            AddExternalChange(subsystem);
        }

        baseline = observed;
    }

    private void ObserveBattery(ConfirmedBatteryState? observed)
    {
        if (observed is null)
        {
            return;
        }

        // Missing fields are unavailable evidence, not a new firmware value.
        // Track the optional temporary override independently so a partial read
        // neither invents drift nor erases the last comparable observation.
        if (battery is not null
            && (battery.ProtectionEnabled != observed.ProtectionEnabled
                || battery.ChargeLimitPercent != observed.ChargeLimitPercent))
        {
            AddExternalChange(DeviceSubsystem.Battery);
        }

        // A confirmed true-to-false readback is the firmware one-shot ending,
        // whether by completion or an outside cancel. When the caller retires
        // its matching intent, that end is not an external change.
        if (temporaryFullCharge is true
            && observed.TemporaryFullCharge is false
            && retireCompletedTemporaryFullCharge?.Invoke() == true)
        {
            temporaryFullCharge = false;
        }

        ObserveValue(
            observed.TemporaryFullCharge,
            ref temporaryFullCharge,
            DeviceSubsystem.Battery);
        battery = observed;
    }

    private void ObserveCustomPerformance(ConfirmedDeviceState state)
    {
        if (state.Performance is null)
        {
            return;
        }

        if (state.Performance != PerformancePreset.Custom)
        {
            customPerformance = null;
            return;
        }

        ObserveValue(
            state.CustomPerformance,
            ref customPerformance,
            DeviceSubsystem.Performance);
    }

    private void AddExternalChange(DeviceSubsystem subsystem)
    {
        if (externalChanges.Add(subsystem))
        {
            externalChangesSnapshot = null;
        }
    }

    private void RemoveExternalChange(DeviceSubsystem subsystem)
    {
        if (externalChanges.Remove(subsystem))
        {
            externalChangesSnapshot = null;
        }
    }
}
