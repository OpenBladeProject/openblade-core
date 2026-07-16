using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public interface IDiagnosticArchiveProvider
{
    Task<DiagnosticArchive> CreateAsync(CancellationToken cancellationToken);
}

public sealed record DiagnosticHostMetadata(
    DiagnosticVersions Versions,
    IReadOnlyCollection<string> SensitiveTokens,
    DateTimeOffset CapturedAt);

public interface IDiagnosticMetadataSource
{
    DiagnosticHostMetadata Capture();
}

public interface IAuthorizedUserTokenSource
{
    IReadOnlyCollection<string> ReadTokens();
}

public sealed class WindowsAuthorizedUserTokenSource : IAuthorizedUserTokenSource
{
    private const string AuthorizedUserEnvironmentVariable = "OPENBLADE_USER_SID";
    private readonly Func<string?> readAuthorizedSid;
    private readonly Func<SecurityIdentifier, string?> translateAccount;
    private readonly Func<SecurityIdentifier, string?> readProfilePath;

    public WindowsAuthorizedUserTokenSource()
        : this(ReadAuthorizedUserSid, TranslateAccount, ReadProfilePath)
    {
    }

    internal WindowsAuthorizedUserTokenSource(
        Func<string?> readAuthorizedSid,
        Func<SecurityIdentifier, string?> translateAccount,
        Func<SecurityIdentifier, string?> readProfilePath)
    {
        this.readAuthorizedSid = readAuthorizedSid
            ?? throw new ArgumentNullException(nameof(readAuthorizedSid));
        this.translateAccount = translateAccount
            ?? throw new ArgumentNullException(nameof(translateAccount));
        this.readProfilePath = readProfilePath
            ?? throw new ArgumentNullException(nameof(readProfilePath));
    }

    public IReadOnlyCollection<string> ReadTokens()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        string? sidText;
        try
        {
            sidText = readAuthorizedSid();
        }
        catch (Exception exception) when (IsSafeMetadataFailure(exception))
        {
            return [];
        }

        Add(tokens, sidText);
        if (string.IsNullOrWhiteSpace(sidText))
        {
            return tokens.ToArray();
        }

        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(sidText);
        }
        catch (ArgumentException)
        {
            return tokens.ToArray();
        }

        try
        {
            string? account = translateAccount(sid);
            Add(tokens, account);
            if (!string.IsNullOrWhiteSpace(account))
            {
                int separator = account.LastIndexOf('\\');
                Add(tokens, separator >= 0 ? account[(separator + 1)..] : account);
            }
        }
        catch (Exception exception) when (IsSafeMetadataFailure(exception))
        {
        }

        try
        {
            string? profilePath = readProfilePath(sid);
            Add(tokens, profilePath);
            if (!string.IsNullOrWhiteSpace(profilePath))
            {
                Add(tokens, Path.GetFileName(profilePath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)));
            }
        }
        catch (Exception exception) when (IsSafeMetadataFailure(exception))
        {
        }

        return tokens.ToArray();
    }

    private static string? ReadAuthorizedUserSid()
    {
        string? configured = Environment.GetEnvironmentVariable(
            AuthorizedUserEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenBlade",
            "authorized-user.sid");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static string? TranslateAccount(SecurityIdentifier sid) =>
        (sid.Translate(typeof(NTAccount)) as NTAccount)?.Value;

    private static string? ReadProfilePath(SecurityIdentifier sid)
    {
        using RegistryKey? profile = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid.Value,
            writable: false);
        return profile?.GetValue("ProfileImagePath") is string path
            ? Environment.ExpandEnvironmentVariables(path)
            : null;
    }

    private static bool IsSafeMetadataFailure(Exception exception) =>
        exception is SystemException;

    private static void Add(ISet<string> tokens, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            tokens.Add(value);
        }
    }
}

public sealed class WindowsDiagnosticMetadataSource : IDiagnosticMetadataSource
{
    private readonly IAuthorizedUserTokenSource authorizedUserTokenSource;

    public WindowsDiagnosticMetadataSource()
        : this(new WindowsAuthorizedUserTokenSource())
    {
    }

    public WindowsDiagnosticMetadataSource(
        IAuthorizedUserTokenSource authorizedUserTokenSource)
    {
        this.authorizedUserTokenSource = authorizedUserTokenSource
            ?? throw new ArgumentNullException(nameof(authorizedUserTokenSource));
    }

    public DiagnosticHostMetadata Capture() => new(
        new DiagnosticVersions(
            ReadOpenBladeVersion(),
            Environment.OSVersion.VersionString,
            ReadBiosVersion()),
        ReadSensitiveTokens(),
        DateTimeOffset.UtcNow);

