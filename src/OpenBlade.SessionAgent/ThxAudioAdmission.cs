using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using Microsoft.Win32;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

internal sealed record ThxAudioAdmission(
    ThxAudioAvailability Availability,
    string? Error)
{
    public string? ProductId { get; init; }
    public string? ModelNumber { get; init; }
    public bool MutationAllowed { get; init; }
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
    internal const string NativeDllSha256 =
        "8E8F6923624762935D70B72F58BEC00808DB84DF740340047919B53331130A0A";
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string SystemInformationKey =
        @"SYSTEM\CurrentControlSet\Control\SystemInformation";

    public ThxAudioAdmission Check()
    {
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

            ThxAudioCompatibilityProfile? profile = GetExactHostProfile();
            if (profile is null || !HasExactDeviceIdentities(profile))
            {
                return Unsupported("The exact Razer model, USB identity, and signed-package audio codec could not be verified.");
            }

            if (IsCompetingControllerRunning())
            {
                return new ThxAudioAdmission(
                    ThxAudioAvailability.ControllerActive,
                    "Razer Synapse or THX Spatial Audio is active. Close it before controlling THX audio.")
                { ProductId = profile.ProductId, ModelNumber = profile.ModelNumber };
            }

            if (!HasPinnedComponents(profile, out string? componentError))
            {
                return Unavailable(componentError!) with { ProductId = profile.ProductId, ModelNumber = profile.ModelNumber };
            }

            if (!profile.ReadAllowed)
            {
                return Unavailable("This model's THX native read contract is awaiting validation.")
                    with
                { ProductId = profile.ProductId, ModelNumber = profile.ModelNumber };
            }
            return new ThxAudioAdmission(profile.MutationAllowed
                ? ThxAudioAvailability.Available : ThxAudioAvailability.AvailableReadOnly, null)
            { ProductId = profile.ProductId, ModelNumber = profile.ModelNumber, MutationAllowed = profile.MutationAllowed };
        }
        catch (Exception exception)
        {
            return Unavailable(
                $"THX audio admission could not be verified: {exception.GetType().Name}.");
        }
    }

    internal static string GetNativeDllPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Razer",
        "RazerAppEngine",
        "User Data",
        "Apps",
        "Common",
        "thxv4common",
        "ThxV4Native_v1.1.1.0.dll");

    internal static bool HasPinnedNativeDll() =>
        ThxAudioCompatibility.HasHash(GetNativeDllPath(), NativeDllSha256);

    private static ThxAudioCompatibilityProfile? GetExactHostProfile()
    {
        using RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        string? biosManufacturer = ReadString(localMachine, BiosKey, "SystemManufacturer");
        string? biosProduct = ReadString(localMachine, BiosKey, "SystemProductName");
        string? systemManufacturer = ReadString(localMachine, SystemInformationKey, "SystemManufacturer");
        string? systemProduct = ReadString(localMachine, SystemInformationKey, "SystemProductName");
        return ThxAudioCompatibility.Profiles.SingleOrDefault(profile => ThxAudioCompatibility.MatchesHost(
            profile, biosManufacturer, biosProduct, systemManufacturer, systemProduct));
    }

    private static bool HasExactDeviceIdentities(ThxAudioCompatibilityProfile profile)
    {
        bool usb = false;
        bool codec = false;
        using ManagementObjectSearcher searcher = CreateSearcher(
            "SELECT PNPDeviceID, HardwareID FROM Win32_PnPEntity WHERE Present = TRUE");
        using ManagementObjectCollection devices = searcher.Get();
        foreach (ManagementBaseObject device in devices)
        {
            using (device)
            {
                usb |= device["PNPDeviceID"] is string id && ThxAudioCompatibility.MatchesUsb(profile, id);
                codec |= device["HardwareID"] is string[] ids && ThxAudioCompatibility.MatchesCodec(profile, ids);
            }
        }
        return usb && codec;
    }

    internal static bool IsExactUsbPnpDeviceId(string value) =>
        ThxAudioCompatibility.MatchesUsb(ThxAudioCompatibility.Profiles[0], value);

    private static bool HasPinnedComponents(ThxAudioCompatibilityProfile profile, out string? error)
    {
        if (!HasPinnedNativeDll())
        {
            error = "The pinned THX native component is unavailable or has changed.";
            return false;
        }
        string driverStore = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "DriverStore", "FileRepository");
        if (!ThxAudioCompatibility.TryResolveDriverStack(driverStore, profile.Stack, out string? serviceBinary))
        {
            error = "The pinned THX service and audio driver stack is unavailable, ambiguous, or has changed.";
            return false;
        }
        if (!ThxAudioCompatibility.HasPinnedActiveConfiguration(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), profile))
        {
            error = "The pinned active THX configuration is unavailable or has changed.";
            return false;
        }
        if (!HasRunningPinnedService(profile.Stack.ServiceName, serviceBinary!))
        {
            error = "The pinned THX Spatial Audio service is not installed and running.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool IsCompetingControllerRunning() =>
        HasProcess("RazerAppEngine")
        || HasProcess("Razer Synapse Service")
        || HasProcess("RazerSynapseService")
        // Product 736 registers this exact standalone client as a competing
        // THX UI and presents a modal while it is running.
        || HasProcess("THXSpatialAudioClient")
        || HasRunningService(
            static (name, displayName) =>
                string.Equals(name, "RazerSynapseService", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    displayName,
                    "Razer Synapse Service",
                    StringComparison.OrdinalIgnoreCase));

    private static bool HasRunningPinnedService(string serviceName, string expectedBinary)
    {
        using ManagementObjectSearcher searcher = CreateSearcher(
            "SELECT Name, PathName FROM Win32_Service "
            + $"WHERE Name = '{serviceName}' AND State = 'Running'");
        using ManagementObjectCollection services = searcher.Get();
        foreach (ManagementBaseObject service in services)
        {
            using (service)
            {
                if (service["Name"] is string name
                    && string.Equals(
                        name,
                        serviceName,
                        StringComparison.OrdinalIgnoreCase)
                    && service["PathName"] is string path
                    && string.Equals(
                        NormalizeServiceBinary(path),
                        Path.GetFullPath(expectedBinary),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasRunningService(Func<string, string, bool> predicate)
    {
        using ManagementObjectSearcher searcher = CreateSearcher(
            "SELECT Name, DisplayName FROM Win32_Service WHERE State = 'Running'");
        using ManagementObjectCollection services = searcher.Get();
        foreach (ManagementBaseObject service in services)
        {
            using (service)
            {
                string name = service["Name"] as string ?? string.Empty;
                string displayName = service["DisplayName"] as string ?? string.Empty;
                if (predicate(name, displayName))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static ManagementObjectSearcher CreateSearcher(string query)
    {
        var searcher = new ManagementObjectSearcher(query);
        searcher.Options.Timeout = TimeSpan.FromSeconds(2);
        searcher.Options.ReturnImmediately = true;
        searcher.Options.Rewindable = false;
        return searcher;
    }

    private static string NormalizeServiceBinary(string commandLine)
    {
        string value = commandLine.Trim();
        if (value.StartsWith('"'))
        {
            int end = value.IndexOf('"', 1);
            return end > 1 ? Path.GetFullPath(value[1..end]) : string.Empty;
        }
        int separator = value.IndexOf(' ');
        return Path.GetFullPath(separator < 0 ? value : value[..separator]);
    }

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
