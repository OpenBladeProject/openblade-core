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
    IDeploymentProcessLauncher processes,
    IRequestIdSource requestIds,
    Action<string>? diagnostic = null)
{
    private static readonly TimeSpan SessionAgentSurvivalWindow = TimeSpan.FromSeconds(1);

    internal async Task<bool> ApplyFixedInboxAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (integrity.IsElevated() || !session.IsActiveLocalConsoleSession())
            {
                return Fail("client-context-rejected");
            }

            Guid requestId = requestIds.Create();
            if (requestId == Guid.Empty)
            {
                throw new InvalidOperationException("The deployment client request ID is unavailable.");
            }

            DeploymentBrokerResponse response = await broker.ApplyFixedInboxAsync(
                requestId,
                cancellationToken).ConfigureAwait(false);
            if (!DeploymentBrokerProtocol.IsExactSuccess(response, requestId))
            {
                return Fail($"broker-response:{response.Status}:{response.Code}");
            }

            if (integrity.IsElevated() || !session.IsActiveLocalConsoleSession())
            {
                return Fail("client-context-changed");
            }

            InstalledPayloadPaths? paths = installedPayload.GetVerifiedPaths();
            if (paths is null)
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

            using ILaunchedDeploymentProcess? tray = processes.Launch(
                InstalledProcessLaunchPlan.Tray(paths));
            bool launched = tray is not null
                && await tray.RemainsAliveAsync(SessionAgentSurvivalWindow)
                    .ConfigureAwait(false)
                && inventory.Inspect(paths).HasExactFinalState;
            return launched || Fail("tray-relaunch-failed");
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

    private bool Fail(string code)
    {
        diagnostic?.Invoke(code);
        return false;
    }
}
