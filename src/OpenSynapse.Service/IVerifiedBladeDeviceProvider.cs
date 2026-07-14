using OpenSynapse.Protocol;

namespace OpenSynapse.Service;

public interface IVerifiedBladeDeviceProvider
{
    bool TryGetVerifiedDevice(out IBladeDevice device);
}
