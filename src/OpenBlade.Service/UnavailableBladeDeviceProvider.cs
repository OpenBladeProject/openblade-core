using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public sealed class UnavailableBladeDeviceProvider : IVerifiedBladeDeviceProvider
{
    public FirmwareCompatibilityStatus FirmwareCompatibility =>
        FirmwareCompatibilityStatus.Unknown;

    public bool RequiresUntestedFirmwareOverride => false;

    public void SetAllowUntestedFirmware(bool enabled)
    {
    }

    public bool TryGetVerifiedDevice(out IBladeDevice device)
    {
        device = null!;
        return false;
    }
}
