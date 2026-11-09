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
        IDotNetRuntimeAdmission? dotNetRuntimeAdmission = null,
        string? setupExecutablePath = null,
        string? currentUserSid = null,
        ISignInTaskScheduler? signInTaskScheduler = null,
        ISetupGuardianQuiescenceGuard? guardianQuiescenceGuard = null) =>
        new(
            new WindowsSetupPlatformOptions(
                ServiceControl: serviceControl,
                ShortcutWriter: shortcutWriter,
                SignInTaskScheduler: signInTaskScheduler ?? new InMemorySignInTaskScheduler(),
                SidWriter: sidWriter,
                HealthProbe: healthProbe,
                FileOperations: fileOperations,
                ProcessOperations: processOperations,
                Paths: paths,
                PayloadDirectory: payloadDirectory,
                PayloadFileSystem: payloadFileSystem,
                MachineDataDirectorySecurity: machineDataDirectorySecurity ?? NoOpMachineDataDirectorySecurity.Instance,
                UninstallRegistration: uninstallRegistration,
                BundleOwnershipRegistration: bundleOwnershipRegistration,
                InstalledUninstallerWriter: installedUninstallerWriter,
                DynamicLightingIdentityRegistration: dynamicLightingIdentityRegistration,
                ProgressReporter: progressReporter,
                DotNetRuntimeAdmission: dotNetRuntimeAdmission ?? AcceptingDotNetRuntimeAdmission.Instance,
                SetupExecutablePath: setupExecutablePath,
                CurrentUserSid: currentUserSid,
                // Unit fixtures must not consult installed guardian records or
                // enumerate real helpers. Guardian behavior tests inject their
                // own guard with controlled paths and process observations.
                GuardianQuiescenceGuard: guardianQuiescenceGuard ?? AcceptingGuardianQuiescenceGuard.Instance));

    private sealed class NoOpMachineDataDirectorySecurity : IMachineDataDirectorySecurity
    {
        internal static NoOpMachineDataDirectorySecurity Instance { get; } = new();

        public Task ProtectAsync(string directory, CancellationToken cancellationToken, bool includeDescendants = true) =>
            Task.CompletedTask;
    }

    private sealed class AcceptingGuardianQuiescenceGuard : ISetupGuardianQuiescenceGuard
    {
        internal static AcceptingGuardianQuiescenceGuard Instance { get; } = new();

        public void EnsureClear()
        {
        }
    }

    private sealed class AcceptingDotNetRuntimeAdmission : IDotNetRuntimeAdmission
    {
        internal static AcceptingDotNetRuntimeAdmission Instance { get; } = new();

        public void EnsureAvailable()
        {
        }
    }
}

internal sealed class InMemorySignInTaskScheduler : ISignInTaskScheduler
{
    private readonly Dictionary<string, SignInTaskState> states = [];

    public SignInTaskState GetState(string taskName) =>
        states.TryGetValue(taskName, out SignInTaskState state) ? state : SignInTaskState.Missing;

    public void Register(SignInTaskDefinition definition) =>
        states[definition.TaskName] = SignInTaskState.Enabled;

    public void Delete(string taskName) => states.Remove(taskName);
}
