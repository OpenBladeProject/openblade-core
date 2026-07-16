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

public interface IServiceClient
{
    Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token);

    Task<ServiceStatusSnapshot> ObserveStateAsync(CancellationToken cancellationToken);

    Task<DiagnosticArchive> ExportDiagnosticsAsync(CancellationToken token);

    Task<ControlOperationResult> UpdateProfilesAsync(
        ProfileSet profiles,
        CancellationToken token);

    Task<ControlOperationResult> SetPerformanceOverrideAsync(
        ManualOverride<PerformancePreset>? value,
        CancellationToken token);

    Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        CancellationToken token);

    Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
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

    IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(CancellationToken token);
}
