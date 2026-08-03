using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Testing;

internal abstract class UnavailableServiceClient : IServiceClient
{
    public virtual IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
        CancellationToken token) =>
        throw new NotSupportedException();

    public virtual Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token) =>
        throw new NotSupportedException();

    public virtual Task<ServiceStatusSnapshot> ObserveStateAsync(
        bool includeLiveReadings,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<ServiceStatusSnapshot> ObserveStateAsync(
        LiveReadingSensors requestedLiveReadings,
        CancellationToken cancellationToken) => ObserveStateAsync(
            requestedLiveReadings != LiveReadingSensors.None,
            cancellationToken);

    public virtual Task<DiagnosticArchive> ExportDiagnosticsAsync(
        bool liveReadingsEnabled,
        CancellationToken token) =>
        throw new NotSupportedException();

    public virtual Task<ControlOperationResult> UpdateProfilesAsync(
        ProfileSet profiles,
        CancellationToken token) =>
        Unavailable("Profile updates are unavailable.");

    public virtual Task<ControlOperationResult> SetPerformanceOverrideAsync(
        ManualOverride<PerformancePreset>? value,
        CancellationToken token) =>
        Unavailable("Performance overrides are unavailable.");

    public virtual Task<ControlOperationResult> SetPerformanceConfigurationOverrideAsync(
        PerformanceOverride? value,
        CancellationToken token) => value is { Value: PerformancePreset.Custom }
            ? Unavailable("Custom performance is unavailable.")
            : SetPerformanceOverrideAsync(
                value is null
                    ? null
                    : new ManualOverride<PerformancePreset>(value.Value, value.Lifetime),
                token);

    public virtual Task<ControlOperationResult> CyclePerformanceShortcutAsync(
        PerformanceShortcutSource source,
        CancellationToken token) =>
        Unavailable("Performance shortcuts are unavailable.");

    public virtual Task<ControlOperationResult> SetPerformanceShortcutPreviewAsync(
        Guid sessionId,
        long generation,
        PerformancePreset? preset,
        CancellationToken token) =>
        Unavailable("Performance shortcut previews are unavailable.");

    public virtual Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        CancellationToken token) =>
        Unavailable("Fan overrides are unavailable.");

    public virtual Task<ControlOperationResult> SetFanControlOverrideAsync(
        ManualOverride<FanMode>? value,
        FanControlSettings? settings,
        CancellationToken token) =>
        SetFanOverrideAsync(value, token);

    public virtual Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken token) =>
        Unavailable("Lighting overrides are unavailable.");

    public virtual Task<ControlOperationResult> StepKeyboardBrightnessAsync(
        KeyboardBrightnessDirection direction,
        OverrideLifetime lifetime,
        CancellationToken token) =>
        Unavailable("Keyboard brightness stepping is unavailable.");

    public virtual Task<ControlOperationResult> SetKeyboardLightingControlModeAsync(
        KeyboardLightingControlMode mode,
        CancellationToken token) =>
        Unavailable("Keyboard lighting ownership selection is unavailable.");

    public virtual Task<ControlOperationResult> SetExternalLightingOverridePolicyAsync(
        bool enabled,
        CancellationToken token) =>
        Unavailable("External lighting override policy is unavailable.");

    public virtual Task<ControlOperationResult> SetLightingSuppressionAsync(
        LightingSuppressionReason reason,
        bool suppressed,
        CancellationToken token) =>
        Unavailable("Lighting power policy is unavailable.");

    public virtual Task<ControlOperationResult> PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken token) =>
        Unavailable("Reactive lighting input is unavailable.");

    public virtual Task<ControlOperationResult> PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken token) =>
        Unavailable("Ambient Awareness input is unavailable.");

    public virtual Task<ControlOperationResult> SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken token) =>
        Unavailable("Playback mute indication is unavailable.");

    public virtual Task<ControlOperationResult> SetBatteryPolicyAsync(
        BatteryPolicy value,
        CancellationToken token) =>
        Unavailable("Battery policy is unavailable.");

    public virtual Task<ControlOperationResult> SetBatteryProtectionAsync(
        bool enabled,
        CancellationToken token) =>
        Unavailable("Battery protection is unavailable.");

    public virtual Task<ControlOperationResult> SetFullChargeOverrideAsync(
        bool enabled,
        CancellationToken token) =>
        Unavailable("Full charge override is unavailable.");

    public virtual Task<ControlOperationResult> ApplyOneShotLaptopControlAsync(
        ApplyOneShotLaptopControl value,
        CancellationToken token) =>
        Unavailable("One-shot laptop controls are unavailable.");

    public virtual Task<ControlOperationResult> ShutdownOpenBladeAsync(
        CancellationToken token) =>
        Unavailable("OpenBlade shutdown is unavailable.");

    private static Task<ControlOperationResult> Unavailable(string message) =>
        Task.FromResult(new ControlOperationResult(ControlOperationStatus.Blocked, message));
}
