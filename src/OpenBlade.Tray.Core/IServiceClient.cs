using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public abstract record ServiceWatchUpdate
{
    private ServiceWatchUpdate()
    {
    }

    public sealed record Connected(ServiceStatusSnapshot Status) : ServiceWatchUpdate;

    public sealed record Reconnecting : ServiceWatchUpdate;
}

public interface IServiceStatusWatcher
{
    IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(CancellationToken token);
}

public interface IServiceClient : IServiceStatusWatcher
{
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

    Task<ControlOperationResult> SetHyperBoostAsync(
        bool enabled,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "HyperBoost control is unavailable."));

    Task<ControlOperationResult> SetCoolingPadFanIntentAsync(
        CoolingPadFanIntent intent,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Cooling-pad fan intent is unavailable."));

    Task<ControlOperationResult> SetCoolingDemandLeaseAsync(
        Guid leaseId,
        CoolingDemandLevel demand,
        TimeSpan lifetime,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "External cooling demand is unavailable."));

    Task<ControlOperationResult> ReleaseCoolingDemandLeaseAsync(
        Guid leaseId,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Stored,
                "No matching cooling-demand lease was active."));

    Task<ControlOperationResult> SetCoolingDemandApiEnabledAsync(
        bool enabled,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "The cooling-demand API is unavailable."));

    Task<ControlOperationResult> SetCoolingPadLightingSyncAsync(
        bool enabled,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Cooling-pad lighting synchronization is unavailable."));

    Task<ControlOperationResult> AcknowledgeCoolingPadLightingRecoveryAsync(
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Cooling-pad lighting recovery acknowledgement is unavailable."));

    Task<ControlOperationResult> SetPerformanceConfigurationOverrideAsync(
        PerformanceOverride? value,
        CancellationToken token);

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

    Task<ControlOperationResult> SetLightingRefreshRateAsync(
        LightingRefreshRate refreshRate,
        CancellationToken token);

    Task<ControlOperationResult> SetFirmwareCompatibilityOverrideAsync(
        bool enabled,
        CancellationToken token);

    Task<ControlOperationResult> ReapplyChangedSettingsAsync(CancellationToken token);

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
