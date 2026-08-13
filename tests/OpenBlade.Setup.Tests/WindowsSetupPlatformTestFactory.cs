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
        IBrightnessHidDriverManager? brightnessHidDriverManager = null,
        ISetupProgressReporter? progressReporter = null,
        string? setupExecutablePath = null) =>
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
                BrightnessHidDriverManager: brightnessHidDriverManager
                    ?? new NoOpBrightnessHidDriverManager(),
                ProgressReporter: progressReporter,
                SetupExecutablePath: setupExecutablePath));
}

internal sealed class NoOpBrightnessHidDriverManager : IBrightnessHidDriverManager
{
    public BrightnessHidDriverInstallState State { get; set; } = new(false);
    public List<string> Operations { get; } = [];

    public Task<BrightnessHidDriverInstallState> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("capture");
        return Task.FromResult(State);
    }

    public Task InstallAsync(string infPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("install");
        State = new(
            true,
            @"ROOT\OSSBLADE_OPENBLADE_BRIGHTNESS_HID\0000",
            @"C:\Windows\INF\oem42.inf",
            new Version(1, 0, 0, 0),
            WindowsBrightnessHidDriverManager.DriverProvider,
            true);
        return Task.CompletedTask;
    }

    public Task VerifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("verify");
        return Task.CompletedTask;
    }

    public Task RestoreAsync(
        BrightnessHidDriverInstallState priorState,
        string fallbackInfPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("restore");
        State = priorState;
        return Task.CompletedTask;
    }

    public Task<string?> CommitAsync(
        BrightnessHidDriverInstallState priorState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("commit");
        return Task.FromResult<string?>(null);
    }

    public Task RemoveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("remove");
        State = new(false);
        return Task.CompletedTask;
    }

    public Task<string?> CommitRemovalAsync(
        BrightnessHidDriverInstallState priorState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("removal-commit");
        return Task.FromResult<string?>(null);
    }
}
