using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal static class HyperBoostBaseModeAdmissionSource
{
    internal static HyperBoostBaseModeCapabilities ReadCapabilities() => new(
        DeviceFeatureAccess.ObservedOnly,
        DeviceFeatureAccess.ReadWrite,
        DeviceFeatureAccess.ObservedOnly);
}
