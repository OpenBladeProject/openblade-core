using System.Diagnostics;
using OpenBlade.Contracts;

namespace OpenBlade.FirmwareSmoke;

public sealed class FirmwareSmokeSnapshotCollector(IFirmwareSmokeClient client)
{
    public const int CurrentSchemaVersion = 6;
    private const int MaximumObservationAttempts = 5;

    public async Task<FirmwareSmokeSnapshot> CaptureAsync(
        CancellationToken cancellationToken)
    {
        _ = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        ServiceStatusSnapshot? status = null;
        Exception? lastFailure = null;
        int observationAttempts = 0;
        for (int attempt = 1; attempt <= MaximumObservationAttempts; attempt++)
        {
            observationAttempts = attempt;
            try
            {
                status = await client.ObserveStateAsync(
                    LiveReadingSensors.All,
                    cancellationToken).ConfigureAwait(false);
                lastFailure = null;
                if (HasCompleteRequiredReadback(status))
                {
                    break;
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or TimeoutException)
            {
                lastFailure = exception;
            }

            if (attempt < MaximumObservationAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(250 * attempt),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (status is null)
        {
            throw new IOException(
                "The service did not return a firmware smoke observation.",
                lastFailure);
        }

        DeviceCapabilities capabilities =
            status.Capabilities ?? DeviceCapabilities.Unavailable();
        ConfirmedDeviceState? confirmed = status.ConfirmedState;
        FirmwareVersions firmware = status.Firmware ?? FirmwareVersions.Unavailable();
        LiveReadings readings = status.LiveReadings ?? new LiveReadings(null, null, null);

        return new FirmwareSmokeSnapshot(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            observationAttempts,
            ResolveTargetModel(status),
            status.ServiceVersion,
            status.DeviceVerified,
            status.WritesBlocked,
            status.PotentialControllers.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            status.ControllerDetectionWarning,
            status.ExternalChanges
                .Select(value => value.ToString())
                .Order(StringComparer.Ordinal)
                .ToArray(),
            status.Message,
            new FirmwareIdentity(
                firmware.BiosVersion,
                firmware.EcVersion,
                firmware.McuVersion,
                firmware.VbiosVersion),
            new FirmwareSmokeCapabilities(
                Names(capabilities.PerformancePresets),
                Names(capabilities.FanModes),
                Names(capabilities.LightingEffects),
                Names(capabilities.ObservedLightingEffects),
                capabilities.SpecialKeyBindingsAccess?.ToString(),
                capabilities.WritableSpecialKeySlots.ToString(),
                capabilities.LampArrayLightingAccess?.ToString(),
                capabilities.ChargeLimits.Order().ToArray(),
                capabilities.SupportsBatteryProtectionOff,
                capabilities.SupportsTemporaryFullCharge,
                Names(capabilities.LogoLightingModes),
                Names(capabilities.OneShotLaptopControls),
                capabilities.CustomPerformance,
                capabilities.CpuPowerLimit,
                capabilities.CpuVoltageOptimizer,
                capabilities.GpuOverclock,
                capabilities.ObservedPowerSourceBehaviors),
            new FirmwareSmokeControlState(
                confirmed?.Performance?.ToString(),
                confirmed?.Fans?.ToString(),
                confirmed?.Battery?.ProtectionEnabled,
                confirmed?.Battery?.ChargeLimitPercent,
                confirmed?.Battery?.TemporaryFullCharge,
                confirmed?.LogoLighting?.ToString(),
                status.DesiredState.Lighting.Value.Effect.ToString(),
                status.LightingRendererOwnership.ToString(),
                status.StartupAnimationEnabled,
                status.FunctionKeyPrimary?.ToString(),
                status.GamingModeEnabled,
                status.KeyboardDriverModeActive,
                status.ActivePowerSource.ToString()),
            new FirmwareSmokeLiveReadings(
                readings.BatteryPowerFlowMilliwatts,
                readings.Fan1Rpm,
                readings.Fan2Rpm,
                readings.CpuTemperatureCelsius,
                readings.GpuTemperatureCelsius),
            CaptureRuntime());
    }

    public async Task<DiagnosticArchive> ExportDiagnosticsAsync(
        CancellationToken cancellationToken) =>
        await client.ExportDiagnosticsAsync(
            liveReadingsEnabled: true,
            cancellationToken).ConfigureAwait(false);

    private static IReadOnlyList<string> Names<T>(IEnumerable<T> values)
        where T : struct, Enum =>
        values.Select(value => value.ToString())
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string ResolveTargetModel(ServiceStatusSnapshot status)
    {
        string? reportedModel = status.Firmware?.ModelNumber;
        return RazerBladeModelCatalog.Find(reportedModel)?.ModelNumber
            ?? (string.IsNullOrWhiteSpace(reportedModel)
                ? "Unavailable"
                : reportedModel.Trim());
    }

    private static bool HasCompleteRequiredReadback(ServiceStatusSnapshot status) =>
        status.DeviceVerified
        && !status.WritesBlocked
        && status.Capabilities is not null
        && status.ConfirmedState is
        {
            Performance: not null,
            Fans: not null,
            Battery: not null,
            LogoLighting: not null,
        }
        && status.StartupAnimationEnabled.HasValue
        && status.FunctionKeyPrimary.HasValue
        && status.GamingModeEnabled.HasValue
        && status.KeyboardDriverModeActive.HasValue
        && status.LiveReadings is
        {
            BatteryPowerFlowMilliwatts: not null,
            Fan1Rpm: not null,
            Fan2Rpm: not null,
            CpuTemperatureCelsius: not null,
        };

    private static FirmwareSmokeRuntime CaptureRuntime()
    {
        string installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "OpenBlade");
        string[] componentNames =
        [
            "OpenBlade.Service.exe",
            "OpenBlade.SessionAgent.exe",
            "OpenBlade.Tray.exe",
        ];
        var components = new List<InstalledComponent>();
        foreach (string name in componentNames)
        {
            string path = Path.Combine(installDirectory, name);
            if (!File.Exists(path))
            {
                components.Add(new InstalledComponent(name, null, null));
                continue;
            }

            var file = new FileInfo(path);
            components.Add(new InstalledComponent(
                name,
                FileVersionInfo.GetVersionInfo(path).ProductVersion,
                file.Length));
        }

        return new FirmwareSmokeRuntime(
            CountProcesses("OpenBlade.Service"),
            CountProcesses("OpenBlade.SessionAgent"),
            CountProcesses("OpenBlade.Tray"),
            components);
    }

    private static int CountProcesses(string name)
    {
        try
        {
            Process[] processes = Process.GetProcessesByName(name);
            try
            {
                return processes.Length;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            return -1;
        }
    }
}
