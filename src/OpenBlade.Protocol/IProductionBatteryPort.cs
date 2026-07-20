using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public readonly record struct ProductionPerformanceFanState(
    PerformancePreset? Performance,
    FanMode? Fans)
{
    public CustomPerformanceSettings? CustomPerformance { get; init; }
}

public interface IProductionBatteryPort : IAsyncDisposable
{
    DeviceCapabilities Capabilities { get; }

    bool SupportsTemporaryFullChargeQuery { get; }

    bool SupportsMcuVersionQuery => false;

    bool SupportsStartupAnimationQuery => false;

    bool SupportsFunctionKeyPrimaryQuery => false;

    bool SupportsGamingModeQuery => false;

    Task<ConfirmedBatteryState> QueryProtectionAsync(CancellationToken cancellationToken);

    Task<bool?> QueryTemporaryFullChargeAsync(CancellationToken cancellationToken);

    Task<ProductionPerformanceFanState> QueryPerformanceFanStateAsync(
        CancellationToken cancellationToken);

    Task<AdapterPowerState> QueryAdapterPowerStateAsync(
        CancellationToken cancellationToken);

    Task<string?> QueryMcuVersionAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    Task<bool?> QueryStartupAnimationAsync(CancellationToken cancellationToken) =>
        Task.FromResult<bool?>(null);

    Task<FunctionKeyPrimaryMode?> QueryFunctionKeyPrimaryAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<FunctionKeyPrimaryMode?>(null);

    Task<bool?> QueryGamingModeAsync(CancellationToken cancellationToken) =>
        Task.FromResult<bool?>(null);

    Task SetPerformanceAsync(
        PerformancePreset value,
        CancellationToken cancellationToken);

    Task SetCustomPerformanceAsync(
        CustomPerformanceSettings value,
        CancellationToken cancellationToken) => Task.FromException(
            new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Custom performance is unavailable."));

    Task SetFanModeAsync(
        FanMode value,
        CancellationToken cancellationToken);

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

    Task SetChargeLimitAsync(int percent, CancellationToken cancellationToken);

    Task SetProtectionOffAsync(CancellationToken cancellationToken);

    Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken);

    Task ApplyOneShotLaptopControlAsync(
        ApplyOneShotLaptopControl value,
        CancellationToken cancellationToken) => Task.FromException(
            new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "One-shot laptop controls are unavailable."));
}

public interface IProductionBatteryPortFactory
{
    DeviceCapabilities Capabilities { get; }

    Task<HidSessionResult<IProductionBatteryPort>> OpenAsync(
        CancellationToken cancellationToken);
}
