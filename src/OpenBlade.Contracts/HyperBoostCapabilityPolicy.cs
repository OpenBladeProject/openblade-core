namespace OpenBlade.Contracts;

public static class HyperBoostCapabilityPolicy
{
    public static bool RequiresCoolingPad(DeviceCapabilities capabilities) =>
        capabilities.HyperBoostWithoutCoolingPadAccess != DeviceFeatureAccess.ReadWrite;

    public static PerformancePreset? EntryBaseMode(DeviceCapabilities capabilities)
    {
        if (capabilities.HyperBoostAccess != DeviceFeatureAccess.ReadWrite)
        {
            return null;
        }
        if (!RequiresCoolingPad(capabilities))
        {
            return (capabilities.HyperBoostBaseModes?.AccessFor(PerformancePreset.Balanced)
                ?? capabilities.HyperBoostAccess) == DeviceFeatureAccess.ReadWrite
                    ? PerformancePreset.Balanced : null;
        }
        return capabilities.HyperBoostBaseModes?.AccessFor(PerformancePreset.Performance)
            == DeviceFeatureAccess.ReadWrite ? PerformancePreset.Performance : null;
    }
}
