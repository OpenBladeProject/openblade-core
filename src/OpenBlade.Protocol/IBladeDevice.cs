using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public interface IBladeDevice
{
    Task<DeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken);

    Task<ConfirmedDeviceState> ReadStateAsync(CancellationToken cancellationToken);

    Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken);

    Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken);

    Task SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken cancellationToken);

    Task SetLightingAsync(LightingSettings value, CancellationToken cancellationToken);
}
