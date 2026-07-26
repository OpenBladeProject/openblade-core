using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenBlade.Protocol;

internal readonly record struct HostIdentityEvidence(
    string Manufacturer, string ProductName, string Sku, string BiosVersion);

internal readonly record struct HostIdentityReadResult(
    HostIdentityEvidence? Evidence, HidSessionFailureKind? Failure);

internal readonly record struct WindowsPlatformEvidence(
    bool IsWindows,
    bool IsWorkstation,
    Version Version,
    Architecture Architecture);

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

internal sealed class WindowsHostIdentitySource(
    IRegistryValueSource registry,
    IWindowsPlatformEvidenceSource platform) : IHostIdentitySource
{
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string SystemInformationKey = @"SYSTEM\CurrentControlSet\Control\SystemInformation";

    public HostIdentityReadResult ReadExactIdentity()
    {
        if (!IsAuthorizedPlatform()
            || !TryReadExact(BiosKey, "SystemManufacturer", "Razer", out string biosManufacturer)
            || !TryReadExact(BiosKey, "SystemProductName", "Blade 16 - RZ09-0581", out string biosProductName)
            || !TryReadExact(BiosKey, "SystemSKU", "RZ09-05819EN4", out string sku)
            || !TryReadExact(BiosKey, "BIOSVersion", "3.01", out string biosVersion)
            || !TryReadExact(SystemInformationKey, "SystemManufacturer", "Razer", out string systemManufacturer)
            || !TryReadExact(SystemInformationKey, "SystemProductName", "Blade 16 - RZ09-0581", out string systemProductName)
            || !TryReadExact(SystemInformationKey, "BIOSVersion", "3.01", out string systemBiosVersion)
            || !string.Equals(biosManufacturer, systemManufacturer, StringComparison.Ordinal)
            || !string.Equals(biosProductName, systemProductName, StringComparison.Ordinal)
            || !string.Equals(biosVersion, systemBiosVersion, StringComparison.Ordinal))
        {
            return new(null, HidSessionFailureKind.IdentityBlocked);
        }

        return new(
            new HostIdentityEvidence(biosManufacturer, biosProductName, sku, biosVersion),
            null);
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
        try
        {
            object? value = registry.Read(
                RegistryHive.LocalMachine,
                RegistryView.Registry64,
                key,
                valueName);
            actual = value as string ?? string.Empty;
            return string.Equals(actual, expected, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            actual = string.Empty;
            return false;
        }
    }
}
