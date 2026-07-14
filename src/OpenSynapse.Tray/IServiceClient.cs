using OpenSynapse.Contracts;

namespace OpenSynapse.Tray;

public interface IServiceClient
{
    Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token);

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

    Task<ControlOperationResult> SetFullChargeOverrideAsync(
        bool enabled,
        CancellationToken token);

    IAsyncEnumerable<ServiceStatusSnapshot> WatchStatusAsync(CancellationToken token);
}
