using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal static class HyperBoostBaseModeAdmissionSource
{
    internal static HyperBoostBaseModeCapabilities ReadCapabilities() => new(
        DeviceFeatureAccess.ReadWrite,
        DeviceFeatureAccess.ReadWrite,
        DeviceFeatureAccess.ReadWrite)
    {
        Custom = DeviceFeatureAccess.ReadWrite,
    };
}
