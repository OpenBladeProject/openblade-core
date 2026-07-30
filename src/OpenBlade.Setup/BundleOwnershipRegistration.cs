using Microsoft.Win32;
using OpenBlade.Contracts;

namespace OpenBlade.Setup;

public interface IBundleOwnershipRegistration
{
    bool Exists();
    bool IsOwnedByCurrentBuild();
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

    public bool IsOwnedByCurrentBuild()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using RegistryKey? key = baseKey.OpenSubKey(RegistryPath, writable: false);
        return IsOwnedByBuild(
            key?.GetValue("InstalledBuild") as string,
            CurrentBuildIdentity);
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
        key.SetValue("InstalledBuild", CurrentBuildIdentity, RegistryValueKind.String);
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

    internal static string FormatInstalledVersion(Version? version) =>
        version is null
            ? "0.0.0"
            : $"{Math.Max(version.Major, 0)}.{Math.Max(version.Minor, 0)}.{Math.Max(version.Build, 0)}";

    internal static bool IsOwnedByBuild(string? installedBuild, string currentBuild) =>
        !string.IsNullOrWhiteSpace(installedBuild)
        && string.Equals(installedBuild.Trim(), currentBuild, StringComparison.Ordinal);

    private static string CurrentBuildIdentity =>
        OpenBladeProductVersion.Read(typeof(WindowsBundleOwnershipRegistration).Assembly);

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
