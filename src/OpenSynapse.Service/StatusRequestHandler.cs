using OpenSynapse.Contracts;
using OpenSynapse.Protocol;

namespace OpenSynapse.Service;

public sealed class StatusRequestHandler(IHardwareAccessGuard hardwareAccessGuard)
{
    public ServiceResponse Handle(ServiceRequest request)
    {
        if (request.Version != OpenSynapseIpc.ProtocolVersion)
        {
            return Failure(request, "Unsupported IPC protocol version.");
        }

        if (request is not GetStatusRequest)
        {
            return Failure(request, "Operation is unavailable until its device commands are validated.");
        }

        HardwareAccessDecision accessDecision = hardwareAccessGuard.Check();
        IReadOnlyList<string> conflicts = accessDecision.Conflicts
            .OrderBy(conflict => conflict, StringComparer.Ordinal)
            .ToArray();
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        PowerProfile activeProfile = profiles.PluggedIn;
        DesiredDeviceState desired = new(
            new ResolvedSetting<PerformancePreset>(
                activeProfile.Performance,
                SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(activeProfile.Fans, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(
                activeProfile.Lighting,
                SettingValueSource.PowerProfile),
            profiles.BatteryPolicy);
        ServiceStatusSnapshot status = new(
            Revision: 0,
            DeviceVerified: false,
            WritesBlocked: true,
            Conflicts: conflicts,
            Message: accessDecision.DetectionFailure is not null
                ? conflicts.Count == 0
                    ? $"Controller conflict detection failed: {accessDecision.DetectionFailure}"
                    : $"Controller conflict detection failed: {accessDecision.DetectionFailure} " +
                        $"Detected conflicts: {string.Join(", ", conflicts)}."
                : conflicts.Count == 0
                ? "PID 02E0 command fixtures have not been admitted."
                : $"Razer controller conflicts detected: {string.Join(", ", conflicts)}.",
            Capabilities: DeviceCapabilities.Unavailable(),
            ConfirmedState: null,
            Profiles: profiles,
            DesiredState: desired,
            Telemetry: new TelemetrySnapshot(
                DateTimeOffset.MinValue,
                PowerSource.PluggedIn,
                BatteryPercent: null,
                CpuTemperatureCelsius: null,
                GpuTemperatureCelsius: null,
                FanRpm: null),
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false);

        return new ServiceResponse(
            OpenSynapseIpc.ProtocolVersion,
            request.RequestId,
            Success: true,
            Error: null,
            Status: status,
            Result: null);
    }

    private static ServiceResponse Failure(ServiceRequest request, string error) => new(
        OpenSynapseIpc.ProtocolVersion,
        request.RequestId,
        Success: false,
        Error: error,
        Status: null,
        Result: null);
}
