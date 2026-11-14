using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed partial class ServiceControlRuntime
{
    private static readonly TimeSpan ExternalPresenceFreshness = TimeSpan.FromSeconds(30);
    private int externalPowerPresent;
    private int windowsPowerKind;
    private int externalClassificationUnavailable;
    private int lastClassifiedExternalSource = -1;
    private long windowsPowerGeneration;
    private long externalPresenceTimestamp;
    private long explicitUnclassifiedOverrideGeneration = -1;

    // Capture failures are not AC evidence. Native submission uses the same
    // fresh Windows callback, so this presentation fact grants no write itself.
    private PowerSource CaptureControlPowerSource()
    {
        try
        {
            PowerSource source = powerStateProvider.CapturePowerSource();
            int kind = source == PowerSource.Battery ? 1
                : source is PowerSource.ExternalUnknown or PowerSource.PluggedIn or PowerSource.UsbC ? 2 : 0;
            int prior = Interlocked.Exchange(ref windowsPowerKind, kind);
            if (prior != kind)
            {
                Interlocked.Increment(ref windowsPowerGeneration);
            }
            Volatile.Write(ref externalPowerPresent, kind == 2 ? 1 : 0);
            Interlocked.Exchange(ref externalPresenceTimestamp, timeProvider.GetTimestamp());
            if (kind != 2)
            {
                Volatile.Write(ref externalClassificationUnavailable, 0);
                Volatile.Write(ref lastClassifiedExternalSource, -1);
            }
            return source;
        }
        catch
        {
            // A failed read is not a physical source boundary. Revoke live
            // evidence without expiring saved temporary intent or inventing ABA.
            Volatile.Write(ref externalPowerPresent, 0);
            Volatile.Write(ref externalClassificationUnavailable, 1);
            throw;
        }
    }

    private bool HasFreshExternalPowerPresence =>
        Volatile.Read(ref externalPowerPresent) != 0 && !lowPowerGate.IsActive
        && timeProvider.GetElapsedTime(Interlocked.Read(ref externalPresenceTimestamp)) <= ExternalPresenceFreshness;

    private bool IsUnclassifiedExternalContext =>
        Volatile.Read(ref externalClassificationUnavailable) != 0;

    private bool CanAdmitUnclassifiedExternalControl(DeviceSubsystem subsystem)
    {
        if (!HasFreshExternalPowerPresence || !IsUnclassifiedExternalContext)
        {
            return false;
        }
        ServiceStatusSnapshot current = GetStatus();
        return subsystem is DeviceSubsystem.Lighting or DeviceSubsystem.Battery
            || subsystem is DeviceSubsystem.Performance or DeviceSubsystem.Fans
                && current.Capabilities?.SupportsOrdinaryThermalControlOnUnclassifiedExternalPower == true
                && current.ConfirmedState?.HyperBoostActive == false
                && !ownsHyperBoost && !hyperBoostRestorePending && !hyperBoostJournalSafetyBlock;
    }

    private bool FreshUnclassifiedExternalControlIsAdmitted(DeviceSubsystem subsystem)
    {
        PowerSource windows = CaptureControlPowerSource();
        return windows is PowerSource.ExternalUnknown or PowerSource.PluggedIn or PowerSource.UsbC
            && CanAdmitUnclassifiedExternalControl(subsystem);
    }

    private void RecordExplicitUnclassifiedOverride() =>
        Interlocked.Exchange(ref explicitUnclassifiedOverrideGeneration, Interlocked.Read(ref windowsPowerGeneration));

    private bool PreserveOverridesForExternalClassification(PowerSource destination) =>
        destination is PowerSource.PluggedIn or PowerSource.UsbC
        && HasFreshExternalPowerPresence
        && Interlocked.Read(ref explicitUnclassifiedOverrideGeneration) == Interlocked.Read(ref windowsPowerGeneration)
        && (Volatile.Read(ref lastClassifiedExternalSource) < 0
            || Volatile.Read(ref lastClassifiedExternalSource) == (int)destination);

    private bool OrdinaryUnclassifiedOverrideIsAvailable =>
        IsUnclassifiedExternalContext && HasFreshExternalPowerPresence
        && Volatile.Read(ref status)?.Capabilities?.SupportsOrdinaryThermalControlOnUnclassifiedExternalPower == true;

    private bool CurrentPowerContextMatchesAutomation(PowerSource source) =>
        Automation.PowerSource == source
        || source == PowerSource.ExternalUnknown && IsUnclassifiedExternalContext && HasFreshExternalPowerPresence;

    private void ValidateUnclassifiedPerformanceOverride(PerformanceOverride? value)
    {
        if (!IsUnclassifiedExternalContext || value is null)
        {
            return;
        }
        if (!OrdinaryUnclassifiedOverrideIsAvailable
            || value.Value is not (PerformancePreset.Balanced or PerformancePreset.Silent or PerformancePreset.Performance
                or PerformancePreset.Custom or PerformancePreset.Turbo or PerformancePreset.MaximumPower
                or PerformancePreset.Gaming or PerformancePreset.Creator)
            || value.GpuClockTuning is not null || value.CustomPerformance?.GpuClockOffsets is not null)
        {
            throw new InvalidDataException("This request needs classified power or includes native overclocking; ordinary external presence does not admit it.");
        }
        if (value.Value == PerformancePreset.Custom
            && GetStatus().Capabilities?.CustomPerformance?.Supports(value.CustomPerformance) != true)
        {
            throw new InvalidDataException("The complete Custom CPU/GPU and maximum-fan intent is not admitted for this model.");
        }
    }

    private DesiredBatteryState? ResolveExplicitBatteryBaseline(PowerSource source)
    {
        if (source != PowerSource.ExternalUnknown)
        {
            return Automation.DesiredBattery;
        }
        ServiceStatusSnapshot current = GetStatus();
        if (current.ConfirmedState?.Battery is not { } battery
            || current.Capabilities?.SupportsTemporaryFullCharge == true && battery.TemporaryFullCharge is null)
        {
            return null;
        }
        return new(Automation.BatteryPolicy,
            Automation.BatteryProtectionOverride ?? battery.ProtectionEnabled,
            battery.TemporaryFullCharge == true);
    }

    private bool HasFreshDirectControlPowerContext()
    {
        PowerSource source = CaptureControlPowerSource();
        return !IsUnclassifiedExternalContext || source is PowerSource.ExternalUnknown or PowerSource.PluggedIn or PowerSource.UsbC;
    }

}
