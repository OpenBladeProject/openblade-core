using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public sealed record LightingRendererInvalidationResult(
    LightingRendererOwnershipState Ownership,
    DriverModeRestoreOutcome DriverModeRestoreOutcome);

public interface IVerifiedBladeDeviceProvider
{
    FirmwareCompatibilityStatus FirmwareCompatibility { get; }

    bool RequiresUntestedFirmwareOverride { get; }

    void SetAllowUntestedFirmware(bool enabled);

    void SetLightingRefreshRate(LightingRefreshRate refreshRate)
    {
    }

    bool TryGetVerifiedDevice(out IBladeDevice device);

    bool TryGetRecentlyVerifiedReadOnlyDevice(out IBladeDevice device) =>
        TryGetVerifiedDevice(out device);

    bool TryGetOwnedKeyTriggeredLightingDevice(out IBladeDevice device) =>
        TryGetVerifiedDevice(out device);

    bool TryGetOwnedAmbientLightingDevice(out IBladeDevice device) =>
        TryGetVerifiedDevice(out device);

    Task<LightingRendererOwnershipState> RelinquishLightingRendererAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(LightingRendererOwnershipState.None);

    Task<LightingRendererInvalidationResult> InvalidateLightingRendererAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(new LightingRendererInvalidationResult(
            LightingRendererOwnershipState.None,
            DriverModeRestoreOutcome.NotOwned));
}
