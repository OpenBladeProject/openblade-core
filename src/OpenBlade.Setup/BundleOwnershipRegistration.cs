using Microsoft.Win32;
using OpenBlade.Contracts;

namespace OpenBlade.Setup;

public sealed record BundleOwnershipIdentity(
    string Owner,
    string InstalledVersion,
    string InstalledBuild)
{
    internal bool IsWellFormed =>
        IsValidValue(Owner)
        && IsValidValue(InstalledVersion)
        && IsValidValue(InstalledBuild);

    private static bool IsValidValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 512;
}

public interface IBundleOwnershipRegistration
{
    BundleOwnershipIdentity? Capture();
    bool IsOwnedByCurrentBuild(BundleOwnershipIdentity ownership);
    void Install();
    void Restore(BundleOwnershipIdentity ownership);
    void Remove();
}

internal sealed class WindowsBundleOwnershipRegistration : IBundleOwnershipRegistration
{
    internal const string RegistryPath = @"SOFTWARE\OSSBlade\OpenBlade\Installer";
    internal const string ProductRegistryPath = @"SOFTWARE\OSSBlade\OpenBlade";
    internal const string PublisherRegistryPath = @"SOFTWARE\OSSBlade";

    public BundleOwnershipIdentity? Capture()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using RegistryKey? key = baseKey.OpenSubKey(RegistryPath, writable: false);
        if (key is null)
        {
            return null;
        }

        var ownership = new BundleOwnershipIdentity(
            ReadRequiredString(key, "Owner"),
            ReadRequiredString(key, "InstalledVersion"),
            ReadRequiredString(key, "InstalledBuild"));
        if (!ownership.IsWellFormed)
        {
            throw new InvalidDataException(
                "The graphical-installer ownership marker is malformed.");
        }

        return ownership;
    }

    public bool IsOwnedByCurrentBuild(BundleOwnershipIdentity ownership) =>
        ownership is { IsWellFormed: true }
        && IsOwnedByBuild(ownership.InstalledBuild, CurrentBuildIdentity);

    public void Install() =>
        Write(new BundleOwnershipIdentity(
            "WiX Burn",
            FormatInstalledVersion(
                typeof(WindowsBundleOwnershipRegistration).Assembly.GetName().Version),
            CurrentBuildIdentity));

    public void Restore(BundleOwnershipIdentity ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        if (!ownership.IsWellFormed)
        {
            throw new InvalidDataException(
                "Refusing to restore a malformed graphical-installer ownership marker.");
        }

        Write(ownership);
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

    private static void Write(BundleOwnershipIdentity ownership)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using RegistryKey key = baseKey.CreateSubKey(RegistryPath, writable: true);
        key.SetValue("Owner", ownership.Owner, RegistryValueKind.String);
        key.SetValue(
            "InstalledVersion",
            ownership.InstalledVersion,
            RegistryValueKind.String);
        key.SetValue(
            "InstalledBuild",
            ownership.InstalledBuild,
            RegistryValueKind.String);
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

    private static string ReadRequiredString(RegistryKey key, string valueName)
    {
        object? value = key.GetValue(
            valueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException(
                $"The graphical-installer ownership value '{valueName}' is missing or malformed.");
        }

        return text;
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
