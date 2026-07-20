using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public interface IVerifiedBladeDeviceProvider
{
    bool TryGetVerifiedDevice(out IBladeDevice device);

    bool TryGetOwnedKeyTriggeredLightingDevice(out IBladeDevice device) =>
        TryGetVerifiedDevice(out device);

    bool TryGetOwnedAmbientLightingDevice(out IBladeDevice device) =>
        TryGetVerifiedDevice(out device);

    Task<LightingRendererOwnershipState> InvalidateLightingRendererAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(LightingRendererOwnershipState.None);
}
