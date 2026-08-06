using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenBlade.Protocol;

internal readonly record struct HostIdentityEvidence(
    string Manufacturer, string ProductName, string Sku, string BiosVersion);

internal readonly record struct HostIdentityReadResult(
    HostIdentityEvidence? Evidence,
    HidSessionFailureKind? Failure,
    HostFirmwareCompatibility FirmwareCompatibility =
        HostFirmwareCompatibility.Unknown);

internal enum HostFirmwareCompatibility
{
    Unknown,
    Tested,
    UntestedBlocked,
    UntestedAllowed,
}

internal readonly record struct WindowsPlatformEvidence(
    bool IsWindows,
    bool IsWorkstation,
    Version Version,
    Architecture Architecture);

internal readonly record struct ExpectedHostIdentity(
    string Manufacturer,
    string ProductName,
    string Sku,
    IReadOnlyList<string> BiosVersions);

internal interface IRegistryValueSource
{
    object? Read(RegistryHive hive, RegistryView view, string key, string valueName);
}

internal sealed class WindowsRegistryValueSource : IRegistryValueSource
{
    public object? Read(RegistryHive hive, RegistryView view, string key, string valueName)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
        using RegistryKey? subKey = baseKey.OpenSubKey(key, writable: false);
        return subKey?.GetValue(
            valueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
    }
}

internal interface IHostIdentitySource
{
    HostIdentityReadResult ReadExactIdentity();
}

internal interface IWindowsPlatformEvidenceSource
{
    WindowsPlatformEvidence Read();
}

internal sealed class RuntimeWindowsPlatformEvidenceSource(
    IRegistryValueSource registry) : IWindowsPlatformEvidenceSource
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public WindowsPlatformEvidence Read() => new(
        OperatingSystem.IsWindows(),
        string.Equals(
            registry.Read(
                RegistryHive.LocalMachine,
                RegistryView.Registry64,
                CurrentVersionKey,
                "InstallationType") as string,
            "Client",
            StringComparison.Ordinal),
        Environment.OSVersion.Version,
        RuntimeInformation.OSArchitecture);
}

internal sealed class WindowsHostIdentitySource : IHostIdentitySource
{
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string SystemInformationKey = @"SYSTEM\CurrentControlSet\Control\SystemInformation";
    private static readonly ExpectedHostIdentity ProductionIdentity = new(
        "Razer",
        "Blade 16 - RZ09-0581",
        "RZ09-05819EN4",
        TargetBladeDevice.SupportedBiosVersions);
    internal static readonly ExpectedHostIdentity Rz090528Identity = new(
        "Razer",
        "Blade 16 - RZ09-0528",
        "RZ09-05289EN4",
        Array.AsReadOnly(new[] { "2.02" }));
    private readonly IRegistryValueSource registry;
    private readonly IWindowsPlatformEvidenceSource platform;
    private readonly ExpectedHostIdentity expected;
    private readonly FirmwareCompatibilityPolicy? compatibilityPolicy;

    internal WindowsHostIdentitySource(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform)
        : this(registry, platform, ProductionIdentity, compatibilityPolicy: null)
    {
    }

    internal WindowsHostIdentitySource(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        FirmwareCompatibilityPolicy compatibilityPolicy)
        : this(registry, platform, ProductionIdentity, compatibilityPolicy)
    {
    }

    internal WindowsHostIdentitySource(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        ExpectedHostIdentity expected,
        FirmwareCompatibilityPolicy? compatibilityPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(platform);
        if (string.IsNullOrWhiteSpace(expected.Manufacturer)
            || string.IsNullOrWhiteSpace(expected.ProductName)
            || string.IsNullOrWhiteSpace(expected.Sku)
            || expected.BiosVersions is null
            || expected.BiosVersions.Count == 0
            || expected.BiosVersions.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Expected host identity must be complete.", nameof(expected));
        }

        this.registry = registry;
        this.platform = platform;
        this.expected = expected;
        this.compatibilityPolicy = compatibilityPolicy;
    }

    public HostIdentityReadResult ReadExactIdentity()
    {
        if (!IsAuthorizedPlatform()
            || !TryReadExact(BiosKey, "SystemManufacturer", expected.Manufacturer, out string biosManufacturer)
            || !TryReadExact(BiosKey, "SystemProductName", expected.ProductName, out string biosProductName)
            || !TryReadExact(BiosKey, "SystemSKU", expected.Sku, out string sku)
            || !TryReadValue(BiosKey, "BIOSVersion", out string biosVersion)
            || !TryReadExact(SystemInformationKey, "SystemManufacturer", expected.Manufacturer, out string systemManufacturer)
            || !TryReadExact(SystemInformationKey, "SystemProductName", expected.ProductName, out string systemProductName)
            || !TryReadValue(SystemInformationKey, "BIOSVersion", out string systemBiosVersion)
            || !string.Equals(biosManufacturer, systemManufacturer, StringComparison.Ordinal)
            || !string.Equals(biosProductName, systemProductName, StringComparison.Ordinal)
            || !string.Equals(biosVersion, systemBiosVersion, StringComparison.Ordinal))
        {
            return new(null, HidSessionFailureKind.IdentityBlocked);
        }

        bool testedFirmware = expected.BiosVersions.Contains(
            biosVersion,
            StringComparer.Ordinal);
        if (!testedFirmware && compatibilityPolicy?.AllowUntestedFirmware != true)
        {
            return new(
                null,
                HidSessionFailureKind.IdentityBlocked,
                HostFirmwareCompatibility.UntestedBlocked);
        }

        return new(
            new HostIdentityEvidence(biosManufacturer, biosProductName, sku, biosVersion),
            null,
            testedFirmware
                ? HostFirmwareCompatibility.Tested
                : HostFirmwareCompatibility.UntestedAllowed);
    }

    internal static bool IsExactRz090528WindowsHost() =>
        IsRz090528WindowsHost(allowUntestedFirmware: false);

    internal static bool IsRz090528WindowsHost(bool allowUntestedFirmware)
    {
        var registry = new WindowsRegistryValueSource();
        var compatibilityPolicy = new FirmwareCompatibilityPolicy
        {
            AllowUntestedFirmware = allowUntestedFirmware,
        };
        return new WindowsHostIdentitySource(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            Rz090528Identity,
            compatibilityPolicy).ReadExactIdentity() is
        {
            Evidence: not null,
            Failure: null,
        };
    }

    private bool IsAuthorizedPlatform()
    {
        try
        {
            WindowsPlatformEvidence evidence = platform.Read();
            return evidence.IsWindows
                && evidence.IsWorkstation
                && evidence.Architecture == Architecture.X64
                && evidence.Version.Major == 10
                && evidence.Version.Minor == 0
                && evidence.Version.Build >= 22000;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryReadExact(string key, string valueName, string expected, out string actual)
    {
        return TryReadValue(key, valueName, out actual)
            && string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private bool TryReadValue(string key, string valueName, out string actual)
    {
        try
        {
            object? value = registry.Read(
                RegistryHive.LocalMachine,
                RegistryView.Registry64,
                key,
                valueName);
            actual = value as string ?? string.Empty;
            return !string.IsNullOrWhiteSpace(actual);
        }
        catch (Exception)
        {
            actual = string.Empty;
            return false;
        }
    }
}
