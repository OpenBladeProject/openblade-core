using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenBlade.Protocol;

internal readonly record struct HostIdentityEvidence(
    string Manufacturer, string ProductName, string Sku, string BiosVersion);

internal readonly record struct HostIdentityReadResult(
    HostIdentityEvidence? Evidence,
    HidSessionFailureKind? Failure,
    HostFirmwareCompatibility FirmwareCompatibility =
        HostFirmwareCompatibility.Unknown,
    HostIdentityFailureDetail? HostIdentityFailure = null);

internal enum HostFirmwareCompatibility
{
    Unknown,
    Tested,
    UntestedBlocked,
    UntestedAllowed,
    ModelScoped,
}

internal enum FirmwareAdmissionScope
{
    ExactVersions,
    ModelScoped,
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
    IReadOnlyList<string> ValidatedBiosVersions,
    FirmwareAdmissionScope FirmwareAdmission = FirmwareAdmissionScope.ExactVersions);

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
    private const int MaximumDiagnosticValueLength = 256;
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string SystemInformationKey = @"SYSTEM\CurrentControlSet\Control\SystemInformation";
    private static readonly ExpectedHostIdentity ProductionIdentity = new(
        "Razer",
        "Blade 16 - RZ09-0581",
        "RZ09-05819EN4",
        TargetBladeDevice.ValidatedBiosVersions,
        FirmwareAdmissionScope.ModelScoped);
    internal static readonly ExpectedHostIdentity Rz090528Identity = new(
        "Razer",
        "Blade 16 - RZ09-0528",
        "RZ09-05289EN4",
        Array.AsReadOnly(new[] { "2.02" }),
        FirmwareAdmissionScope.ModelScoped);
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
            || expected.ValidatedBiosVersions is null
            || expected.ValidatedBiosVersions.Count == 0
            || expected.ValidatedBiosVersions.Any(string.IsNullOrWhiteSpace)
            || !Enum.IsDefined(expected.FirmwareAdmission))
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
        HostIdentityFailureDetail? failure = ReadPlatformFailure();
        if (failure is not null)
        {
            return Blocked(failure);
        }

        if (!TryReadExact(
                BiosKey,
                "BIOS",
                "SystemManufacturer",
                expected.Manufacturer,
                out string biosManufacturer,
                out failure)
            || !TryReadExact(
                BiosKey,
                "BIOS",
                "SystemProductName",
                expected.ProductName,
                out string biosProductName,
                out failure)
            || !TryReadExact(
                BiosKey,
                "BIOS",
                "SystemSKU",
                expected.Sku,
                out string sku,
                out failure)
            || !TryReadValue(
                BiosKey,
                "BIOS",
                "BIOSVersion",
                out string biosVersion,
                out failure)
            || !TryReadExact(
                SystemInformationKey,
                "SystemInformation",
                "SystemManufacturer",
                expected.Manufacturer,
                out string systemManufacturer,
                out failure)
            || !TryReadExact(
                SystemInformationKey,
                "SystemInformation",
                "SystemProductName",
                expected.ProductName,
                out string systemProductName,
                out failure)
            || !TryReadValue(
                SystemInformationKey,
                "SystemInformation",
                "BIOSVersion",
                out string systemBiosVersion,
                out failure))
        {
            return Blocked(failure!);
        }

        if (!string.Equals(biosManufacturer, systemManufacturer, StringComparison.Ordinal))
        {
            return Blocked(SourceMismatch(
                "SystemManufacturer", biosManufacturer, systemManufacturer));
        }

        if (!string.Equals(biosProductName, systemProductName, StringComparison.Ordinal))
        {
            return Blocked(SourceMismatch(
                "SystemProductName", biosProductName, systemProductName));
        }

        if (!string.Equals(biosVersion, systemBiosVersion, StringComparison.Ordinal))
        {
            return Blocked(SourceMismatch("BIOSVersion", biosVersion, systemBiosVersion));
        }

        bool testedFirmware = expected.ValidatedBiosVersions.Contains(
            biosVersion,
            StringComparer.Ordinal);
        if (!testedFirmware
            && expected.FirmwareAdmission == FirmwareAdmissionScope.ExactVersions
            && compatibilityPolicy?.AllowUntestedFirmware != true)
        {
            return new(
                null,
                HidSessionFailureKind.IdentityBlocked,
                HostFirmwareCompatibility.UntestedBlocked,
                new HostIdentityFailureDetail(
                    HostIdentityFailureReason.FirmwareNotAdmitted,
                    "BIOS",
                    "BIOSVersion",
                    Bounded(string.Join(", ", expected.ValidatedBiosVersions)),
                    Bounded(biosVersion)));
        }

        return new(
            new HostIdentityEvidence(biosManufacturer, biosProductName, sku, biosVersion),
            null,
            testedFirmware
                ? HostFirmwareCompatibility.Tested
                : expected.FirmwareAdmission == FirmwareAdmissionScope.ModelScoped
                    ? HostFirmwareCompatibility.ModelScoped
                    : HostFirmwareCompatibility.UntestedAllowed);
    }

    internal static bool IsExactRz090528WindowsHost() =>
        IsRz090528WindowsHost(allowUntestedFirmware: false);

    internal static bool IsExactRz090581WindowsHost()
    {
        var registry = new WindowsRegistryValueSource();
        return new WindowsHostIdentitySource(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry))
            .ReadExactIdentity() is
        {
            Evidence: not null,
            Failure: null,
        };
    }

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

    private HostIdentityFailureDetail? ReadPlatformFailure()
    {
        WindowsPlatformEvidence evidence;
        try
        {
            evidence = platform.Read();
        }
        catch (Exception exception)
        {
            return new HostIdentityFailureDetail(
                HostIdentityFailureReason.PlatformReadFailed,
                "Platform",
                "Evidence",
                ExpectedValue: null,
                ActualValue: null,
                exception.GetType().Name,
                exception.HResult);
        }

        if (!evidence.IsWindows)
        {
            return PlatformMismatch("IsWindows", bool.TrueString, evidence.IsWindows.ToString());
        }

        if (!evidence.IsWorkstation)
        {
            return PlatformMismatch(
                "IsWorkstation", bool.TrueString, evidence.IsWorkstation.ToString());
        }

        if (evidence.Architecture != Architecture.X64)
        {
            return PlatformMismatch(
                "Architecture", Architecture.X64.ToString(), evidence.Architecture.ToString());
        }

        if (evidence.Version.Major != 10
            || evidence.Version.Minor != 0
            || evidence.Version.Build < 22000)
        {
            return PlatformMismatch(
                "Version", "10.0.22000 or later", evidence.Version.ToString());
        }

        return null;
    }

    private bool TryReadExact(
        string key,
        string source,
        string valueName,
        string expectedValue,
        out string actual,
        out HostIdentityFailureDetail? failure)
    {
        if (!TryReadValue(key, source, valueName, out actual, out failure))
        {
            if (failure?.Reason == HostIdentityFailureReason.RegistryValueMissing)
            {
                failure = failure with { ExpectedValue = Bounded(expectedValue) };
            }

            return false;
        }

        if (!string.Equals(actual, expectedValue, StringComparison.Ordinal))
        {
            failure = new HostIdentityFailureDetail(
                HostIdentityFailureReason.RegistryValueMismatch,
                source,
                valueName,
                Bounded(expectedValue),
                Bounded(actual));
            return false;
        }

        return true;
    }

    private bool TryReadValue(
        string key,
        string source,
        string valueName,
        out string actual,
        out HostIdentityFailureDetail? failure)
    {
        try
        {
            object? value = registry.Read(
                RegistryHive.LocalMachine,
                RegistryView.Registry64,
                key,
                valueName);
            actual = value as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(actual))
            {
                failure = new HostIdentityFailureDetail(
                    HostIdentityFailureReason.RegistryValueMissing,
                    source,
                    valueName,
                    ExpectedValue: "Non-empty string",
                    ActualValue: null);
                return false;
            }

            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            actual = string.Empty;
            failure = new HostIdentityFailureDetail(
                HostIdentityFailureReason.RegistryReadFailed,
                source,
                valueName,
                ExpectedValue: null,
                ActualValue: null,
                exception.GetType().Name,
                exception.HResult);
            return false;
        }
    }

    private static HostIdentityReadResult Blocked(HostIdentityFailureDetail failure) =>
        new(null, HidSessionFailureKind.IdentityBlocked, HostFirmwareCompatibility.Unknown, failure);

    private static HostIdentityFailureDetail PlatformMismatch(
        string field,
        string expectedValue,
        string actualValue) =>
        new(
            HostIdentityFailureReason.PlatformNotSupported,
            "Platform",
            field,
            Bounded(expectedValue),
            Bounded(actualValue));

    private static HostIdentityFailureDetail SourceMismatch(
        string field,
        string biosValue,
        string systemInformationValue) =>
        new(
            HostIdentityFailureReason.RegistrySourcesMismatch,
            "BIOS/SystemInformation",
            field,
            Bounded(biosValue),
            Bounded(systemInformationValue));

    private static string? Bounded(string? value) => value switch
    {
        null => null,
        { Length: <= MaximumDiagnosticValueLength } => value,
        _ => value[..MaximumDiagnosticValueLength],
    };
}
