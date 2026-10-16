using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

internal sealed record ThxAudioAdmission(
    ThxAudioAvailability Availability,
    string? Error)
{
    public bool IsAvailable =>
        Availability == ThxAudioAvailability.Available && Error is null;
}

internal interface IThxAudioAdmissionSource
{
    ThxAudioAdmission Check();
}

internal sealed class WindowsThxAudioAdmissionSource : IThxAudioAdmissionSource
{
    internal const string NativeDllSha256 =
        "8E8F6923624762935D70B72F58BEC00808DB84DF740340047919B53331130A0A";
    internal const string ProductId = "rznb12";
    private const string ExpectedManufacturer = "Razer";
    private const string ExpectedProductName = "Blade 16 - RZ09-0581";
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string SystemInformationKey =
        @"SYSTEM\CurrentControlSet\Control\SystemInformation";
    private const string UsbIdentityPrefix = "VID_1532&PID_02E0";

    private static readonly (string RelativePath, string Sha256)[] DriverFiles =
    [
        (
            @"thx_rznb12_svc.inf_amd64_36ca69c6d3eda8ed\thx_rznb12_svc.inf",
            "0B312BA852C34727A0CD51BABCFE6B8E56E7A40D83BFB5BE8F9F8218B60AB610"),
        (
            @"thx_rznb12_svc.inf_amd64_36ca69c6d3eda8ed\thx_rznb12_svc.exe",
            "BD1DC54D4ABECC67DD3A8FA25A39223577E035A623FEF38C915F70A659E8DD42"),
        (
            @"thx_rznb12_svc.inf_amd64_36ca69c6d3eda8ed\thx_rznb12_svc.cat",
            "C8F088106319AF05D5D57D7A04BF918E19BF795AF62F0B6FA8B93C31D5CBF9C7"),
        (
            @"thx_rznb12_apo4.inf_amd64_4796d3a237351feb\thx_rznb12_apo4.inf",
            "475EAA14B62F4C17DB5DCC89CB1F4A5381F8A3D6DE75E14DD92A4EC09002A914"),
        (
            @"thx_rznb12_apo4.inf_amd64_4796d3a237351feb\thx_rznb12_apo4.dll",
            "C8C1FCD4869DC7F384E69ACE5A320D9816FFC2D81C03D0667BC19644D9EF4122"),
        (
            @"thx_rznb12_apo4.inf_amd64_4796d3a237351feb\thx_rznb12_apo4_grpc.dll",
            "7A220E038B76F799ED079BBFBF864C4C30D16EE2777881A46FA0E7861EDC328B"),
        (
            @"thx_rznb12_apo4.inf_amd64_4796d3a237351feb\thx_rznb12_apo4.cat",
            "F7CA851E858BDA079B0B481D5BEDCD83EA8E5B18976D2A404B299AFDF95237D2"),
        (
            @"thx_rznb12_ext.inf_amd64_510b44355ff422ff\thx_rznb12_ext.inf",
            "4559784C6F2C0951CCE99DFAE8E26321B2AF4E0C01BF717F3455354C455CAD5D"),
        (
            @"thx_rznb12_ext.inf_amd64_510b44355ff422ff\thx_rznb12_ext.cat",
            "9754CAA20A26963F2512A215DDB1E09877265E25A011C5D283B6E8E9B2E9F242"),
    ];

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

            if (!HasExactHostIdentity() || !HasExactUsbIdentity())
            {
                return Unsupported("THX audio is admitted only for RZ09-0581 with USB 1532:02E0.");
            }

            if (IsCompetingControllerRunning())
            {
                return new ThxAudioAdmission(
                    ThxAudioAvailability.ControllerActive,
                    "Razer Synapse or THX Spatial Audio is active. Close it before controlling THX audio.");
            }

            if (!HasPinnedComponents(out string? componentError))
            {
                return Unavailable(componentError!);
            }

            return new ThxAudioAdmission(ThxAudioAvailability.Available, null);
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
        HasHash(GetNativeDllPath(), NativeDllSha256);

    private static bool HasExactHostIdentity()
    {
        using RegistryKey localMachine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        return ReadExact(localMachine, BiosKey, "SystemManufacturer", ExpectedManufacturer)
            && ReadExact(localMachine, BiosKey, "SystemProductName", ExpectedProductName)
            && ReadExact(
                localMachine,
                SystemInformationKey,
                "SystemManufacturer",
                ExpectedManufacturer)
            && ReadExact(
                localMachine,
                SystemInformationKey,
                "SystemProductName",
                ExpectedProductName);
    }

    private static bool HasExactUsbIdentity()
    {
        using ManagementObjectSearcher searcher = CreateSearcher(
            "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE Present = TRUE");
        using ManagementObjectCollection devices = searcher.Get();
        foreach (ManagementBaseObject device in devices)
        {
            using (device)
            {
                if (device["PNPDeviceID"] is string id && IsExactUsbPnpDeviceId(id))
                {
                    return true;
                }
            }
        }
        return false;
    }

    internal static bool IsExactUsbPnpDeviceId(string value)
    {
        string prefix = $"USB\\{UsbIdentityPrefix}";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return value.Length == prefix.Length
            || value[prefix.Length] is '\\' or '&';
    }

    private static bool HasPinnedComponents(out string? error)
    {
        string dll = GetNativeDllPath();
        if (!HasHash(dll, NativeDllSha256))
        {
            error = "The pinned THX native component is unavailable or has changed.";
            return false;
        }

        string driverStore = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "DriverStore",
            "FileRepository");
        foreach ((string relativePath, string sha256) in DriverFiles)
        {
            if (!HasHash(Path.Combine(driverStore, relativePath), sha256))
            {
                error = "The pinned THX service and audio driver stack is unavailable or has changed.";
                return false;
            }
        }

        string serviceBinary = Path.Combine(
            driverStore,
            @"thx_rznb12_svc.inf_amd64_36ca69c6d3eda8ed\thx_rznb12_svc.exe");
        if (!HasRunningPinnedService(serviceBinary))
        {
            error = "The pinned THX Spatial Audio service is not installed and running.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool HasHash(string path, string expected)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                SHA256.HashData(stream));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
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

    private static bool HasRunningPinnedService(string expectedBinary)
    {
        using ManagementObjectSearcher searcher = CreateSearcher(
            "SELECT Name, PathName FROM Win32_Service "
            + "WHERE Name = 'thx_rznb12_service' AND State = 'Running'");
        using ManagementObjectCollection services = searcher.Get();
        foreach (ManagementBaseObject service in services)
        {
            using (service)
            {
                if (service["Name"] is string name
                    && string.Equals(
                        name,
                        "thx_rznb12_service",
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

    private static bool ReadExact(
        RegistryKey localMachine,
        string keyPath,
        string valueName,
        string expected)
    {
        using RegistryKey? key = localMachine.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(
            valueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) is string value
            && string.Equals(value, expected, StringComparison.Ordinal);
    }

    private static ThxAudioAdmission Unsupported(string error) =>
        new(ThxAudioAvailability.UnsupportedModel, error);

    private static ThxAudioAdmission Unavailable(string error) =>
        new(ThxAudioAvailability.ComponentsUnavailable, error);
}