    private static string ReadOpenBladeVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(DiagnosticArchiveProvider).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "Unavailable";
    }

    private static string ReadBiosVersion()
    {
        try
        {
            using RegistryKey? bios = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\BIOS",
                writable: false);
            object? value = bios?.GetValue("BIOSVersion");
            return value switch
            {
                string text when !string.IsNullOrWhiteSpace(text) => text,
                string[] values when values.Length > 0 => string.Join(" ", values),
                _ => "Unavailable",
            };
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "Unavailable";
        }
    }

    private IReadOnlyCollection<string> ReadSensitiveTokens()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (string token in authorizedUserTokenSource.ReadTokens())
        {
            Add(tokens, token);
        }

        Add(tokens, Environment.UserName);
        Add(tokens, Environment.MachineName);

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string systemProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Add(tokens, userProfile);
        Add(tokens, systemProfile);
        Add(tokens, programFiles);
        Add(tokens, commonData);
        Add(tokens, localData);
        Add(tokens, Path.Combine(programFiles, "OpenBlade"));
        Add(tokens, Path.Combine(commonData, "OpenBlade"));
        Add(tokens, Path.Combine(localData, "OpenBlade"));
        Add(tokens, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return tokens.ToArray();
    }

    private static void Add(ISet<string> tokens, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            tokens.Add(value);
        }
    }
}

public sealed class DiagnosticArchiveProvider : IDiagnosticArchiveProvider
{
    private readonly IServiceControlRuntime runtime;
    private readonly DiagnosticHistory history;
    private readonly IDiagnosticMetadataSource metadataSource;

    public DiagnosticArchiveProvider(
        IServiceControlRuntime runtime,
        DiagnosticHistory history)
        : this(runtime, history, new WindowsDiagnosticMetadataSource())
    {
    }

