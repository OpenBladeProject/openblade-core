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
    bool SessionAgentShortcutExisted);

public sealed record SetupInstallLifecycleState(
    SetupServiceInstallState Service,
    SetupStartupInstallState Startup);

public interface ISetupPlatform
{
    string CurrentUserSid { get; }
    Task<ISetupPayloadTransaction> StagePayloadAsync(CancellationToken cancellationToken);
    Task<SetupInstallLifecycleState> CaptureInstallLifecycleStateAsync(CancellationToken cancellationToken);
    Task PrepareServiceForInstallAsync(CancellationToken cancellationToken);
    Task RestoreServiceInstallStateAsync(
        SetupServiceInstallState priorState,
        CancellationToken cancellationToken);
    Task WriteAuthorizedUserSidAsync(string sid, CancellationToken cancellationToken);
    Task InstallServiceAsync(CancellationToken cancellationToken);
    Task VerifyServiceHealthAsync(CancellationToken cancellationToken);
    Task InstallStartupShortcutAsync(CancellationToken cancellationToken);
    Task RemoveStartupShortcutAsync(CancellationToken cancellationToken);
    Task InstallSessionAgentStartupShortcutAsync(CancellationToken cancellationToken);
    Task RemoveSessionAgentStartupShortcutAsync(CancellationToken cancellationToken);
    Task RemoveServiceAsync(CancellationToken cancellationToken);
    Task StopTrayAsync(CancellationToken cancellationToken);
    Task StopSessionAgentAsync(CancellationToken cancellationToken);
    Task RemoveBinariesAsync(CancellationToken cancellationToken);
    Task RemoveDataAsync(CancellationToken cancellationToken);
}

public sealed class SetupCommandRunner(ISetupPlatform platform)
{
    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (!SetupCommandParser.TryParse(arguments, out SetupCommand command))
        {
            Console.Error.WriteLine("Usage: OpenBlade.Setup.exe install | OpenBlade.Setup.exe uninstall [--purge]");
            return 2;
        }

        if (command == SetupCommand.Install)
        {
            await using ISetupPayloadTransaction transaction =
                await platform.StagePayloadAsync(cancellationToken);
            SetupInstallLifecycleState? priorLifecycle = null;
            bool mutationStarted = false;
            try
            {
                priorLifecycle = await platform.CaptureInstallLifecycleStateAsync(cancellationToken);
                if (priorLifecycle.Service.Existed && !transaction.HadExistingInstall)
                {
                    throw new InvalidOperationException(
                        "An OpenBlade service registration exists without its installed binary tree. "
                        + "Setup stopped before making changes; perform a manual repair or uninstall first.");
                }

                mutationStarted = true;
                await platform.RemoveStartupShortcutAsync(cancellationToken);
                await platform.RemoveSessionAgentStartupShortcutAsync(cancellationToken);
                await platform.PrepareServiceForInstallAsync(cancellationToken);
                await platform.StopTrayAsync(cancellationToken);
                await platform.StopSessionAgentAsync(cancellationToken);
                await transaction.ActivateAsync(cancellationToken);
                await platform.WriteAuthorizedUserSidAsync(platform.CurrentUserSid, cancellationToken);
                await platform.InstallServiceAsync(cancellationToken);
                await platform.VerifyServiceHealthAsync(cancellationToken);
                await platform.InstallSessionAgentStartupShortcutAsync(cancellationToken);
                if (!transaction.HadExistingInstall || priorLifecycle.Startup.TrayShortcutExisted)
                {
                    await platform.InstallStartupShortcutAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                WriteCommitWarningNoThrow(transaction.CommitWarning);
                return 0;
            }
            catch (Exception failure)
            {
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
                }

                if (recoveryFailures.Count > 0)
                {
                    throw new AggregateException([failure, .. recoveryFailures]);
                }

                ExceptionDispatchInfo.Capture(failure).Throw();
                throw new InvalidOperationException("Unreachable setup recovery path.");
            }
        }

        await platform.RemoveStartupShortcutAsync(cancellationToken);
        await platform.RemoveSessionAgentStartupShortcutAsync(cancellationToken);
        await platform.RemoveServiceAsync(cancellationToken);
        await platform.StopTrayAsync(cancellationToken);
        await platform.StopSessionAgentAsync(cancellationToken);
        await platform.RemoveBinariesAsync(cancellationToken);
        if (command == SetupCommand.UninstallAndPurge)
        {
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
