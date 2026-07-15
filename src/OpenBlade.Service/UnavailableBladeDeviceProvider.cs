using OpenBlade.Protocol;

namespace OpenBlade.Service;

public sealed class UnavailableBladeDeviceProvider : IVerifiedBladeDeviceProvider
{
    public bool TryGetVerifiedDevice(out IBladeDevice device)
    {
        device = null!;
        return false;
    }
}
