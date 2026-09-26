using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public abstract record ServiceWatchUpdate
{
    private ServiceWatchUpdate()
    {
    }

    public sealed record Connected(ServiceStatusSnapshot Status) : ServiceWatchUpdate
    {
        public bool SubscriptionOpened { get; init; }
    }

    public sealed record Reconnecting : ServiceWatchUpdate;
}

public interface IServiceStatusWatcher
{
    IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(CancellationToken token);
}

public interface IServiceClient : IServiceStatusWatcher
{
    Task<ControlOperationResult> ReportSessionPowerTransitionAsync(
        ReportSessionPowerTransitionRequest request,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Session power transition reporting is unavailable."));

    Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token);

    Task<ServiceStatusSnapshot> ObserveStateAsync(
        bool includeLiveReadings,
        CancellationToken cancellationToken);

    Task<ServiceStatusSnapshot> ObserveStateAsync(
        LiveReadingSensors requestedLiveReadings,
        CancellationToken cancellationToken);

    Task<DiagnosticArchive> ExportDiagnosticsAsync(
        bool liveReadingsEnabled,
        CancellationToken token);

    Task<ControlOperationResult> UpdateProfilesAsync(
        ProfileSet profiles,
        CancellationToken token);

    Task<ControlOperationResult> SetPerformanceOverrideAsync(
        ManualOverride<PerformancePreset>? value,
        CancellationToken token);

    Task<ControlOperationResult> SetPerformanceConfigurationOverrideAsync(
        PerformanceOverride? value,
        CancellationToken token);

    Task<ControlOperationResult> SetGpuClockTuningAsync(
        GpuClockTuningSettings value,
        OverrideLifetime lifetime,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "GPU clock tuning control is unavailable."));

    Task<ControlOperationResult> SetHyperBoostAsync(
        bool enabled,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "HyperBoost control is unavailable."));

    Task<ControlOperationResult> CyclePerformanceShortcutAsync(
        PerformanceShortcutSource source,
        CancellationToken token);

    Task<ControlOperationResult> SetPerformanceShortcutPreviewAsync(
        Guid sessionId,
        long generation,
        PerformancePreset? preset,
        CancellationToken token);

    Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        CancellationToken token);

    Task<ControlOperationResult> SetFanControlOverrideAsync(
        ManualOverride<FanMode>? value,
        FanControlSettings? settings,
        CancellationToken token);

    Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken token);

    Task<ControlOperationResult> StepKeyboardBrightnessAsync(
        KeyboardBrightnessDirection direction,
        OverrideLifetime lifetime,
        CancellationToken token);

    Task<ControlOperationResult> SetDisplayBrightnessHidUsageAsync(
        DisplayBrightnessHidUsage usage,
        CancellationToken token);

    Task<ControlOperationResult> SetKeyboardLightingControlModeAsync(
        KeyboardLightingControlMode mode,
        CancellationToken token);

    Task<ControlOperationResult> SetExternalLightingOverridePolicyAsync(
        bool enabled,
        CancellationToken token);

    Task<ControlOperationResult> SetThermalControlPolicyAsync(
        ThermalControlPolicy policy,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Thermal control policy is unavailable."));

    Task<ControlOperationResult> AcknowledgeThermalPolicyUpgradeDisclosureAsync(
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Thermal control policy disclosure is unavailable."));

    Task<ControlOperationResult> SetLightingRefreshRateAsync(
        LightingRefreshRate refreshRate,
        CancellationToken token);

    Task<ControlOperationResult> SetFirmwareCompatibilityOverrideAsync(
        bool enabled,
        CancellationToken token);

    Task<ControlOperationResult> ReapplyChangedSettingsAsync(CancellationToken token);

    Task<ControlOperationResult> RecoverHyperBoostRestoreAsync(CancellationToken token);

    Task<ControlOperationResult> SetLightingSuppressionAsync(
        LightingSuppressionReason reason,
        bool suppressed,
        CancellationToken token);

    Task<ControlOperationResult> PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken token);

    Task<ControlOperationResult> PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken token);

    Task<ControlOperationResult> SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken token);

    Task<ControlOperationResult> SetBatteryPolicyAsync(
        BatteryPolicy value,
        CancellationToken token);

    Task<ControlOperationResult> SetBatteryProtectionAsync(
        bool enabled,
        CancellationToken token);

    Task<ControlOperationResult> SetFullChargeOverrideAsync(
        bool enabled,
        CancellationToken token);

    Task<ControlOperationResult> ApplyOneShotLaptopControlAsync(
        ApplyOneShotLaptopControl value,
        CancellationToken token);

    Task<ControlOperationResult> ShutdownOpenBladeAsync(CancellationToken token);

}
