using System.Runtime.ExceptionServices;

namespace OpenBlade.Setup;

public interface ISetupPayloadTransaction : IAsyncDisposable
{
    bool HadExistingInstall { get; }
    bool IsActivated { get; }
    string? CommitWarning { get; }
    Task ActivateAsync(CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

public sealed record SetupServiceInstallState(bool Existed, bool WasRunning);

public sealed record SetupStartupInstallState(
    bool TrayShortcutExisted,
    bool SessionAgentShortcutExisted,
    bool StartMenuShortcutExisted,
    bool UninstallShortcutExisted = false,
    bool UninstallRegistrationExisted = false);

public sealed record SetupInstallLifecycleState(
    SetupServiceInstallState Service,
    SetupStartupInstallState Startup,
    bool BundleOwnershipExisted = false,
    bool DynamicLightingIdentityRegistered = false,
    bool BundleOwnershipMatchesCurrentBuild = true)
{
    public BundleOwnershipIdentity? BundleOwnership { get; init; }
}

public interface ISetupPlatform
{
    string CurrentUserSid { get; }
    Task<ISetupPayloadTransaction> StagePayloadAsync(CancellationToken cancellationToken);
    Task<SetupInstallLifecycleState> CaptureInstallLifecycleStateAsync(CancellationToken cancellationToken);
    Task PrepareServiceForInstallAsync(CancellationToken cancellationToken);
    Task RestoreServiceInstallStateAsync(
        SetupServiceInstallState priorState,
        CancellationToken cancellationToken);
    Task InstallUninstallerAsync(CancellationToken cancellationToken);
    Task WriteAuthorizedUserSidAsync(string sid, CancellationToken cancellationToken);
    Task InstallServiceAsync(CancellationToken cancellationToken);
    Task VerifyServiceHealthAsync(CancellationToken cancellationToken);
    Task InstallStartupShortcutAsync(CancellationToken cancellationToken);
    Task RemoveStartupShortcutAsync(CancellationToken cancellationToken);
    Task InstallSessionAgentStartupShortcutAsync(CancellationToken cancellationToken);
    Task RemoveSessionAgentStartupShortcutAsync(CancellationToken cancellationToken);
    Task InstallStartMenuShortcutAsync(CancellationToken cancellationToken);
    Task RemoveStartMenuShortcutAsync(CancellationToken cancellationToken);
    Task InstallUninstallShortcutAsync(CancellationToken cancellationToken);
    Task RemoveUninstallShortcutAsync(CancellationToken cancellationToken);
    Task InstallUninstallRegistrationAsync(CancellationToken cancellationToken);
    Task RemoveUninstallRegistrationAsync(CancellationToken cancellationToken);
    Task InstallBundleOwnershipAsync(CancellationToken cancellationToken);
    Task RestoreBundleOwnershipAsync(
        BundleOwnershipIdentity ownership,
        CancellationToken cancellationToken);
    Task RemoveBundleOwnershipAsync(CancellationToken cancellationToken);
    Task RestoreDynamicLightingIdentityAsync(CancellationToken cancellationToken);
    Task RemoveDynamicLightingIdentityAsync(CancellationToken cancellationToken);
    Task RemoveServiceAsync(CancellationToken cancellationToken);
    Task StopTrayAsync(CancellationToken cancellationToken);
    Task StopSessionAgentAsync(CancellationToken cancellationToken);
    Task RemoveBinariesAsync(CancellationToken cancellationToken);
    Task RemoveDataAsync(CancellationToken cancellationToken);
}

public sealed class SetupCommandRunner(
    ISetupPlatform platform,
    ISetupProgressReporter? progressReporter = null)
{
    private readonly ISetupProgressReporter progress =
        progressReporter ?? NullSetupProgressReporter.Instance;

    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (!SetupCommandParser.TryParseInvocation(arguments, out SetupInvocation invocation))
        {
            Console.Error.WriteLine(
                "Usage: OpenBlade.Setup.exe install | OpenBlade.Setup.exe uninstall "
                + "[--purge | --recover-orphaned-bundle]");
            return 2;
        }

        SetupCommand command = invocation.Command;
        if (command == SetupCommand.Install)
        {
            progress.Report(SetupProgressCode.InspectPayload);
            await using ISetupPayloadTransaction transaction =
                await platform.StagePayloadAsync(cancellationToken);
            SetupInstallLifecycleState? priorLifecycle = null;
            bool mutationStarted = false;
            try
            {
                progress.Report(SetupProgressCode.ReadInstallState);
                priorLifecycle = await platform.CaptureInstallLifecycleStateAsync(cancellationToken);
                ValidateBundleOwnershipSnapshot(priorLifecycle);
                if (priorLifecycle.BundleOwnershipExisted && !invocation.BundleManaged)
                {
                    throw new InvalidOperationException(
                        "This OpenBlade installation is managed by the graphical installer. "
                        + "Use Windows Installed apps or OpenBlade-Setup.exe to repair or remove it.");
                }
                if (priorLifecycle.Service.Existed && !transaction.HadExistingInstall)
                {
                    throw new InvalidOperationException(
                        "An OpenBlade service registration exists without its installed binary tree. "
                        + "Setup stopped before making changes; perform a manual repair or uninstall first.");
                }

                mutationStarted = true;
                progress.Report(SetupProgressCode.RemoveLegacyRegistration);
                await platform.RemoveStartupShortcutAsync(cancellationToken);
                await platform.RemoveSessionAgentStartupShortcutAsync(cancellationToken);
                await platform.RemoveStartMenuShortcutAsync(cancellationToken);
                await platform.RemoveUninstallShortcutAsync(cancellationToken);
                await platform.RemoveUninstallRegistrationAsync(cancellationToken);
                progress.Report(SetupProgressCode.StopServiceForReplacement);
                await platform.PrepareServiceForInstallAsync(cancellationToken);
                progress.Report(SetupProgressCode.StopTrayAndSessionAgent);
                await platform.StopTrayAsync(cancellationToken);
                await platform.StopSessionAgentAsync(cancellationToken);
                progress.Report(SetupProgressCode.RemoveDynamicLighting);
                await platform.RemoveDynamicLightingIdentityAsync(cancellationToken);
                await transaction.ActivateAsync(cancellationToken);
                if (!invocation.BundleManaged)
                {
                    progress.Report(SetupProgressCode.WriteStandaloneUninstaller);
                    await platform.InstallUninstallerAsync(cancellationToken);
                }
                progress.Report(SetupProgressCode.InstallService);
                await platform.InstallServiceAsync(cancellationToken);
                progress.Report(SetupProgressCode.VerifyServiceHealth);
                await platform.VerifyServiceHealthAsync(cancellationToken);
                progress.Report(SetupProgressCode.CreateShortcuts);
                await platform.InstallSessionAgentStartupShortcutAsync(cancellationToken);
                await platform.InstallStartMenuShortcutAsync(cancellationToken);
                if (!invocation.BundleManaged)
                {
                    await platform.InstallUninstallShortcutAsync(cancellationToken);
                    await platform.InstallUninstallRegistrationAsync(cancellationToken);
                }
                await platform.InstallStartupShortcutAsync(cancellationToken);
                if (invocation.BundleManaged)
                {
                    progress.Report(SetupProgressCode.WriteBundleOwnership);
                    await platform.InstallBundleOwnershipAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                WriteCommitWarningNoThrow(transaction.CommitWarning);
                return 0;
            }
            catch (Exception failure)
            {
                progress.Report(SetupProgressCode.RestorePreviousState);
                List<Exception> recoveryFailures = [];
                using CancellationTokenSource recoveryTimeout = new(TimeSpan.FromSeconds(60));
                CancellationToken recoveryToken = recoveryTimeout.Token;

                if (mutationStarted)
                {
                    await RecoverAsync(
                        () => platform.RemoveStartupShortcutAsync(recoveryToken),
                        recoveryFailures);
                    await RecoverAsync(
                        () => platform.RemoveSessionAgentStartupShortcutAsync(recoveryToken),
                        recoveryFailures);
                    await RecoverAsync(
                        () => platform.RemoveStartMenuShortcutAsync(recoveryToken),
                        recoveryFailures);
                    await RecoverAsync(
                        () => platform.RemoveUninstallShortcutAsync(recoveryToken),
                        recoveryFailures);
                    await RecoverAsync(
                        () => platform.RemoveUninstallRegistrationAsync(recoveryToken),
                        recoveryFailures);
                    await RecoverAsync(
                        () => platform.RemoveDynamicLightingIdentityAsync(recoveryToken),
                        recoveryFailures);
                }

                bool wasActivated = transaction.IsActivated;
                if (wasActivated)
                {
                    if (priorLifecycle?.Service is { Existed: true })
                    {
                        await RecoverAsync(() => platform.PrepareServiceForInstallAsync(recoveryToken), recoveryFailures);
                    }
                    else
                    {
                        await RecoverAsync(() => platform.RemoveServiceAsync(recoveryToken), recoveryFailures);
                    }

                    await RecoverAsync(() => platform.StopTrayAsync(recoveryToken), recoveryFailures);
                    await RecoverAsync(() => platform.StopSessionAgentAsync(recoveryToken), recoveryFailures);
                }

                await RecoverAsync(() => transaction.RollbackAsync(recoveryToken), recoveryFailures);

                if (mutationStarted)
                {
                    if (priorLifecycle?.DynamicLightingIdentityRegistered == true)
                    {
                        await RecoverAsync(
                            () => platform.RestoreDynamicLightingIdentityAsync(recoveryToken),
                            recoveryFailures);
                    }

                    if (priorLifecycle?.Service is { Existed: true } priorService)
                    {
                        await RecoverAsync(
                            () => platform.RestoreServiceInstallStateAsync(priorService, recoveryToken),
                            recoveryFailures);
                    }
                    else if (!wasActivated)
                    {
                        await RecoverAsync(() => platform.RemoveServiceAsync(recoveryToken), recoveryFailures);
                    }

                    if (priorLifecycle?.Startup is { SessionAgentShortcutExisted: true })
                    {
                        await RecoverAsync(
                            () => platform.InstallSessionAgentStartupShortcutAsync(recoveryToken),
                            recoveryFailures);
                    }

                    if (priorLifecycle?.Startup is { TrayShortcutExisted: true })
                    {
                        await RecoverAsync(() => platform.InstallStartupShortcutAsync(recoveryToken), recoveryFailures);
                    }

                    if (priorLifecycle?.Startup is { StartMenuShortcutExisted: true })
                    {
                        await RecoverAsync(
                            () => platform.InstallStartMenuShortcutAsync(recoveryToken),
                            recoveryFailures);
                    }

                    if (priorLifecycle?.Startup is { UninstallShortcutExisted: true })
                    {
                        await RecoverAsync(
                            () => platform.InstallUninstallShortcutAsync(recoveryToken),
                            recoveryFailures);
                    }

                    if (priorLifecycle?.Startup is { UninstallRegistrationExisted: true })
                    {
                        await RecoverAsync(
                            () => platform.InstallUninstallRegistrationAsync(recoveryToken),
                            recoveryFailures);
                    }

                    if (invocation.BundleManaged)
                    {
                        if (priorLifecycle?.BundleOwnershipExisted == true)
                        {
                            await RecoverAsync(
                                () => platform.RestoreBundleOwnershipAsync(
                                    priorLifecycle.BundleOwnership!,
                                    recoveryToken),
                                recoveryFailures);
                        }
                        else
                        {
                            await RecoverAsync(
                                () => platform.RemoveBundleOwnershipAsync(recoveryToken),
                                recoveryFailures);
                        }
                    }
                }

                if (recoveryFailures.Count > 0)
                {
                    throw new AggregateException([failure, .. recoveryFailures]);
                }

                ExceptionDispatchInfo.Capture(failure).Throw();
                throw new InvalidOperationException("Unreachable setup recovery path.");
            }
        }

        progress.Report(SetupProgressCode.ReadUninstallState);
        SetupInstallLifecycleState uninstallLifecycle =
            await platform.CaptureInstallLifecycleStateAsync(cancellationToken);
        ValidateBundleOwnershipSnapshot(uninstallLifecycle);
        if (invocation.RecoverOrphanedBundle && !uninstallLifecycle.BundleOwnershipExisted)
        {
            throw new InvalidOperationException(
                "No orphaned graphical-installer ownership marker was found.");
        }
        if (invocation.BundleManaged
            && uninstallLifecycle.BundleOwnershipExisted
            && !uninstallLifecycle.BundleOwnershipMatchesCurrentBuild)
        {
            Console.WriteLine(
                "A different OpenBlade payload owns this installation; "
                + "the superseded bundle uninstall was ignored.");
            return 0;
        }
        if (uninstallLifecycle.BundleOwnershipExisted
            && !invocation.BundleManaged
            && !invocation.RecoverOrphanedBundle)
        {
            throw new InvalidOperationException(
                "This OpenBlade installation is managed by the graphical installer. "
                + "Use Windows Installed apps or OpenBlade-Setup.exe to remove it.");
        }
        progress.Report(SetupProgressCode.RemoveShortcuts);
        await platform.RemoveStartupShortcutAsync(cancellationToken);
        await platform.RemoveSessionAgentStartupShortcutAsync(cancellationToken);
        await platform.RemoveStartMenuShortcutAsync(cancellationToken);
        progress.Report(SetupProgressCode.RemoveService);
        await platform.RemoveServiceAsync(cancellationToken);
        progress.Report(SetupProgressCode.StopTrayAndSessionAgent);
        await platform.StopTrayAsync(cancellationToken);
        await platform.StopSessionAgentAsync(cancellationToken);
        try
        {
            progress.Report(SetupProgressCode.RemoveDynamicLightingAndOwnership);
            await platform.RemoveDynamicLightingIdentityAsync(cancellationToken);
            await platform.RemoveUninstallShortcutAsync(cancellationToken);
            await platform.RemoveUninstallRegistrationAsync(cancellationToken);
            if (invocation.BundleManaged || invocation.RecoverOrphanedBundle)
            {
                await platform.RemoveBundleOwnershipAsync(cancellationToken);
            }
            progress.Report(SetupProgressCode.RemoveProgramFiles);
            await platform.RemoveBinariesAsync(cancellationToken);
        }
        catch (Exception failure)
        {
            progress.Report(SetupProgressCode.RestoreRegistrationState);
            List<Exception> recoveryFailures = [];
            using CancellationTokenSource recoveryTimeout = new(TimeSpan.FromSeconds(30));
            if (uninstallLifecycle.DynamicLightingIdentityRegistered)
            {
                await RecoverAsync(
                    () => platform.RestoreDynamicLightingIdentityAsync(recoveryTimeout.Token),
                    recoveryFailures);
            }

            if (uninstallLifecycle.Startup.UninstallShortcutExisted)
            {
                await RecoverAsync(
                    () => platform.InstallUninstallShortcutAsync(recoveryTimeout.Token),
                    recoveryFailures);
            }

            if (uninstallLifecycle.Startup.UninstallRegistrationExisted)
            {
                await RecoverAsync(
                    () => platform.InstallUninstallRegistrationAsync(recoveryTimeout.Token),
                    recoveryFailures);
            }

            if ((invocation.BundleManaged || invocation.RecoverOrphanedBundle)
                && uninstallLifecycle.BundleOwnershipExisted)
            {
                await RecoverAsync(
                    () => platform.RestoreBundleOwnershipAsync(
                        uninstallLifecycle.BundleOwnership!,
                        recoveryTimeout.Token),
                    recoveryFailures);
            }

            if (recoveryFailures.Count > 0)
            {
                throw new AggregateException([failure, .. recoveryFailures]);
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
            throw new InvalidOperationException("Unreachable uninstall recovery path.");
        }

        if (command == SetupCommand.UninstallAndPurge)
        {
            progress.Report(SetupProgressCode.RemoveSavedData);
            await platform.RemoveDataAsync(cancellationToken);
        }

        return 0;
    }

    private static async Task RecoverAsync(Func<Task> recovery, List<Exception> failures)
    {
        try
        {
            await recovery();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void ValidateBundleOwnershipSnapshot(
        SetupInstallLifecycleState lifecycle)
    {
        bool hasSnapshot = lifecycle.BundleOwnership is not null;
        if (lifecycle.BundleOwnershipExisted != hasSnapshot
            || lifecycle.BundleOwnership is { IsWellFormed: false })
        {
            throw new InvalidOperationException(
                "The graphical-installer ownership marker could not be captured safely.");
        }
    }

    private static void WriteCommitWarningNoThrow(string? warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return;
        }

        try
        {
            Console.Error.WriteLine(warning);
        }
        catch
        {
            // Reporting after the commit point must never enter payload or lifecycle recovery.
        }
    }
}
