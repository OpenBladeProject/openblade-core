using OpenBlade.Contracts;

namespace OpenBlade.Tray;

// Presentation observations only: retained values never authorize hardware writes.
internal sealed record DeviceObservationState
{
    public DeviceCapabilities Capabilities { get; init; } = DeviceCapabilities.Unavailable();

    public bool? StartupAnimationEnabled { get; init; }

    public FunctionKeyPrimaryMode? FunctionKeyPrimary { get; init; }

    public bool? GamingModeEnabled { get; init; }

    public bool StartupAnimationReadPending { get; init; }

    public bool FunctionKeyReadPending { get; init; }

    public bool GamingModeReadPending { get; init; }

    public DeviceCapabilities ResolveCapabilities(
        ServiceStatusSnapshot status,
        bool hasPreviousStatus)
    {
        DeviceCapabilities incoming = status.Capabilities ?? DeviceCapabilities.Unavailable();
        return hasPreviousStatus
            && (IsCheckingLaptopConnection(status) || IsRetainedPowerSourceRecheck(status))
            && HasAnyCapability(Capabilities)
            && !HasAnyCapability(incoming)
                ? Capabilities
                : incoming;
    }

    public DeviceObservationState ObserveOneShotReadbacks(ServiceStatusSnapshot status)
    {
        bool supportsStartup = Capabilities.OneShotLaptopControls.Contains(
            OneShotLaptopControl.StartupAnimation);
        bool supportsFunctionKey = Capabilities.OneShotLaptopControls.Contains(
            OneShotLaptopControl.FunctionKeyPrimary);
        bool supportsGaming = Capabilities.OneShotLaptopControls.Contains(
            OneShotLaptopControl.GamingMode);

        return this with
        {
            StartupAnimationReadPending = supportsStartup && status.StartupAnimationEnabled is null,
            FunctionKeyReadPending = supportsFunctionKey && status.FunctionKeyPrimary is null,
            GamingModeReadPending = supportsGaming && status.GamingModeEnabled is null,
            StartupAnimationEnabled = supportsStartup
                ? status.StartupAnimationEnabled ?? StartupAnimationEnabled
                : status.StartupAnimationEnabled,
            FunctionKeyPrimary = supportsFunctionKey
                ? status.FunctionKeyPrimary ?? FunctionKeyPrimary
                : status.FunctionKeyPrimary,
            GamingModeEnabled = supportsGaming
                ? status.GamingModeEnabled ?? GamingModeEnabled
                : status.GamingModeEnabled,
        };
    }

    public static bool IsRetainedPowerSourceRecheck(ServiceStatusSnapshot? status) =>
        status is
        {
            DeviceVerified: true,
            WritesBlocked: true,
            ActivePowerSource: PowerSource.PluggedIn or PowerSource.UsbC,
            ObservationReason: DeviceObservationReason.RetainedPowerSource,
        };

    public static bool IsCheckingLaptopConnection(ServiceStatusSnapshot? status) =>
        status?.ObservationReason is
            DeviceObservationReason.CheckingConnection or DeviceObservationReason.RefreshingControls;

    private static bool HasAnyCapability(DeviceCapabilities capabilities) =>
        capabilities.PerformancePresets.Count > 0
        || capabilities.FanModes.Count > 0
        || capabilities.LightingEffects.Count > 0
        || capabilities.ChargeLimits.Count > 0
        || capabilities.SupportsBatteryProtectionOff
        || capabilities.SupportsTemporaryFullCharge
        || capabilities.HyperBoostAccess is not null
        || capabilities.HyperBoostWithoutCoolingPadAccess is not null
        || capabilities.LogoLightingModes.Count > 0
        || capabilities.OneShotLaptopControls.Count > 0;
}
