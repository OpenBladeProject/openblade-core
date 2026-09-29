namespace OpenBlade.Contracts;

public static class HyperBoostCapabilityPolicy
{
    // True when no pad-less path is admitted, so the exact cooling pad is the only entry path.
    public static bool RequiresCoolingPad(DeviceCapabilities capabilities) =>
        capabilities.HyperBoostWithoutCoolingPadAccess != DeviceFeatureAccess.ReadWrite;

    // Base mode for an entry with the exact cooling pad connected: the captured
    // Performance transition, or null when that path is not admitted.
    public static PerformancePreset? PadPresentEntryBaseMode(DeviceCapabilities capabilities) =>
        capabilities.HyperBoostAccess == DeviceFeatureAccess.ReadWrite
        && capabilities.HyperBoostBaseModes?.AccessFor(PerformancePreset.Performance)
            == DeviceFeatureAccess.ReadWrite
            ? PerformancePreset.Performance
            : null;

    // Base mode for an entry with no cooling pad: the model's admitted direct
    // transition (Balanced where admitted, otherwise Performance), or null.
    public static PerformancePreset? PadlessEntryBaseMode(DeviceCapabilities capabilities)
    {
        if (capabilities.HyperBoostAccess != DeviceFeatureAccess.ReadWrite
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

    public static bool IsWritable(DeviceCapabilities capabilities) =>
        PadPresentEntryBaseMode(capabilities) is not null
        || PadlessEntryBaseMode(capabilities) is not null;
}
