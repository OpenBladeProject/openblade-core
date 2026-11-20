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

    // A performance choice stored under manual fans, to be applied once when
    // fans next become Automatic. Only an observed hardware performance change
    // after the choice invalidates it. OpenBlade's own desired-versus-confirmed
    // comparisons never touch it: under a deferral they differ by design.
    private DeferredPerformanceChoice deferredPerformanceChoice;
    private PerformancePreset deferredPreset;
    private CustomPerformanceSettings? deferredCustomPerformance;

    public ConfirmedStateDriftTracker(Func<bool>? retireCompletedTemporaryFullCharge = null) =>
        this.retireCompletedTemporaryFullCharge = retireCompletedTemporaryFullCharge;

    public IReadOnlyList<DeviceSubsystem> ExternalChanges =>
        externalChangesSnapshot ??= new ReadOnlyCollection<DeviceSubsystem>(
            externalChanges.OrderBy(subsystem => subsystem).ToArray());

    // Only a choice that is still pending waits; an invalidated one will be
    // dropped by the next fan request.
    internal bool HasPendingDeferredPerformanceChoice =>
        deferredPerformanceChoice == DeferredPerformanceChoice.Pending;

    // Called only when a performance choice was stored with the deferral
    // message. A newer choice that is not deferred clears it instead.
    public void RecordDeferredPerformanceChoice(
        PerformancePreset preset,
        CustomPerformanceSettings? customPerformance)
    {
        deferredPerformanceChoice = DeferredPerformanceChoice.Pending;
        deferredPreset = preset;
        deferredCustomPerformance = preset == PerformancePreset.Custom ? customPerformance : null;
    }

    public void ClearDeferredPerformanceChoice() =>
        deferredPerformanceChoice = DeferredPerformanceChoice.None;

    // Settles the pending choice from a fan step's fresh readback after fans
    // became Automatic, against the intent that request resolved. A choice the
    // intent no longer holds (automation, a power change or a profile edit
    // replaced it) no longer applies. A fresh readback that already holds the
    // choice needs no write. Otherwise the choice applies only while no
    // performance change was observed since it was made and the fresh preset
    // is known and still the last observed one, including Custom levels; any
    // other outcome keeps the mode. Either way the choice is consumed before
    // any write, so a canceled or uncertain write is never replayed.
    internal DeferredPerformanceDecision DecideDeferredPerformance(
        ConfirmedDeviceState? fresh,
        PerformancePreset intent,
        CustomPerformanceSettings? customPerformanceIntent)
    {
        DeferredPerformanceChoice choice = deferredPerformanceChoice;
        if (choice == DeferredPerformanceChoice.None)
        {
            return DeferredPerformanceDecision.None;
        }

        deferredPerformanceChoice = DeferredPerformanceChoice.None;
        if (intent != deferredPreset
            || intent == PerformancePreset.Custom && customPerformanceIntent != deferredCustomPerformance)
        {
            return DeferredPerformanceDecision.NoLongerApplies;
        }

        if (fresh?.Performance == deferredPreset
            && (deferredPreset != PerformancePreset.Custom
                || fresh.CustomPerformance == deferredCustomPerformance))
        {
            return DeferredPerformanceDecision.None;
        }

        if (choice == DeferredPerformanceChoice.Pending
            && fresh?.Performance is { } observed
            && observed == performance
            && !(observed == PerformancePreset.Custom
                && fresh.CustomPerformance is { } levels
                && customPerformance is { } baseline
                && levels != baseline))
        {
            return DeferredPerformanceDecision.Apply;
        }

        return DeferredPerformanceDecision.Kept;
    }

    // An applied decision whose preset write is known not to have been
    // submitted stays pending for the next fan request that finds fans
    // Automatic.
    internal void RestoreUnsubmittedDeferredPerformance() =>
        deferredPerformanceChoice = DeferredPerformanceChoice.Pending;

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
            AddObservedChange(subsystem);
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
            AddObservedChange(subsystem);
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

    // A hardware value differs from the last observed one.
    private void AddObservedChange(DeviceSubsystem subsystem)
    {
        if (subsystem == DeviceSubsystem.Performance
            && deferredPerformanceChoice == DeferredPerformanceChoice.Pending)
        {
            deferredPerformanceChoice = DeferredPerformanceChoice.Invalidated;
        }

        AddExternalChange(subsystem);
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

    private enum DeferredPerformanceChoice
    {
        None,
        Pending,
        Invalidated,
    }
}
