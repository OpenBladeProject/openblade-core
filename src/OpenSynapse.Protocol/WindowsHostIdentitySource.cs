using Microsoft.Win32;

namespace OpenSynapse.Protocol;

internal readonly record struct HostIdentityEvidence(
    string Manufacturer, string ProductName, string Sku, string BiosVersion);

internal readonly record struct HostIdentityReadResult(
    HostIdentityEvidence? Evidence, HidSessionFailureKind? Failure);

internal interface IRegistryValueSource
{
    object? Read(RegistryHive hive, RegistryView view, string key, string valueName);
}

internal interface IHostIdentitySource
{
    HostIdentityReadResult ReadExactIdentity();
}

internal sealed class WindowsHostIdentitySource(IRegistryValueSource registry) : IHostIdentitySource
{
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string SystemInformationKey = @"SYSTEM\CurrentControlSet\Control\SystemInformation";

    public HostIdentityReadResult ReadExactIdentity()
    {
        if (!TryReadExact(BiosKey, "SystemManufacturer", "Razer", out string biosManufacturer)
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
