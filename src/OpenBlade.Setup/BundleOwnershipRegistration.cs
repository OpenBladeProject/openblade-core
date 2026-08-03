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
    internal const string ProductRegistryPath = @"SOFTWARE\OSSBlade\OpenBlade";
    internal const string PublisherRegistryPath = @"SOFTWARE\OSSBlade";

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
        using RegistryKey key = baseKey.CreateSubKey(RegistryPath, writable: true);
        key.SetValue("Owner", "WiX Burn", RegistryValueKind.String);
        key.SetValue(
            "InstalledVersion",
            FormatInstalledVersion(typeof(WindowsBundleOwnershipRegistration).Assembly.GetName().Version),
            RegistryValueKind.String);
    }

    public void Remove()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        baseKey.DeleteSubKeyTree(RegistryPath, throwOnMissingSubKey: false);
        DeleteIfEmpty(baseKey, ProductRegistryPath);
        DeleteIfEmpty(baseKey, PublisherRegistryPath);
    }

    internal static string FormatInstalledVersion(Version? version)
    {
        if (version is null)
        {
            return "0.0.0";
        }

        string baseVersion =
            $"{Math.Max(version.Major, 0)}.{Math.Max(version.Minor, 0)}.{Math.Max(version.Build, 0)}";
        return version.Revision > 0
            ? $"{baseVersion}.{version.Revision}"
            : baseVersion;
    }

    private static void DeleteIfEmpty(RegistryKey baseKey, string path)
    {
        bool isEmpty;
        using (RegistryKey? key = baseKey.OpenSubKey(path, writable: false))
        {
            isEmpty = key is not null && key.SubKeyCount == 0 && key.ValueCount == 0;
        }

        if (isEmpty)
        {
            baseKey.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
    }
}
