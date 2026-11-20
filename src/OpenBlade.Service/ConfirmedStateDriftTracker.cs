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
    private byte? independentBrightness;
    private LogoLightingMode? independentLogo;
    private bool? independentVaporChamber;
    private bool? independentTouchpadIndicator;
    private readonly Func<bool>? retireCompletedTemporaryFullCharge;

    // The preset the firmware held at the latest point OpenBlade's performance
    // intent became authoritative: the first observation, a confirmed
    // OpenBlade performance write, or an explicit user choice (deferred or
    // applied). A later readback that differs is an external change newer than
    // that intent. Unlike the external-change latch, an explicit choice moves
    // this point forward without clearing the user-visible warning.
    private PerformancePreset? performanceAtIntent;

    public ConfirmedStateDriftTracker(Func<bool>? retireCompletedTemporaryFullCharge = null) =>
        this.retireCompletedTemporaryFullCharge = retireCompletedTemporaryFullCharge;

    public IReadOnlyList<DeviceSubsystem> ExternalChanges =>
        externalChangesSnapshot ??= new ReadOnlyCollection<DeviceSubsystem>(
            externalChanges.OrderBy(subsystem => subsystem).ToArray());

    // Records that the user chose or cleared a performance intent now. Any
    // earlier external preset change is older than this choice.
    public void RecordExplicitPerformanceIntent() =>
        performanceAtIntent = performance ?? performanceAtIntent;

    // True when a fresh preset readback differs from the preset held when the
    // current intent became authoritative, including a change no observation
    // has recorded yet. Unknown evidence is not an external change.
    public bool PerformanceChangedSinceIntent(PerformancePreset? fresh) =>
        fresh is { } observed
        && performanceAtIntent is { } reference
        && observed != reference;

    public void Observe(ConfirmedDeviceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        performanceAtIntent ??= state.Performance;
        ObserveValue(state.Performance, ref performance, DeviceSubsystem.Performance);
        ObserveCustomPerformance(state);
        ObserveValue(
            state.GpuClockOffsets?.Current,
            ref gpuClockOffsets,
            DeviceSubsystem.Performance);
        ObserveValue(state.Fans, ref fans, DeviceSubsystem.Fans);
        ObserveBattery(state.Battery);
        ObserveValue(state.Lighting, ref lighting, DeviceSubsystem.Lighting);
        ObserveValue(state.IndependentLighting?.Brightness, ref independentBrightness, DeviceSubsystem.Lighting);
        ObserveValue(state.IndependentLighting?.LogoMode, ref independentLogo, DeviceSubsystem.Lighting);
        ObserveValue(state.IndependentLighting?.VaporChamberLightingEnabled, ref independentVaporChamber, DeviceSubsystem.Lighting);
        ObserveValue(state.IndependentLighting?.TouchpadStatusIndicatorEnabled, ref independentTouchpadIndicator, DeviceSubsystem.Lighting);
    }

    public void ConfirmIntentional(DeviceSubsystem subsystem, bool matched)
    {
        if (matched)
        {
            RemoveExternalChange(subsystem);
            if (subsystem == DeviceSubsystem.Performance)
            {
                performanceAtIntent = performance ?? performanceAtIntent;
            }
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

        // A confirmed true-to-false readback is the one-shot ending, whether
        // Synapse cleared it at full charge or another app cancelled it. When
        // the caller retires its matching intent, that end is not an external
        // change.
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
