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

    Task EnsureDriverModeAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

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

    Task SetGpuClockOffsetsAsync(
        GpuClockOffsetSettings value,
        CancellationToken cancellationToken) => Task.FromException(
            new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "GPU clock tuning is unavailable."));

    Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken);

    Task SetFanControlAsync(
        FanControlSettings value,
        CancellationToken cancellationToken) => value.Mode is FanMode.Automatic or FanMode.Maximum
            ? SetFanModeAsync(value.Mode, cancellationToken)
            : Task.FromException(
                new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "Adjustable fan control is unavailable."));

    Task SetFanTargetsAsync(
        int fan1Rpm,
        int fan2Rpm,
        CancellationToken cancellationToken) => Task.FromException(
            new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Manual fan targets are unavailable."));

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

    Task PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken cancellationToken) => Task.FromException(
            new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Ambient Awareness input is unavailable."));
}
