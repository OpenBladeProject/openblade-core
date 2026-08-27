using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

internal static class WindowsSetupPlatformTestFactory
{
    public static WindowsSetupPlatform Create(
        ISetupServiceControl? serviceControl = null,
        IStartupShortcutWriter? shortcutWriter = null,
        IAuthorizedUserSidWriter? sidWriter = null,
        ISetupServiceHealthProbe? healthProbe = null,
        ISetupFileOperations? fileOperations = null,
        ISetupProcessOperations? processOperations = null,
        SetupPaths? paths = null,
        string? payloadDirectory = null,
        ISetupPayloadFileSystem? payloadFileSystem = null,
        IMachineDataDirectorySecurity? machineDataDirectorySecurity = null,
        IUninstallRegistration? uninstallRegistration = null,
        IBundleOwnershipRegistration? bundleOwnershipRegistration = null,
        IInstalledUninstallerWriter? installedUninstallerWriter = null,
        IDynamicLightingIdentityRegistration? dynamicLightingIdentityRegistration = null,
        ISetupProgressReporter? progressReporter = null,
        string? setupExecutablePath = null,
        string? currentUserSid = null) =>
        new(
            new WindowsSetupPlatformOptions(
                ServiceControl: serviceControl,
                ShortcutWriter: shortcutWriter,
                SidWriter: sidWriter,
                HealthProbe: healthProbe,
                FileOperations: fileOperations,
                ProcessOperations: processOperations,
                Paths: paths,
                PayloadDirectory: payloadDirectory,
                PayloadFileSystem: payloadFileSystem,
                MachineDataDirectorySecurity: machineDataDirectorySecurity,
                UninstallRegistration: uninstallRegistration,
                BundleOwnershipRegistration: bundleOwnershipRegistration,
                InstalledUninstallerWriter: installedUninstallerWriter,
                DynamicLightingIdentityRegistration: dynamicLightingIdentityRegistration,
                ProgressReporter: progressReporter,
                SetupExecutablePath: setupExecutablePath,
                CurrentUserSid: currentUserSid));
}
