using System;
using System.IO;
using Microsoft.Win32;
using WixToolset.BootstrapperApplicationApi;

namespace OpenBlade.Installer.UI;

internal enum BundleOwnershipKind
{
    Absent,
    Current,
    Other,
    Invalid,
}

internal enum UninstallOutcome
{
    Removed,
    RegistrationOnly,
    AlreadyAbsent,
    Failed,
}

internal sealed class UninstallMachineState
{
    internal UninstallMachineState(
        BundleOwnershipKind ownershipKind,
        string? bundleOwnerId,
        bool installDirectoryExists,
        bool serviceRegistrationExists)
    {
        OwnershipKind = ownershipKind;
        BundleOwnerId = bundleOwnerId;
        InstallDirectoryExists = installDirectoryExists;
        ServiceRegistrationExists = serviceRegistrationExists;
    }

    internal BundleOwnershipKind OwnershipKind { get; }

    internal string? BundleOwnerId { get; }

    internal bool InstallDirectoryExists { get; }

    internal bool ServiceRegistrationExists { get; }

    internal bool HasInstalledFootprint =>
        InstallDirectoryExists || ServiceRegistrationExists;

    internal bool IsCompletelyAbsent =>
        OwnershipKind == BundleOwnershipKind.Absent
        && !HasInstalledFootprint;
}

internal interface IUninstallPostconditionProbe
{
    UninstallMachineState Capture(string currentBundleOwnerId, string installDirectory);
}

internal sealed class WindowsUninstallPostconditionProbe : IUninstallPostconditionProbe
{
    private const string BundleOwnerSeparator = "|bundle:";
    private const string OwnershipRegistryPath = @"SOFTWARE\OSSBlade\OpenBlade\Installer";
    private const string ServiceRegistryPath = @"SYSTEM\CurrentControlSet\Services\OpenBlade";

    public UninstallMachineState Capture(
        string currentBundleOwnerId,
        string installDirectory)
    {
        if (!Guid.TryParseExact(currentBundleOwnerId, "D", out Guid currentOwner))
        {
            throw new ArgumentException(
                "The current bundle owner ID must be a D-format GUID.",
                nameof(currentBundleOwnerId));
        }

        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            throw new ArgumentException(
                "The OpenBlade installation directory is required.",
                nameof(installDirectory));
        }

        using RegistryKey baseKey = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using RegistryKey? ownershipKey = baseKey.OpenSubKey(
            OwnershipRegistryPath,
            writable: false);

        BundleOwnershipKind ownershipKind = BundleOwnershipKind.Absent;
        string? detectedOwnerId = null;
        if (ownershipKey is not null)
        {
            string? owner = ownershipKey.GetValue(
                "Owner",
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            string? installedVersion = ownershipKey.GetValue(
                "InstalledVersion",
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            string? installedBuild = ownershipKey.GetValue(
                "InstalledBuild",
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (TryReadBundleOwnerId(
                    owner,
                    installedVersion,
                    installedBuild,
                    out Guid detectedOwner))
            {
                detectedOwnerId = detectedOwner.ToString("D");
                ownershipKind = detectedOwner == currentOwner
                    ? BundleOwnershipKind.Current
                    : BundleOwnershipKind.Other;
            }
            else
            {
                ownershipKind = BundleOwnershipKind.Invalid;
            }
        }

        using RegistryKey? serviceKey = baseKey.OpenSubKey(
            ServiceRegistryPath,
            writable: false);
        return new UninstallMachineState(
            ownershipKind,
            detectedOwnerId,
            Directory.Exists(installDirectory),
            serviceKey is not null);
    }

    internal static bool TryReadBundleOwnerId(string? installedBuild, out Guid ownerId)
    {
        ownerId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(installedBuild))
        {
            return false;
        }

        string candidate = installedBuild!.Trim();
        int separatorIndex = candidate.LastIndexOf(
            BundleOwnerSeparator,
            StringComparison.Ordinal);
        if (separatorIndex <= 0
            || candidate.IndexOf(BundleOwnerSeparator, StringComparison.Ordinal)
                != separatorIndex)
        {
            return false;
        }

        string owner = candidate.Substring(separatorIndex + BundleOwnerSeparator.Length);
        return Guid.TryParseExact(owner, "D", out ownerId);
    }

    internal static bool TryReadBundleOwnerId(
        string? owner,
        string? installedVersion,
        string? installedBuild,
        out Guid ownerId)
    {
        ownerId = Guid.Empty;
        return IsValidMarkerValue(owner)
            && IsValidMarkerValue(installedVersion)
            && IsValidMarkerValue(installedBuild)
            && TryReadBundleOwnerId(installedBuild, out ownerId);
    }

    private static bool IsValidMarkerValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value!.Length <= 512;
}

internal static class UninstallPostconditionClassifier
{
    internal static UninstallOutcome Classify(
        UninstallMachineState before,
        UninstallMachineState after,
        bool runningBundleInstalled,
        bool sameVersionMaintenance,
        bool setupPackagePlanned,
        RegistrationType? registrationAfterApply)
    {
        if (before is null)
        {
            throw new ArgumentNullException(nameof(before));
        }

        if (after is null)
        {
            throw new ArgumentNullException(nameof(after));
        }

        if (before.OwnershipKind == BundleOwnershipKind.Invalid
            || after.OwnershipKind == BundleOwnershipKind.Invalid)
        {
            return UninstallOutcome.Failed;
        }

        if ((runningBundleInstalled || sameVersionMaintenance)
            && !setupPackagePlanned)
        {
            return UninstallOutcome.Failed;
        }

        if (runningBundleInstalled
            && registrationAfterApply != RegistrationType.None)
        {
            return UninstallOutcome.Failed;
        }

        bool expectsProductRemoval = sameVersionMaintenance
            || before.OwnershipKind == BundleOwnershipKind.Current
            || (before.OwnershipKind == BundleOwnershipKind.Absent
                && before.HasInstalledFootprint);
        if (expectsProductRemoval)
        {
            return after.IsCompletelyAbsent
                ? UninstallOutcome.Removed
                : UninstallOutcome.Failed;
        }

        if (before.OwnershipKind == BundleOwnershipKind.Other)
        {
            bool sameOwnerRemains = after.OwnershipKind == BundleOwnershipKind.Other
                && string.Equals(
                    before.BundleOwnerId,
                    after.BundleOwnerId,
                    StringComparison.OrdinalIgnoreCase);
            bool footprintUnchanged = before.HasInstalledFootprint
                && after.HasInstalledFootprint
                && before.InstallDirectoryExists == after.InstallDirectoryExists
                && before.ServiceRegistrationExists == after.ServiceRegistrationExists;
            return runningBundleInstalled
                && footprintUnchanged
                && sameOwnerRemains
                ? UninstallOutcome.RegistrationOnly
                : UninstallOutcome.Failed;
        }

        return before.IsCompletelyAbsent && after.IsCompletelyAbsent
            ? UninstallOutcome.AlreadyAbsent
            : UninstallOutcome.Failed;
    }
}
