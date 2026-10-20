using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal sealed record ThxAudioAdmission(
    ThxAudioAvailability Availability,
    string? Error)
{
    public string? ProductId { get; init; }
    public string? ModelNumber { get; init; }
    public bool MutationAllowed { get; init; }
    public bool RequiresEndpointProbe { get; init; }
    public string DriverStackIdentity { get; init; } = string.Empty;
    public bool IsAvailable =>
        Availability is ThxAudioAvailability.Available or ThxAudioAvailability.AvailableReadOnly
        && ThxAudioCompatibility.Profiles.Any(profile =>
            profile.ProductId == ProductId && profile.ModelNumber == ModelNumber) && Error is null;
}

internal interface IThxAudioAdmissionSource
{
    ThxAudioAdmission Check();
}

internal sealed class WindowsThxAudioAdmissionSource : IThxAudioAdmissionSource
{
    internal const string NativeDllSha256 = ThxNativeComponentPackage.Sha256;
    internal const string NativeDllName = ThxNativeComponentPackage.FileName;
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string SystemInformationKey =
        @"SYSTEM\CurrentControlSet\Control\SystemInformation";

    public ThxAudioAdmission Check()
    {
        ThxAudioCompatibilityProfile? profile = null;
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            using Process currentProcess = Process.GetCurrentProcess();
            if (!OperatingSystem.IsWindows()
                || identity.User is null
                || identity.IsSystem
                || currentProcess.SessionId <= 0)
            {
                return Unsupported("THX audio requires an interactive Windows user session.");
            }

            profile = GetExactHostProfile(out string? exclusionReason);
            if (profile is null || !WindowsThxAdmissionQueries.HasExactDeviceIdentities(profile))
            {
                return Unsupported(exclusionReason ?? "The exact Razer model, USB identity, and signed-package audio codec could not be verified.");
            }

            if (IsCompetingControllerRunning())
            {
                return new ThxAudioAdmission(
                    ThxAudioAvailability.ControllerActive,
                    "Razer Synapse or THX Spatial Audio is active. Close it before controlling THX audio.")
                { ProductId = profile.ProductId, ModelNumber = profile.ModelNumber };
            }

            if (!HasCompatibleComponents(profile, out string? componentError, out string stackIdentity))
            {
                return new ThxAudioAdmission(
                    profile.Backend == ThxAudioNativeBackend.Legacy265
                        ? ThxAudioAvailability.LegacyComponentsUnavailable : ThxAudioAvailability.ComponentsUnavailable,
                    componentError)
                { ProductId = profile.ProductId, ModelNumber = profile.ModelNumber };
            }

            if (!profile.ReadAllowed)
            {
                return Unavailable("This model's THX native read contract is awaiting validation.")
                    with
                { ProductId = profile.ProductId, ModelNumber = profile.ModelNumber };
            }
            return new ThxAudioAdmission(profile.MutationAllowed
                ? ThxAudioAvailability.Available : ThxAudioAvailability.AvailableReadOnly, null)
            {
                ProductId = profile.ProductId,
                ModelNumber = profile.ModelNumber,
                MutationAllowed = profile.MutationAllowed,
                RequiresEndpointProbe = profile.Backend == ThxAudioNativeBackend.V4,
                DriverStackIdentity = stackIdentity,
            };
        }
        catch (Exception exception)
        {
            string error = $"THX audio admission could not be verified: {exception.GetType().Name}.";
            return profile is null ? Unsupported(error) : new ThxAudioAdmission(
                profile.Backend == ThxAudioNativeBackend.Legacy265
                    ? ThxAudioAvailability.LegacyComponentsUnavailable : ThxAudioAvailability.ComponentsUnavailable,
                error)
            { ProductId = profile.ProductId, ModelNumber = profile.ModelNumber };
        }
    }

    internal static string GetNativeDllPath() =>
        ThxNativeComponentPackage.TryResolve(out string? path)
            ? path! : File.Exists(ThxNativeComponentPackage.GetInstalledPath())
                ? ThxNativeComponentPackage.GetInstalledPath() : ThxNativeComponentPackage.GetSynapsePath();

    internal static bool HasPinnedNativeDll() => ThxNativeComponentPackage.HasVerifiedComponent();

    internal static bool HasPinnedNativeDll(string path, out string? error)
    {
        try
        {
            using var file = ThxNativeComponentPackage.OpenVerifiedRead(path);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            error = $"The pinned THX native component {NativeDllName} is missing.";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            error = $"The pinned THX native component {NativeDllName} is incompatible or unreadable.";
        }
        return false;
    }

    internal static ThxAudioCompatibilityProfile? GetExactHostProfile() => GetExactHostProfile(out _);

    private static ThxAudioCompatibilityProfile? GetExactHostProfile(out string? exclusionReason)
    {
        using RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        string? biosManufacturer = ReadString(localMachine, BiosKey, "SystemManufacturer");
        string? biosProduct = ReadString(localMachine, BiosKey, "SystemProductName");
        string? systemManufacturer = ReadString(localMachine, SystemInformationKey, "SystemManufacturer");
        string? systemProduct = ReadString(localMachine, SystemInformationKey, "SystemProductName");
        exclusionReason = biosManufacturer == "Razer" && systemManufacturer == "Razer" && biosProduct == systemProduct
            ? ThxAudioCompatibility.ExcludedModels.SingleOrDefault(model => model.ProductName == biosProduct)?.Reason : null;
        return ThxAudioCompatibility.Profiles.SingleOrDefault(profile => ThxAudioCompatibility.MatchesHost(
            profile, biosManufacturer, biosProduct, systemManufacturer, systemProduct));
    }

    internal static bool IsExactUsbPnpDeviceId(string value) =>
        ThxAudioCompatibility.MatchesUsb(ThxAudioCompatibility.Profiles[0], value);

    private static bool HasCompatibleComponents(ThxAudioCompatibilityProfile profile, out string? error,
        out string stackIdentity)
    {
        stackIdentity = string.Empty;
        if (profile.Backend == ThxAudioNativeBackend.V4 && !HasPinnedNativeDll(GetNativeDllPath(), out error))
        {
            return false;
        }
        ThxDriverApiEvidence evidence = WindowsThxDriverApiDiscovery.Capture(
            profile,
            profile.Backend == ThxAudioNativeBackend.Legacy265 ? GetLegacyApoPath() : null);
        if (!evidence.Trusted)
        {
            error = evidence.Error ?? "The installed THX API packages are unavailable.";
            return false;
        }
        if (!WindowsThxAdmissionQueries.HasRunningPinnedService(
            profile.Stack.ServiceName,
            evidence.ServiceBinary))
        {
            error = GetServiceFailure(profile.Stack);
            return false;
        }
        stackIdentity = evidence.Identity;
        error = null;
        return true;
    }

    internal static string GetLegacyApoPath()
    {
        // THXRTAPO.inf OUT_FX_MODE_HOST_CLSID. Read HKLM explicitly;
        // per-user COM registration cannot supply the admitted audio effect.
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = machine.OpenSubKey(@"SOFTWARE\Classes\CLSID\{68650828-CA0F-4196-9096-5F50931AF10A}\InProcServer32");
        return key?.GetValue(null) as string ?? string.Empty;
    }

    internal static string GetServiceFailure(ThxAudioDriverStack stack) =>
        $"THX service {stack.ServiceName} is missing, stopped, or does not match {stack.ServiceExecutable}.";

    internal static string GetServiceVerificationFailure(ThxAudioDriverStack stack) =>
        $"THX service {stack.ServiceName} could not be verified against {stack.ServiceExecutable}.";

    internal static bool IsCompetingControllerRunning() =>
        HasProcess("RazerAppEngine")
        || HasProcess("Razer Synapse Service")
        || HasProcess("RazerSynapseService")
        // Product 736 registers this exact standalone client as a competing
        // THX UI and presents a modal while it is running.
        || HasProcess("THXSpatialAudioControl")
        || HasProcess("thxspatialaudiosrv")
        || HasProcess("thxsahelper")
        || HasProcess("thxsahelper-discovery")
        || HasProcess("THXSpatialAudioClient")
        || WindowsThxAdmissionQueries.HasCompetingService();

    private static bool HasProcess(string name)
    {
        Process[] processes = Process.GetProcessesByName(name);
        try
        {
            return processes.Length != 0;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static string? ReadString(RegistryKey localMachine, string keyPath, string valueName)
    {
        using RegistryKey? key = localMachine.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(valueName, defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private static ThxAudioAdmission Unsupported(string error) =>
        new(ThxAudioAvailability.UnsupportedModel, error);

    private static ThxAudioAdmission Unavailable(string error) =>
        new(ThxAudioAvailability.ComponentsUnavailable, error);
}
