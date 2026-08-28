using System.ComponentModel;
using System.Security.Cryptography;

namespace OpenBlade.DeploymentClient;

internal interface IRequestIdSource
{
    Guid Create();
}

internal sealed class CryptographicRequestIdSource : IRequestIdSource
{
    public Guid Create()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return new Guid(bytes);
    }
}

internal sealed class DeploymentClientCoordinator(
    IBrokerChannel broker,
    IClientIntegritySource integrity,
    IClientSessionSource session,
    ILegacyStartupCleanup legacyStartup,
    IInstalledPayloadVerifier installedPayload,
    IInstalledProcessInventory inventory,
    IInstalledProcessShutdown shutdown,
    IDeploymentProcessLauncher processes,
    IRequestIdSource requestIds,
    Action<string>? diagnostic = null)
{
    private static readonly TimeSpan SessionAgentSurvivalWindow = TimeSpan.FromSeconds(1);
    private const string ExactRollbackCode = "install-rolled-back";

    internal async Task<bool> ApplyFixedInboxAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!HasRequiredContext())
            {
                return Fail("client-context-rejected");
            }

            InstalledPayloadPaths? expectedPaths = installedPayload.GetExpectedPaths();
            if (expectedPaths is null)
            {
                return Fail("installed-paths-unavailable");
            }

            InstalledProcessInventory initialInventory = inventory.Inspect(expectedPaths);
            if (!initialInventory.TryGetRestorableShape(out InstalledProcessShape originalShape))
            {
                return Fail("installed-process-state-rejected");
            }

            if (originalShape != InstalledProcessShape.None)
            {
                InstalledPayloadPaths? verifiedBeforeShutdown =
                    installedPayload.GetVerifiedPaths();
                if (verifiedBeforeShutdown is null
                    || !InstalledPayloadPathPolicy.IsExact(
                        verifiedBeforeShutdown,
                        expectedPaths))
                {
                    return Fail("installed-payload-unverified-before-shutdown");
                }

                bool stopped;
                try
                {
                    stopped = await shutdown.ShutdownAsync(
                        verifiedBeforeShutdown,
                        originalShape,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is
                    IOException or UnauthorizedAccessException or InvalidDataException
                    or TimeoutException or Win32Exception or InvalidOperationException)
                {
                    return Fail("installed-process-shutdown-failed");
                }

                if (!stopped)
                {
                    return Fail("installed-process-shutdown-failed");
                }
            }

            if (!HasRequiredContext())
            {
                return Fail("client-context-changed-before-broker");
            }

            if (!inventory.Inspect(expectedPaths).IsEmpty)
            {
                return Fail("installed-process-shutdown-incomplete");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Guid requestId = requestIds.Create();
            if (requestId == Guid.Empty)
            {
                throw new InvalidOperationException("The deployment client request ID is unavailable.");
            }

            DeploymentBrokerResponse response = await broker.ApplyFixedInboxAsync(
                requestId,
                cancellationToken).ConfigureAwait(false);
            if (DeploymentBrokerProtocol.IsExactSuccess(response, requestId))
            {
                return await CompleteSuccessfulInstallAsync(
                    expectedPaths,
                    cancellationToken).ConfigureAwait(false);
            }

            if (IsExactRollback(response, requestId))
            {
                diagnostic?.Invoke($"broker-response:{response.Status}:{response.Code}");
                await RestoreAfterRollbackAsync(
                    expectedPaths,
                    originalShape,
                    cancellationToken).ConfigureAwait(false);
                return false;
            }

            return Fail($"broker-response:{response.Status}:{response.Code}");
        }
        catch (OperationCanceledException)
        {
            return Fail("client-operation-canceled");
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException
            or TimeoutException or Win32Exception or InvalidOperationException)
        {
            return Fail($"client-exception:{exception.GetType().Name}");
        }
    }

    private async Task<bool> CompleteSuccessfulInstallAsync(
        InstalledPayloadPaths expectedPaths,
        CancellationToken cancellationToken)
    {
        if (!HasRequiredContext())
        {
            return Fail("client-context-changed");
        }

        InstalledPayloadPaths? paths = installedPayload.GetVerifiedPaths();
        if (paths is null || !InstalledPayloadPathPolicy.IsExact(paths, expectedPaths))
        {
            return Fail("installed-payload-unverified");
        }

        if (!inventory.Inspect(paths).IsEmpty)
        {
            return Fail("installed-processes-already-running");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            legacyStartup.RemoveCurrentUserEntries();
        }
        catch (Exception)
        {
            return Fail("legacy-startup-cleanup-failed");
        }

        return await LaunchShapeAsync(
            paths,
            InstalledProcessShape.SessionAgentAndTray,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreAfterRollbackAsync(
        InstalledPayloadPaths expectedPaths,
        InstalledProcessShape originalShape,
        CancellationToken cancellationToken)
    {
        if (!HasRequiredContext())
        {
            return;
        }

        InstalledPayloadPaths? paths = installedPayload.GetVerifiedPaths();
        if (paths is null
            || !InstalledPayloadPathPolicy.IsExact(paths, expectedPaths)
            || !inventory.Inspect(paths).IsEmpty)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await LaunchShapeAsync(paths, originalShape, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> LaunchShapeAsync(
        InstalledPayloadPaths paths,
        InstalledProcessShape shape,
        CancellationToken cancellationToken)
    {
        if (shape == InstalledProcessShape.None)
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using ILaunchedDeploymentProcess? sessionAgent = processes.Launch(
            InstalledProcessLaunchPlan.SessionAgent(paths));
        if (sessionAgent is null
            || !await sessionAgent.RemainsAliveAsync(SessionAgentSurvivalWindow)
                .ConfigureAwait(false)
            || !inventory.Inspect(paths).HasOnlySessionAgent)
        {
            return Fail("session-agent-relaunch-failed");
        }

        if (shape == InstalledProcessShape.SessionAgentOnly)
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using ILaunchedDeploymentProcess? tray = processes.Launch(
            InstalledProcessLaunchPlan.Tray(paths));
        bool launched = tray is not null
            && await tray.RemainsAliveAsync(SessionAgentSurvivalWindow)
                .ConfigureAwait(false)
            && inventory.Inspect(paths).HasExactFinalState;
        return launched || Fail("tray-relaunch-failed");
    }

    private bool HasRequiredContext() =>
        !integrity.IsElevated() && session.IsActiveLocalConsoleSession();

    private static bool IsExactRollback(
        DeploymentBrokerResponse response,
        Guid requestId) =>
        response.ProtocolVersion == DeploymentBrokerProtocol.ProtocolVersion
        && response.RequestId == requestId
        && response.Status == DeploymentBrokerResponseStatus.RolledBack
        && string.Equals(response.Code, ExactRollbackCode, StringComparison.Ordinal);

    private bool Fail(string code)
    {
        diagnostic?.Invoke(code);
        return false;
    }
}
