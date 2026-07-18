using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public interface IVerifiedBladeDeviceProvider
{
    bool TryGetVerifiedDevice(out IBladeDevice device);

    Task<LightingRendererOwnershipState> InvalidateLightingRendererAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(LightingRendererOwnershipState.None);
}
