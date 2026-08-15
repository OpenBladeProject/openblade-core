using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public readonly record struct PowerTransitionDeviceStateReadResult(
    ConfirmedDeviceState State,
    bool LightingOffConfirmationUnavailable);

public interface IBladeDevice
{
    Task<DeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken);

    Task<ConfirmedDeviceState> ReadStateAsync(CancellationToken cancellationToken);

    Task<ConfirmedDeviceState> ReadLightingStateAsync(
        CancellationToken cancellationToken) =>
        ReadStateAsync(cancellationToken);

    Task<PowerTransitionDeviceStateReadResult> ReadStateForPowerTransitionAsync(
        CancellationToken cancellationToken);

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

    Task EnsureDriverModeAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task<bool?> ReadDriverModeAsync(CancellationToken cancellationToken) =>
        Task.FromResult<bool?>(null);

    async Task<bool?> MaintainDriverModeAsync(CancellationToken cancellationToken)
    {
        await EnsureDriverModeAsync(cancellationToken).ConfigureAwait(false);
        return await ReadDriverModeAsync(cancellationToken).ConfigureAwait(false);
    }

    Task<bool?> ReleaseDriverModeAsync(CancellationToken cancellationToken) =>
        Task.FromResult<bool?>(null);

    Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken);

    Task SetPerformanceAsync(
        PerformancePreset value,
        CustomPerformanceSettings? customPerformance,
        CancellationToken cancellationToken);

    Task SetPerformanceAsync(
        PerformancePreset value,
        CustomPerformanceSettings? customPerformance,
        GpuClockOffsetSettings? gpuClockOffsets,
        CancellationToken cancellationToken);

    Task SetGpuClockOffsetsAsync(
        GpuClockOffsetSettings value,
        CancellationToken cancellationToken);

    Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken);

    Task SetFanControlAsync(
        FanControlSettings value,
        CancellationToken cancellationToken);

    Task SetFanTargetsAsync(
        int fan1Rpm,
        int fan2Rpm,
        CancellationToken cancellationToken);

    Task SetChargeLimitAsync(BatteryPolicy value, CancellationToken cancellationToken);

    Task SetBatteryProtectionOffAsync(CancellationToken cancellationToken);

    Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken);

    Task SetLightingAsync(LightingSettings value, CancellationToken cancellationToken);

    Task<byte> StepKeyboardBrightnessAsync(
        KeyboardBrightnessDirection direction,
        CancellationToken cancellationToken);

    Task ApplyOneShotLaptopControlAsync(
        ApplyOneShotLaptopControl value,
        CancellationToken cancellationToken);

    Task<LightingRendererOwnershipState> ReadLightingRendererOwnershipAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(LightingRendererOwnershipState.None);

    Task PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken cancellationToken);

    Task PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken cancellationToken);

    Task SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken cancellationToken);
}
