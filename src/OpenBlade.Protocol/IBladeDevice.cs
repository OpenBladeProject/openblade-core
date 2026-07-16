using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public interface IBladeDevice
{
    Task<DeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken);

    Task<ConfirmedDeviceState> ReadStateAsync(CancellationToken cancellationToken);

    Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken);

    Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken);

    Task SetChargeLimitAsync(BatteryPolicy value, CancellationToken cancellationToken) =>
        Task.FromException(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "The device does not admit charge-limit writes."));

    Task SetBatteryProtectionOffAsync(CancellationToken cancellationToken) =>
        Task.FromException(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "The device does not admit battery-protection writes."));

    Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken) =>
        Task.FromException(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "The device does not admit temporary-full-charge writes."));

    Task SetLightingAsync(LightingSettings value, CancellationToken cancellationToken);
}
