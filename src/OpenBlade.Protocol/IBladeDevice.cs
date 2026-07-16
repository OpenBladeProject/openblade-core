using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public interface IBladeDevice
{
    Task<DeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken);

    Task<ConfirmedDeviceState> ReadStateAsync(CancellationToken cancellationToken);

    Task<int?> ReadFanRpmAsync(CancellationToken cancellationToken);

    Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken);

    Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken);

    Task SetChargeLimitAsync(BatteryPolicy value, CancellationToken cancellationToken);

    Task SetBatteryProtectionOffAsync(CancellationToken cancellationToken);

    Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken);

    Task SetLightingAsync(LightingSettings value, CancellationToken cancellationToken);
}
