namespace OpenBlade.Contracts;

public static class HyperBoostCapabilityPolicy
{
    // True when no pad-less path is admitted, so the exact cooling pad is the only entry path.
    public static bool RequiresCoolingPad(DeviceCapabilities capabilities) =>
        capabilities.HyperBoostWithoutCoolingPadAccess != DeviceFeatureAccess.ReadWrite;

    // Base mode for an entry with the exact cooling pad connected: an explicit
    // declaration where provided, otherwise the legacy Performance inference.
    public static PerformancePreset? PadPresentEntryBaseMode(DeviceCapabilities capabilities)
    {
        if (capabilities.HyperBoostAccess != DeviceFeatureAccess.ReadWrite
            || IsEntryRestricted(capabilities)
            || capabilities.HyperBoostBaseModes is not { } baseModes)
        {
            return null;
        }

        PerformancePreset candidate = baseModes.CoolingPadEntryBaseMode
            ?? PerformancePreset.Performance;
        return baseModes.AccessFor(candidate) == DeviceFeatureAccess.ReadWrite
            ? candidate
            : null;
    }

    // Base mode for an entry with no cooling pad: the model's admitted direct
    // transition (Balanced where admitted, otherwise Performance), or null.
    public static PerformancePreset? PadlessEntryBaseMode(DeviceCapabilities capabilities)
    {
        if (capabilities.HyperBoostAccess != DeviceFeatureAccess.ReadWrite
            || IsEntryRestricted(capabilities)
            || RequiresCoolingPad(capabilities))
        {
            return null;
        }
        if ((capabilities.HyperBoostBaseModes?.AccessFor(PerformancePreset.Balanced)
            ?? capabilities.HyperBoostAccess) == DeviceFeatureAccess.ReadWrite)
        {
            return PerformancePreset.Balanced;
        }
        return capabilities.HyperBoostBaseModes?.AccessFor(PerformancePreset.Performance)
            == DeviceFeatureAccess.ReadWrite ? PerformancePreset.Performance : null;
    }

    public static PerformancePreset? EntryBaseMode(
        DeviceCapabilities capabilities,
        bool coolingPadPresent) => coolingPadPresent
            ? PadPresentEntryBaseMode(capabilities)
            : PadlessEntryBaseMode(capabilities);

    // A mode HyperBoost may enter from and restore to directly on an admitted pad path.
    public static bool IsDirectEntryBase(
        DeviceCapabilities capabilities,
        bool coolingPadPresent,
        PerformancePreset mode) =>
        EntryBaseMode(capabilities, coolingPadPresent) is not null
        && mode is PerformancePreset.Silent or PerformancePreset.Balanced or PerformancePreset.Performance
            or PerformancePreset.Custom or PerformancePreset.Turbo
        && (capabilities.HyperBoostBaseModes?.AccessFor(mode)
            ?? (mode == PerformancePreset.Balanced ? capabilities.HyperBoostAccess : null))
            == DeviceFeatureAccess.ReadWrite;

    // An entry restriction declines new entries only. Exit and restoration need
    // HyperBoostAccess and the owned base, so a laptop can always leave HyperBoost.
    public static bool IsEntryRestricted(DeviceCapabilities capabilities) =>
        capabilities.HyperBoostEntryRestriction is not null;

    // An entry restriction never removes the way out: the tray offers an exit
    // exactly when the laptop would admit an entry path without the restriction.
    public static bool CanExit(DeviceCapabilities capabilities) =>
        IsWritable(capabilities with { HyperBoostEntryRestriction = null });

    // Whether a confirmed mode after a HyperBoost exit is the expected result for
    // the owned base. On AC that is the owned base itself. On battery the mode-7
    // backends (RZ09-0483, RZ09-0482, RZ09-0510) hand off to Balanced, DC raw 6,
    // whatever base they own; models with a distinct DC Balanced preset
    // (RZ09-0510) confirm that raw value as BatteryBalanced. The owned base is not
    // carried over: when AC returns, the plugged-in profile applies again. Backends whose
    // reviewed exit restores the owned base on battery still confirm that base.
    public static bool IsRestoredBase(
        PerformancePreset ownedBase,
        PerformancePreset? confirmed,
        PowerSource exitPowerSource) =>
        confirmed == ownedBase
        || exitPowerSource == PowerSource.Battery
            && confirmed is PerformancePreset.Balanced or PerformancePreset.BatteryBalanced;

    public static bool IsWritable(DeviceCapabilities capabilities) =>
        PadPresentEntryBaseMode(capabilities) is not null
        || PadlessEntryBaseMode(capabilities) is not null;
}
