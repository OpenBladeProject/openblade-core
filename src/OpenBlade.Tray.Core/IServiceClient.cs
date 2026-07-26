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
        CancellationToken cancellationToken) => ObserveStateAsync(
            requestedLiveReadings != LiveReadingSensors.None,
            cancellationToken);

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
        CancellationToken token) => value is { Value: PerformancePreset.Custom }
            ? Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Custom performance is unavailable."))
            : SetPerformanceOverrideAsync(
                value is null
                    ? null
                    : new ManualOverride<PerformancePreset>(value.Value, value.Lifetime),
                token);

    Task<ControlOperationResult> CyclePerformanceShortcutAsync(
        PerformanceShortcutSource source,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Performance shortcuts are unavailable."));

    Task<ControlOperationResult> SetPerformanceShortcutPreviewAsync(
        Guid sessionId,
        long generation,
        PerformancePreset? preset,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Performance shortcut previews are unavailable."));

    Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        CancellationToken token);

    Task<ControlOperationResult> SetFanControlOverrideAsync(
        ManualOverride<FanMode>? value,
        FanControlSettings? settings,
        CancellationToken token) => SetFanOverrideAsync(value, token);

    Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken token);

    Task<ControlOperationResult> StepKeyboardBrightnessAsync(
        KeyboardBrightnessDirection direction,
        OverrideLifetime lifetime,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Keyboard brightness stepping is unavailable."));

    Task<ControlOperationResult> SetExternalLightingOverridePolicyAsync(
        bool enabled,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "External lighting override policy is unavailable."));

    Task<ControlOperationResult> SetLightingSuppressionAsync(
        LightingSuppressionReason reason,
        bool suppressed,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Lighting power policy is unavailable."));

    Task<ControlOperationResult> PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Reactive lighting input is unavailable."));

    Task<ControlOperationResult> PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Ambient Awareness input is unavailable."));

    Task<ControlOperationResult> SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Playback mute indication is unavailable."));

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
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "One-shot laptop controls are unavailable."));

    Task<ControlOperationResult> ShutdownOpenBladeAsync(CancellationToken token) =>
        Task.FromResult(new ControlOperationResult(
            ControlOperationStatus.Blocked,
            "OpenBlade shutdown is unavailable."));

}
