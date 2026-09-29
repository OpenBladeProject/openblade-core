using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public readonly record struct ProductionPerformanceFanState(
    PerformancePreset? Performance,
    FanMode? Fans)
{
    public bool HyperBoostActive { get; init; }

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

    bool SupportsRz090581CapabilityDiagnosticsQuery => false;

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

    Task<Rz090581CapabilityDiagnostics?> QueryRz090581CapabilityDiagnosticsAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<Rz090581CapabilityDiagnostics?>(null);

    Task SetPerformanceAsync(
        PerformancePreset value,
        CancellationToken cancellationToken);

    Task SetHyperBoostAsync(bool enabled, CancellationToken cancellationToken) =>
        Task.FromException(new CapabilityUnavailableException(
            "HyperBoost control is unavailable."));

    Task SetHyperBoostAsync(
        bool enabled,
        PerformancePreset baseMode,
        CancellationToken cancellationToken) =>
        SetHyperBoostAsync(enabled, cancellationToken);

    // Submits only under the journaled cooling-pad path; an entry never selects the other path.
    Task SetHyperBoostAsync(
        bool enabled,
        PerformancePreset baseMode,
        bool requiresCoolingPad,
        CancellationToken cancellationToken) =>
        SetHyperBoostAsync(enabled, baseMode, cancellationToken);

    Task SetCustomPerformanceAsync(
        CustomPerformanceSettings value,
        CancellationToken cancellationToken);

    Task SetFanModeAsync(
        FanMode value,
        CancellationToken cancellationToken);

    Task SetFanControlAsync(
        FanControlSettings value,
        CancellationToken cancellationToken);

    Task SetFanTargetsAsync(
        int fan1Rpm,
        int fan2Rpm,
        CancellationToken cancellationToken);

    Task SetChargeLimitAsync(int percent, CancellationToken cancellationToken);

    Task SetProtectionOffAsync(CancellationToken cancellationToken);

    Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken);

    Task ApplyOneShotLaptopControlAsync(
        ApplyOneShotLaptopControl value,
        CancellationToken cancellationToken);
}

internal interface IPadlessHyperBoostLifecycleValidationPort
{
    Task EnablePadlessHyperBoostLifecycleValidationAsync(
        CancellationToken cancellationToken);
}

public interface IProductionBatteryPortFactory
{
    DeviceCapabilities Capabilities { get; }

    Task<HidSessionResult<IProductionBatteryPort>> OpenAsync(
        CancellationToken cancellationToken);
}
