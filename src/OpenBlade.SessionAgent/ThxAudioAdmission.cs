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
            if (profile is null || !WindowsThxAdmissionQueries.HasExactDeviceIdentities(profile))
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

    internal static ThxAudioCompatibilityProfile? GetExactHostProfile()
    {
        using RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        string? biosManufacturer = ReadString(localMachine, BiosKey, "SystemManufacturer");
        string? biosProduct = ReadString(localMachine, BiosKey, "SystemProductName");
        string? systemManufacturer = ReadString(localMachine, SystemInformationKey, "SystemManufacturer");
        string? systemProduct = ReadString(localMachine, SystemInformationKey, "SystemProductName");
        return ThxAudioCompatibility.Profiles.SingleOrDefault(profile => ThxAudioCompatibility.MatchesHost(
            profile, biosManufacturer, biosProduct, systemManufacturer, systemProduct));
    }

    internal static bool IsExactUsbPnpDeviceId(string value) =>
        ThxAudioCompatibility.MatchesUsb(ThxAudioCompatibility.Profiles[0], value);

    private static bool HasPinnedComponents(ThxAudioCompatibilityProfile profile, out string? error)
    {
        if (!HasPinnedNativeDll(GetNativeDllPath(), out error))
        {
            return false;
        }
        string driverStore = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "DriverStore", "FileRepository");
        if (!ThxAudioCompatibility.TryResolveDriverStack(
            driverStore, profile.Stack, out string? serviceBinary, out error))
        {
            return false;
        }
        if (!ThxAudioCompatibility.HasPinnedActiveConfiguration(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), profile, out error))
        {
            return false;
        }
        bool serviceAvailable;
        try
        {
            serviceAvailable = WindowsThxAdmissionQueries.HasRunningPinnedService(
                profile.Stack.ServiceName, serviceBinary!);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            error = GetServiceVerificationFailure(profile.Stack);
            return false;
        }
        if (!serviceAvailable)
        {
            error = GetServiceFailure(profile.Stack);
            return false;
        }
        error = null;
        return true;
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
