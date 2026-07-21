using Microsoft.Win32;

namespace OpenBlade.Setup;

public sealed record UninstallRegistrationRequest(
    string DisplayVersion,
    string InstallLocation,
    string DisplayIcon,
    string UninstallCommand);

public interface IUninstallRegistration
{
    bool Exists();
    void Install(UninstallRegistrationRequest request);
    void Remove();
}

internal sealed class WindowsUninstallRegistration : IUninstallRegistration
{
    internal const string RegistryPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OpenBlade";

    public bool Exists()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = baseKey.OpenSubKey(RegistryPath, writable: false);
        return key is not null;
    }

    public void Install(UninstallRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey key = baseKey.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Could not create OpenBlade's Installed apps entry.");

        key.SetValue("DisplayName", "OpenBlade", RegistryValueKind.String);
        key.SetValue("DisplayVersion", request.DisplayVersion, RegistryValueKind.String);
        key.SetValue("Publisher", "OSSBlade", RegistryValueKind.String);
        key.SetValue("InstallLocation", request.InstallLocation, RegistryValueKind.String);
        key.SetValue("DisplayIcon", request.DisplayIcon, RegistryValueKind.String);
        key.SetValue("UninstallString", request.UninstallCommand, RegistryValueKind.String);
        key.SetValue("URLInfoAbout", "https://github.com/OSSBlade/openblade-core", RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    public void Remove()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        baseKey.DeleteSubKeyTree(RegistryPath, throwOnMissingSubKey: false);
    }
}
