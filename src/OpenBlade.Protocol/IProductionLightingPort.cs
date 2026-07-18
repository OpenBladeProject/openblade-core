using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Closed, typed production lighting surface. Raw Razer commands and matrix
/// buffers never cross the protocol boundary.
/// </summary>
public interface IProductionLightingPort : IAsyncDisposable
{
    DeviceCapabilities Capabilities { get; }

    LightingRendererOwnershipState OwnershipState { get; }

    Task<LightingSettings?> ReadStateAsync(CancellationToken cancellationToken);

    Task SetLightingAsync(
        LightingSettings settings,
        CancellationToken cancellationToken);

    Task PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken cancellationToken);
}

public interface IProductionLightingPortFactory
{
    DeviceCapabilities Capabilities { get; }

    Task<HidSessionResult<IProductionLightingPort>> OpenAsync(
        CancellationToken cancellationToken);
}