    public DiagnosticArchiveProvider(
        IServiceControlRuntime runtime,
        DiagnosticHistory history,
        IDiagnosticMetadataSource metadataSource)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.metadataSource = metadataSource
            ?? throw new ArgumentNullException(nameof(metadataSource));
    }

    public async Task<DiagnosticArchive> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ServiceStatusSnapshot status = runtime.GetStatus();
        DiagnosticHistorySnapshot historySnapshot = history.Snapshot();
        DiagnosticHostMetadata metadata = metadataSource.Capture();
        DiagnosticBundle bundle = new(
            metadata.Versions,
            new DiagnosticDeviceIdentity("RZ09-0581", "1532", "02E0", SerialNumber: null),
            CreateCapabilities(status.Capabilities),
            CreateConfiguration(status),
            historySnapshot.CommandResults,
            historySnapshot.LogLines,
            metadata.SensitiveTokens);

        await using var destination = new MemoryStream();
        await DiagnosticBundleExporter.ExportAsync(destination, bundle, cancellationToken)
            .ConfigureAwait(false);
        if (destination.Length > OpenBladeIpc.MaximumDiagnosticArchiveBytes)
        {
            throw new InvalidDataException("Diagnostic archive exceeds the 32 KiB limit.");
        }

        string fileName = $"OpenBlade-diagnostics-{metadata.CapturedAt.UtcDateTime:yyyyMMdd-HHmmssZ}.zip";
        return new DiagnosticArchive(fileName, destination.ToArray());
    }

    private static IReadOnlyDictionary<string, bool> CreateCapabilities(
        DeviceCapabilities? capabilities)
    {
        capabilities ??= DeviceCapabilities.Unavailable();
        var result = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (PerformancePreset value in Enum.GetValues<PerformancePreset>())
        {
            result[$"performance.{value}"] = capabilities.PerformancePresets.Contains(value);
        }

        foreach (FanMode value in Enum.GetValues<FanMode>())
        {
            result[$"fans.{value}"] = capabilities.FanModes.Contains(value);
        }

        foreach (LightingEffect value in Enum.GetValues<LightingEffect>())
        {
            result[$"lighting.{value}"] = capabilities.LightingEffects.Contains(value);
        }

        result["battery.chargeLimit"] = capabilities.ChargeLimits.Count > 0;
        result["battery.protectionOff"] = capabilities.SupportsBatteryProtectionOff;
        result["battery.temporaryFullCharge"] = capabilities.SupportsTemporaryFullCharge;
        return result;
    }

    private static IReadOnlyDictionary<string, string> CreateConfiguration(
        ServiceStatusSnapshot status)
    {
        DeviceCapabilities capabilities = status.Capabilities ?? DeviceCapabilities.Unavailable();
        ConfirmedDeviceState? confirmed = status.ConfirmedState;
        var configuration = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["status.revision"] = status.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["status.deviceVerified"] = status.DeviceVerified.ToString(),
            ["status.writesBlocked"] = status.WritesBlocked.ToString(),
            ["status.potentialControllers"] = string.Join(", ", status.PotentialControllers),
            ["status.controllerDetectionWarning"] =
                status.ControllerDetectionWarning ?? "Unavailable",
            ["status.externalChanges"] = string.Join(", ", status.ExternalChanges),
            ["status.message"] = status.Message,
            ["power.source"] = status.Telemetry.PowerSource.ToString(),
            ["profile.pluggedIn.performance"] = status.Profiles.PluggedIn.Performance.ToString(),
            ["profile.pluggedIn.fans"] = status.Profiles.PluggedIn.Fans.ToString(),
            ["profile.battery.performance"] = status.Profiles.Battery.Performance.ToString(),
            ["profile.battery.fans"] = status.Profiles.Battery.Fans.ToString(),
            ["profile.pluggedIn.lighting"] = status.Profiles.PluggedIn.Lighting.Effect.ToString(),
            ["profile.battery.lighting"] = status.Profiles.Battery.Lighting.Effect.ToString(),
            ["battery.chargeLimitPercent"] = status.Profiles.BatteryPolicy.ChargeLimitPercent
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["battery.protectionEnabled"] = status.DesiredState.Battery.ProtectionEnabled.ToString(),
            ["battery.temporaryFullCharge"] = status.FullChargeOverride.ToString(),
            ["desired.performance"] = status.DesiredState.Performance.Value.ToString(),
            ["desired.performance.source"] = status.DesiredState.Performance.Source.ToString(),
            ["desired.fans"] = status.DesiredState.Fans.Value.ToString(),
            ["desired.fans.source"] = status.DesiredState.Fans.Source.ToString(),
            ["desired.lighting"] = status.DesiredState.Lighting.Value.Effect.ToString(),
            ["desired.lighting.source"] = status.DesiredState.Lighting.Source.ToString(),
            ["confirmed.performance"] = EnumOrUnavailable(confirmed?.Performance),
            ["confirmed.fans"] = EnumOrUnavailable(confirmed?.Fans),
            ["confirmed.battery.chargeLimitPercent"] = NumberOrUnavailable(
                confirmed?.Battery?.ChargeLimitPercent),
            ["confirmed.battery.protectionEnabled"] = BoolOrUnavailable(
                confirmed?.Battery?.ProtectionEnabled),
            ["confirmed.battery.temporaryFullCharge"] = BoolOrUnavailable(
                confirmed?.Battery?.TemporaryFullCharge),
            ["confirmed.lighting.effect"] = EnumOrUnavailable(confirmed?.Lighting?.Effect),
            ["confirmed.lighting.brightness"] = NumberOrUnavailable(
                confirmed?.Lighting?.Brightness),
            ["telemetry.capturedAt"] = status.Telemetry.CapturedAt.ToUniversalTime().ToString("O"),
            ["telemetry.batteryPercent"] = NumberOrUnavailable(status.Telemetry.BatteryPercent),
            ["telemetry.cpuTemperatureCelsius"] = NumberOrUnavailable(
                status.Telemetry.CpuTemperatureCelsius),
            ["telemetry.gpuTemperatureCelsius"] = NumberOrUnavailable(
                status.Telemetry.GpuTemperatureCelsius),
            ["telemetry.fanRpm"] = NumberOrUnavailable(status.Telemetry.FanRpm),
            ["capability.chargeLimits"] = string.Join(
                ", ",
                capabilities.ChargeLimits.Order()),
            ["capability.supportsBatteryProtectionOff"] =
                capabilities.SupportsBatteryProtectionOff.ToString(),
            ["capability.supportsTemporaryFullCharge"] =
                capabilities.SupportsTemporaryFullCharge.ToString(),
            ["override.performance"] = status.PerformanceOverride is null
                ? "None"
                : $"{status.PerformanceOverride.Value} ({status.PerformanceOverride.Lifetime})",
            ["override.fans"] = status.FanOverride is null
                ? "None"
                : $"{status.FanOverride.Value} ({status.FanOverride.Lifetime})",
            ["override.lighting"] = status.LightingOverride is null
                ? "None"
                : $"{status.LightingOverride.Value.Effect} ({status.LightingOverride.Lifetime})",
        };

        return configuration;
    }

    private static string NumberOrUnavailable<T>(T? value)
        where T : struct, IFormattable => value?.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
            ?? "Unavailable";

    private static string BoolOrUnavailable(bool? value) =>
        value?.ToString() ?? "Unavailable";

    private static string EnumOrUnavailable<T>(T? value)
        where T : struct, Enum => value?.ToString() ?? "Unavailable";
}
