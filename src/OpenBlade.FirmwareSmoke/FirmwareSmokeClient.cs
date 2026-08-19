using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.FirmwareSmoke;

public interface IFirmwareSmokeClient : IAsyncDisposable
{
    Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken);

    Task<ServiceStatusSnapshot> ObserveStateAsync(
        LiveReadingSensors sensors,
        CancellationToken cancellationToken);

    Task<DiagnosticArchive> ExportDiagnosticsAsync(
        bool liveReadingsEnabled,
        CancellationToken cancellationToken);

    Task<ControlOperationResult> SetPerformanceOverrideAsync(
        PerformanceOverride? value,
        CancellationToken cancellationToken);

    Task<ControlOperationResult> SetHyperBoostAsync(
        bool enabled,
        CancellationToken cancellationToken);

    Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        FanControlSettings? settings,
        CancellationToken cancellationToken);

    Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken cancellationToken);

    Task<ControlOperationResult> SetBatteryPolicyAsync(
        BatteryPolicy value,
        CancellationToken cancellationToken);

    Task<ControlOperationResult> ApplyLaptopControlAsync(
        ApplyOneShotLaptopControl value,
        CancellationToken cancellationToken);
}

public sealed class FirmwareSmokeClient : IFirmwareSmokeClient
{
    private readonly ServiceClient client = new();

    public Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken) =>
        client.GetStatusAsync(cancellationToken);

    public Task<ServiceStatusSnapshot> ObserveStateAsync(
        LiveReadingSensors sensors,
        CancellationToken cancellationToken) =>
        client.ObserveStateAsync(sensors, cancellationToken);

    public Task<DiagnosticArchive> ExportDiagnosticsAsync(
        bool liveReadingsEnabled,
        CancellationToken cancellationToken) =>
        client.ExportDiagnosticsAsync(liveReadingsEnabled, cancellationToken);

    public Task<ControlOperationResult> SetPerformanceOverrideAsync(
        PerformanceOverride? value,
        CancellationToken cancellationToken) =>
        client.SetPerformanceConfigurationOverrideAsync(value, cancellationToken);

    public Task<ControlOperationResult> SetHyperBoostAsync(
        bool enabled,
        CancellationToken cancellationToken) =>
        client.SetHyperBoostAsync(enabled, cancellationToken);

    public Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        FanControlSettings? settings,
        CancellationToken cancellationToken) =>
        client.SetFanControlOverrideAsync(value, settings, cancellationToken);

    public Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken cancellationToken) =>
        client.SetLightingOverrideAsync(value, cancellationToken);

    public Task<ControlOperationResult> SetBatteryPolicyAsync(
        BatteryPolicy value,
        CancellationToken cancellationToken) =>
        client.SetBatteryPolicyAsync(value, cancellationToken);

    public Task<ControlOperationResult> ApplyLaptopControlAsync(
        ApplyOneShotLaptopControl value,
        CancellationToken cancellationToken) =>
        client.ApplyOneShotLaptopControlAsync(value, cancellationToken);

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
