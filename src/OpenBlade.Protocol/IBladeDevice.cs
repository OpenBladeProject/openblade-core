using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public interface IBladeDevice
{
    Task<DeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken);

    Task<ConfirmedDeviceState> ReadStateAsync(CancellationToken cancellationToken);

    Task<FanRpmReadings?> ReadFanRpmAsync(CancellationToken cancellationToken);

    Task<AdapterPowerState> ReadAdapterPowerStateAsync(
        CancellationToken cancellationToken);

    Task<string?> ReadMcuVersionAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    Task<bool?> ReadStartupAnimationAsync(CancellationToken cancellationToken) =>
        Task.FromResult<bool?>(null);

    Task<FunctionKeyPrimaryMode?> ReadFunctionKeyPrimaryAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<FunctionKeyPrimaryMode?>(null);

    Task<bool?> ReadGamingModeAsync(CancellationToken cancellationToken) =>
        Task.FromResult<bool?>(null);

    Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken);

    Task SetPerformanceAsync(
        PerformancePreset value,
        CustomPerformanceSettings? customPerformance,
        CancellationToken cancellationToken) => value == PerformancePreset.Custom
            ? Task.FromException(
                new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "Custom performance is unavailable."))
            : SetPerformanceAsync(value, cancellationToken);

    Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken);

    Task SetChargeLimitAsync(BatteryPolicy value, CancellationToken cancellationToken);

    Task SetBatteryProtectionOffAsync(CancellationToken cancellationToken);

    Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken);

    Task SetLightingAsync(LightingSettings value, CancellationToken cancellationToken);

    Task ApplyOneShotLaptopControlAsync(
        ApplyOneShotLaptopControl value,
        CancellationToken cancellationToken) => Task.FromException(
            new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "One-shot laptop controls are unavailable."));

    Task<LightingRendererOwnershipState> ReadLightingRendererOwnershipAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(LightingRendererOwnershipState.None);

    Task PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken cancellationToken) => Task.FromException(
            new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Reactive lighting input is unavailable."));
}
