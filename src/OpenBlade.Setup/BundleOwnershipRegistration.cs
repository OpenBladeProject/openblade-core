using Microsoft.Win32;

namespace OpenBlade.Setup;

public interface IBundleOwnershipRegistration
{
    bool Exists();
    void Install();
    void Remove();
}

internal sealed class WindowsBundleOwnershipRegistration : IBundleOwnershipRegistration
{
    internal const string RegistryPath = @"SOFTWARE\OSSBlade\OpenBlade\Installer";

    public bool Exists()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using RegistryKey? key = baseKey.OpenSubKey(RegistryPath, writable: false);
        return key is not null;
    }

    public void Install()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using RegistryKey key = baseKey.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException(
                "Could not record graphical installer ownership for OpenBlade.");
        key.SetValue("Owner", "WiX Burn", RegistryValueKind.String);
    }

    public void Remove()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        baseKey.DeleteSubKeyTree(RegistryPath, throwOnMissingSubKey: false);
    }
}
