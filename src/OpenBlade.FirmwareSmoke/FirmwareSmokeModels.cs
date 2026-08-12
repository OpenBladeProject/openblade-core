using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.FirmwareSmoke;

public enum FirmwareSmokeOutcome
{
    Passed,
    Warning,
    Failed,
    Skipped,
}

public enum FirmwareComponent
{
    Bios,
    Ec,
    Mcu,
    Vbios,
}

public sealed record FirmwareIdentity(
    string Bios,
    string Ec,
    string Mcu,
    string Vbios);

public sealed record FirmwareSmokeCapabilities(
    IReadOnlyList<string> PerformancePresets,
    IReadOnlyList<string> FanModes,
    IReadOnlyList<string> LightingEffects,
    IReadOnlyList<string> ObservedLightingEffects,
    string? SpecialKeyBindingsAccess,
    string? WritableSpecialKeySlots,
    string? LampArrayLightingAccess,
    IReadOnlyList<int> ChargeLimits,
    bool SupportsBatteryProtectionOff,
    bool SupportsTemporaryFullCharge,
    IReadOnlyList<string> LogoLightingModes,
    IReadOnlyList<string> OneShotLaptopControls,
    CustomPerformanceCapabilities? CustomPerformance,
    CpuPowerLimitCapabilities? CpuPowerLimit,
    CpuVoltageOptimizerCapabilities? CpuVoltageOptimizer,
    GpuOverclockCapabilities? GpuOverclock,
    IReadOnlyList<PowerSourceBehaviorObservation> ObservedPowerSourceBehaviors);

public sealed record FirmwareSmokeControlState(
    string? Performance,
    string? Fans,
    bool? BatteryProtectionEnabled,
    int? ChargeLimitPercent,
    bool? TemporaryFullCharge,
    string? LogoLighting,
    string? DesiredLightingEffect,
    string LightingRendererOwnership,
    bool? StartupAnimationEnabled,
    string? FunctionKeyPrimary,
    bool? GamingModeEnabled,
    bool? KeyboardDriverModeActive,
    string ActivePowerSource);

public sealed record FirmwareSmokeLiveReadings(
    int? BatteryPowerFlowMilliwatts,
    int? Fan1Rpm,
    int? Fan2Rpm,
    int? CpuTemperatureCelsius,
    int? GpuTemperatureCelsius);

public sealed record InstalledComponent(
    string Name,
    string? ProductVersion,
    long? Length);

public sealed record FirmwareSmokeRuntime(
    int ServiceProcessCount,
    int SessionAgentProcessCount,
    int TrayProcessCount,
    IReadOnlyList<InstalledComponent> InstalledComponents);

public sealed record FirmwareSmokeSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    int ObservationAttempts,
    string TargetModel,
    string? ServiceVersion,
    bool DeviceVerified,
    bool WritesBlocked,
    IReadOnlyList<string> PotentialControllers,
    string? ControllerDetectionWarning,
    IReadOnlyList<string> ExternalChanges,
    IReadOnlyList<string> UnconfirmedWrites,
    string Message,
    FirmwareIdentity Firmware,
    FirmwareSmokeCapabilities Capabilities,
    FirmwareSmokeControlState Controls,
    FirmwareSmokeLiveReadings LiveReadings,
    FirmwareSmokeRuntime Runtime);

public sealed record FirmwareSmokeCheck(
    string Id,
    string Description,
    FirmwareSmokeOutcome Outcome,
    string Expected,
    string Actual);

public sealed record FirmwareSmokeReport(
    string Phase,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<FirmwareSmokeCheck> Checks,
    IReadOnlyList<string> ManualChecks)
{
    [JsonIgnore]
    public bool Succeeded => Checks.All(check => check.Outcome != FirmwareSmokeOutcome.Failed);
}

public static class FirmwareSmokeFiles
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    public static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static async Task<T> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return await JsonSerializer.DeserializeAsync<T>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"{typeof(T).Name} is missing.");
    }

    public static async Task WriteReportAsync(
        string path,
        FirmwareSmokeReport report,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>
        {
            $"# OpenBlade firmware smoke report: {report.Phase}",
            string.Empty,
            $"- Created: `{report.CreatedAtUtc:O}`",
            $"- Result: **{(report.Succeeded ? "PASS" : "FAIL")}**",
            string.Empty,
            "| Result | Check | Expected | Actual |",
            "| --- | --- | --- | --- |",
        };
        lines.AddRange(report.Checks.Select(check =>
            $"| {check.Outcome} | {Escape(check.Description)} | " +
            $"{Escape(check.Expected)} | {Escape(check.Actual)} |"));
        if (report.ManualChecks.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("## Guided physical checks");
            lines.Add(string.Empty);
            lines.AddRange(report.ManualChecks.Select(item => $"- [ ] {item}"));
        }

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllLinesAsync(fullPath, lines, cancellationToken).ConfigureAwait(false);
    }

    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
