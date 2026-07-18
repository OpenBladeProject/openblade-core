using OpenBlade.Protocol;

namespace OpenBlade.Service;

public interface IVerifiedBladeDeviceProvider
{
    bool TryGetVerifiedDevice(out IBladeDevice device);

    Task InvalidateLightingRendererAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
