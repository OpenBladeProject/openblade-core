using System.Reflection;
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
    private const string BundleOwnerSeparator = "|bundle:";
    internal const string RegistryPath = @"SOFTWARE\OSSBlade\OpenBlade\Installer";
    internal const string ProductRegistryPath = @"SOFTWARE\OSSBlade\OpenBlade";
    internal const string PublisherRegistryPath = @"SOFTWARE\OSSBlade";
    private readonly string currentBuildIdentity;
    private readonly string currentInstalledVersion;

    internal WindowsBundleOwnershipRegistration(string? bundleOwnerId = null)
    {
        Assembly assembly = typeof(WindowsBundleOwnershipRegistration).Assembly;
        string productVersion = OpenBladeProductVersion.Read(assembly);
        currentBuildIdentity = FormatOwnedBuildIdentity(
            productVersion,
            bundleOwnerId);
        currentInstalledVersion = ResolveInstalledVersion(
            assembly.GetName().Version,
            productVersion);
    }

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
        && IsOwnedByBuild(ownership.InstalledBuild, currentBuildIdentity);

    public void Install() =>
        Write(new BundleOwnershipIdentity(
            "WiX Burn",
            currentInstalledVersion,
            currentBuildIdentity));

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

    internal static string FormatInstalledVersion(Version? version)
    {
        if (version is null)
        {
            return "0.0.0";
        }

        // Burn compares a missing fourth component as zero. Keep stable markers
        // three-part and add the revision only when the bundle has a nonzero one.
        string baseVersion =
            $"{Math.Max(version.Major, 0)}.{Math.Max(version.Minor, 0)}.{Math.Max(version.Build, 0)}";
        return version.Revision > 0
            ? $"{baseVersion}.{version.Revision}"
            : baseVersion;
    }

    internal static string ResolveInstalledVersion(
        Version? binaryVersion,
        string productVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);

        string normalizedProductVersion = productVersion.Trim();
        int metadataSeparator = normalizedProductVersion.IndexOf('+');
        if (metadataSeparator >= 0)
        {
            normalizedProductVersion = normalizedProductVersion[..metadataSeparator];
        }

        // LocalDev reserves major 65535 for Burn while CLR binary metadata uses
        // 65534. Burn package detection must retain the exact bundle identity.
        if (!normalizedProductVersion.StartsWith("65535.", StringComparison.Ordinal))
        {
            return FormatInstalledVersion(binaryVersion);
        }

        if (!Version.TryParse(normalizedProductVersion, out Version? localDevVersion)
            || localDevVersion.Major != 65535
            || localDevVersion.Build < 0
            || localDevVersion.Revision < 0
            || binaryVersion is null
            || binaryVersion.Major != 65534
            || binaryVersion.Minor != localDevVersion.Minor
            || binaryVersion.Build != localDevVersion.Build
            || binaryVersion.Revision != localDevVersion.Revision)
        {
            throw new InvalidDataException(
                "The local-development bundle and binary versions do not match.");
        }

        return localDevVersion.ToString(4);
    }

    internal static bool IsOwnedByBuild(string? installedBuild, string currentBuild) =>
        !string.IsNullOrWhiteSpace(installedBuild)
        && string.Equals(installedBuild.Trim(), currentBuild, StringComparison.Ordinal);

    internal static string FormatOwnedBuildIdentity(
        string buildIdentity,
        string? bundleOwnerId)
    {
        if (string.IsNullOrWhiteSpace(buildIdentity))
        {
            throw new ArgumentException("The build identity is required.", nameof(buildIdentity));
        }

        if (bundleOwnerId is null)
        {
            return buildIdentity;
        }

        if (!Guid.TryParseExact(bundleOwnerId, "D", out Guid parsedBundleOwnerId))
        {
            throw new ArgumentException(
                "The bundle owner ID must be a D-format GUID.",
                nameof(bundleOwnerId));
        }

        return buildIdentity + BundleOwnerSeparator + parsedBundleOwnerId.ToString("D");
    }

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
