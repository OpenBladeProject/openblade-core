using System.Security.Principal;
using Microsoft.Win32;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public interface IDiagnosticArchiveProvider
{
    Task<DiagnosticArchive> CreateAsync(
        bool liveReadingsEnabled,
        CancellationToken cancellationToken);
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
    private readonly Func<IReadOnlyCollection<string>> readUserSids;
    private readonly Func<SecurityIdentifier, string?> translateAccount;
    private readonly Func<SecurityIdentifier, string?> readProfilePath;

    public WindowsAuthorizedUserTokenSource()
        : this(ReadKnownUserSids, TranslateAccount, ReadProfilePath)
    {
    }

    internal WindowsAuthorizedUserTokenSource(
        Func<string?> readAuthorizedSid,
        Func<SecurityIdentifier, string?> translateAccount,
        Func<SecurityIdentifier, string?> readProfilePath)
        : this(
            () => readAuthorizedSid() is { } sid ? [sid] : [],
            translateAccount,
            readProfilePath)
    {
        ArgumentNullException.ThrowIfNull(readAuthorizedSid);
    }

    internal WindowsAuthorizedUserTokenSource(
        Func<IReadOnlyCollection<string>> readUserSids,
        Func<SecurityIdentifier, string?> translateAccount,
        Func<SecurityIdentifier, string?> readProfilePath)
    {
        this.readUserSids = readUserSids
            ?? throw new ArgumentNullException(nameof(readUserSids));
        this.translateAccount = translateAccount
            ?? throw new ArgumentNullException(nameof(translateAccount));
        this.readProfilePath = readProfilePath
            ?? throw new ArgumentNullException(nameof(readProfilePath));
    }

    public IReadOnlyCollection<string> ReadTokens()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyCollection<string> userSids;
        try
        {
            userSids = readUserSids();
        }
        catch (Exception exception) when (IsSafeMetadataFailure(exception))
        {
            return [];
        }

        foreach (string sidText in userSids)
        {
            AddUserTokens(tokens, sidText);
        }

        return tokens.ToArray();
    }

    private void AddUserTokens(ISet<string> tokens, string? sidText)
    {
        Add(tokens, sidText);
        if (string.IsNullOrWhiteSpace(sidText))
        {
            return;
        }

        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(sidText);
        }
        catch (ArgumentException)
        {
            return;
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

    }

    private static IReadOnlyCollection<string> ReadKnownUserSids()
    {
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(sids, ReadAuthorizedUserSid());
        string usersDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenBlade",
            "users");
        if (Directory.Exists(usersDirectory))
        {
            foreach (string directory in Directory.EnumerateDirectories(usersDirectory))
            {
                string candidate = Path.GetFileName(directory);
                try
                {
                    var sid = new SecurityIdentifier(candidate);
                    if (sid.IsAccountSid())
                    {
                        sids.Add(sid.Value);
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return sids.ToArray();
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
    private readonly IFirmwareVersionSource firmwareVersionSource;

    public WindowsDiagnosticMetadataSource()
        : this(new WindowsAuthorizedUserTokenSource(), new WindowsFirmwareVersionSource())
    {
    }

    public WindowsDiagnosticMetadataSource(
        IAuthorizedUserTokenSource authorizedUserTokenSource)
        : this(authorizedUserTokenSource, new WindowsFirmwareVersionSource())
    {
    }

    public WindowsDiagnosticMetadataSource(
        IAuthorizedUserTokenSource authorizedUserTokenSource,
        IFirmwareVersionSource firmwareVersionSource)
    {
        this.authorizedUserTokenSource = authorizedUserTokenSource
            ?? throw new ArgumentNullException(nameof(authorizedUserTokenSource));
        this.firmwareVersionSource = firmwareVersionSource
            ?? throw new ArgumentNullException(nameof(firmwareVersionSource));
    }

    public DiagnosticHostMetadata Capture()
    {
        FirmwareVersions firmware = firmwareVersionSource.Capture();
        return new DiagnosticHostMetadata(
            new DiagnosticVersions(
                ReadOpenBladeVersion(),
                Environment.OSVersion.VersionString,
                firmware.BiosVersion,
                firmware.EcVersion,
                firmware.McuVersion,
                firmware.VbiosVersion),
            ReadSensitiveTokens(),
            DateTimeOffset.UtcNow);
    }

    private static string ReadOpenBladeVersion() => OpenBladeProductVersion.Read(
        typeof(DiagnosticArchiveProvider).Assembly);

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

    public async Task<DiagnosticArchive> CreateAsync(
        bool liveReadingsEnabled,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ServiceStatusSnapshot status = runtime.GetStatus();
        DiagnosticHistorySnapshot historySnapshot = history.Snapshot();
        DiagnosticHostMetadata metadata = metadataSource.Capture();
        DiagnosticVersions versions = metadata.Versions;
        if (status.Firmware is { } firmware
            && !string.IsNullOrWhiteSpace(firmware.McuVersion)
            && !string.Equals(
                firmware.McuVersion,
                "Unavailable",
                StringComparison.OrdinalIgnoreCase))
        {
            versions = versions with { McuVersion = firmware.McuVersion };
        }

        DiagnosticBundle bundle = new(
            versions,
            CreateDeviceIdentity(status),
            CreateCapabilities(status.Capabilities),
            CreateConfiguration(status, liveReadingsEnabled),
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

    private static DiagnosticDeviceIdentity CreateDeviceIdentity(
        ServiceStatusSnapshot status)
    {
        string? reportedModel = status.Firmware?.ModelNumber;
        RazerBladeModelInfo? model = RazerBladeModelCatalog.Find(reportedModel);
        return model is null
            ? new DiagnosticDeviceIdentity(
                string.IsNullOrWhiteSpace(reportedModel) ? "Unavailable" : reportedModel.Trim(),
                "Unavailable",
                "Unavailable",
                SerialNumber: null)
            : new DiagnosticDeviceIdentity(
                model.ModelNumber,
                model.UsbVendorId,
                model.UsbProductId,
                SerialNumber: null);
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
            result[$"lighting.observed.{value}"] =
                capabilities.ObservedLightingEffects.Contains(value);
        }

        result["battery.chargeLimit"] = capabilities.ChargeLimits.Count > 0;
        result["battery.protectionOff"] = capabilities.SupportsBatteryProtectionOff;
        result["battery.temporaryFullCharge"] = capabilities.SupportsTemporaryFullCharge;
        result["performance.customCombinations"] =
            capabilities.CustomPerformance?.IsWellFormed == true;
        result["performance.cpuPowerLimit.observed"] =
            capabilities.CpuPowerLimit?.IsWellFormed == true;
        result["performance.cpuPowerLimit.write"] =
            capabilities.CpuPowerLimit?.Access == DeviceFeatureAccess.ReadWrite;
        result["performance.cpuVoltageOptimizer.observed"] =
            capabilities.CpuVoltageOptimizer?.IsWellFormed == true;
        result["performance.cpuVoltageOptimizer.write"] =
            capabilities.CpuVoltageOptimizer?.Access == DeviceFeatureAccess.ReadWrite;
        result["performance.cpuVoltageOptimizer.enableDisableObserved"] =
            capabilities.CpuVoltageOptimizer?.SupportsEnableDisable == true;
        result["performance.gpuOverclock.observed"] =
            capabilities.GpuOverclock?.IsWellFormed == true;
        result["performance.gpuOverclock.write"] =
            capabilities.GpuOverclock?.Access == DeviceFeatureAccess.ReadWrite;
        result["performance.gpuOverclock.enableDisableObserved"] =
            capabilities.GpuOverclock?.SupportsEnableDisable == true;
        result["laptop.specialKeyBindings.observed"] =
            capabilities.SpecialKeyBindingsAccess is not null;
        result["laptop.specialKeyBindings.write"] =
            capabilities.SpecialKeyBindingsAccess == DeviceFeatureAccess.ReadWrite;
        foreach (SpecialKeySlotSet slot in new[]
        {
            SpecialKeySlotSet.M1,
            SpecialKeySlotSet.M2,
            SpecialKeySlotSet.M3,
            SpecialKeySlotSet.M4,
            SpecialKeySlotSet.M5,
        })
        {
            result[$"laptop.specialKeyBindings.write.{slot}"] =
                (capabilities.WritableSpecialKeySlots & slot) != 0;
        }
        result["lighting.lampArray.observed"] =
            capabilities.LampArrayLightingAccess is not null;
        result["lighting.lampArray.write"] =
            capabilities.LampArrayLightingAccess == DeviceFeatureAccess.ReadWrite;
        foreach (PowerSourceBehaviorObservation observation
            in capabilities.ObservedPowerSourceBehaviors)
        {
            string prefix = $"powerSource.{observation.Source}";
            result[$"{prefix}.observed"] = observation.IsWellFormed;
            result[$"{prefix}.fixedFansAvailable"] =
                observation.FixedFanControlAvailable == true;
            result[$"{prefix}.performanceModeAvailable"] =
                observation.PerformanceModeAvailable == true;
            result[$"{prefix}.customModeAvailable"] =
                observation.CustomModeAvailable == true;
            result[$"{prefix}.inadequatePowerWarningVisible"] =
                observation.InadequatePowerWarningVisible == true;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> CreateConfiguration(
        ServiceStatusSnapshot status,
        bool liveReadingsEnabled)
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
            ["power.source"] = status.ActivePowerSource.ToString(),
            ["liveReadings.enabled"] = liveReadingsEnabled.ToString(),
            ["liveReadings.available"] = HasAnyLiveReading(status.LiveReadings).ToString(),
            ["profile.pluggedIn.performance"] = status.Profiles.PluggedIn.Performance.ToString(),
            ["profile.pluggedIn.fans"] = status.Profiles.PluggedIn.Fans.ToString(),
            ["profile.battery.performance"] = status.Profiles.Battery.Performance.ToString(),
            ["profile.battery.fans"] = status.Profiles.Battery.Fans.ToString(),
            ["profile.usbC.performance"] = status.Profiles.UsbC.Performance.ToString(),
            ["profile.usbC.fans"] = status.Profiles.UsbC.Fans.ToString(),
            ["profile.pluggedIn.lighting"] = status.Profiles.PluggedIn.Lighting.Effect.ToString(),
            ["profile.battery.lighting"] = status.Profiles.Battery.Lighting.Effect.ToString(),
            ["profile.usbC.lighting"] = status.Profiles.UsbC.Lighting.Effect.ToString(),
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
            ["lighting.rendererOwnership"] =
                status.LightingRendererOwnership.ToString(),
            ["lighting.alwaysOverrideExternalChanges"] =
                status.AlwaysOverrideExternalLightingChanges.ToString(),
            ["firmware.allowUntested"] = status.AllowUntestedFirmware.ToString(),
            ["firmware.compatibility"] = status.FirmwareCompatibility.ToString(),
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
            ["capability.chargeLimits"] = string.Join(
                ", ",
                capabilities.ChargeLimits.Order()),
            ["capability.supportsBatteryProtectionOff"] =
                capabilities.SupportsBatteryProtectionOff.ToString(),
            ["capability.supportsTemporaryFullCharge"] =
                capabilities.SupportsTemporaryFullCharge.ToString(),
            ["capability.observedLightingEffects"] = string.Join(
                ", ",
                capabilities.ObservedLightingEffects
                    .Where(Enum.IsDefined)
                    .Order()),
            ["capability.specialKeyBindingsAccess"] =
                capabilities.SpecialKeyBindingsAccess?.ToString() ?? "Unavailable",
            ["capability.writableSpecialKeySlots"] =
                capabilities.WritableSpecialKeySlots.ToString(),
            ["capability.lampArrayLightingAccess"] =
                capabilities.LampArrayLightingAccess?.ToString() ?? "Unavailable",
            ["capability.fanSpeedMinimumRpm"] = NumberOrUnavailable(
                capabilities.FanSpeedRange?.MinimumRpm),
            ["capability.fanSpeedMaximumRpm"] = NumberOrUnavailable(
                capabilities.FanSpeedRange?.MaximumRpm),
            ["capability.fanSpeedStepRpm"] = NumberOrUnavailable(
                capabilities.FanSpeedRange?.StepRpm),
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

        foreach (PowerSourceBehaviorObservation observation
            in capabilities.ObservedPowerSourceBehaviors)
        {
            string prefix = $"capability.powerSource.{observation.Source}";
            configuration[$"{prefix}.selectedPerformance"] =
                observation.SelectedPerformance.ToString();
            configuration[$"{prefix}.selectedFans"] = observation.SelectedFans.ToString();
            configuration[$"{prefix}.fixedFansAvailable"] =
                BoolOrUnavailable(observation.FixedFanControlAvailable);
            configuration[$"{prefix}.performanceModeAvailable"] =
                BoolOrUnavailable(observation.PerformanceModeAvailable);
            configuration[$"{prefix}.customModeAvailable"] =
                BoolOrUnavailable(observation.CustomModeAvailable);
            configuration[$"{prefix}.inadequatePowerWarningVisible"] =
                BoolOrUnavailable(observation.InadequatePowerWarningVisible);
        }

        return configuration;
    }

    private static bool HasAnyLiveReading(LiveReadings? readings) =>
        readings is not null
        && (readings.BatteryPowerFlowMilliwatts is not null
            || readings.Fan1Rpm is not null
            || readings.Fan2Rpm is not null);

    private static string NumberOrUnavailable<T>(T? value)
        where T : struct, IFormattable => value?.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
            ?? "Unavailable";

    private static string BoolOrUnavailable(bool? value) =>
        value?.ToString() ?? "Unavailable";

    private static string EnumOrUnavailable<T>(T? value)
        where T : struct, Enum => value?.ToString() ?? "Unavailable";
}
