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
    IInstalledPayloadVerifier installedPayload,
    IInstalledProcessInventory inventory,
    IDeploymentProcessLauncher processes,
    IRequestIdSource requestIds)
{
    private static readonly TimeSpan SessionAgentSurvivalWindow = TimeSpan.FromSeconds(1);

    internal async Task<bool> ApplyFixedInboxAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (integrity.IsElevated() || !session.IsActiveLocalConsoleSession())
            {
                return false;
            }

            Guid requestId = requestIds.Create();
            if (requestId == Guid.Empty)
            {
                throw new InvalidOperationException("The deployment client request ID is unavailable.");
            }

            DeploymentBrokerResponse response = await broker.ApplyFixedInboxAsync(
                requestId,
                cancellationToken).ConfigureAwait(false);
            if (!DeploymentBrokerProtocol.IsExactSuccess(response, requestId)
                || integrity.IsElevated()
                || !session.IsActiveLocalConsoleSession())
            {
                return false;
            }

            InstalledPayloadPaths? paths = installedPayload.GetVerifiedPaths();
            if (paths is null)
            {
                return false;
            }

            if (!inventory.Inspect(paths).IsEmpty)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using ILaunchedDeploymentProcess? sessionAgent = processes.Launch(
                InstalledProcessLaunchPlan.SessionAgent(paths));
            if (sessionAgent is null
                || !await sessionAgent.RemainsAliveAsync(SessionAgentSurvivalWindow)
                    .ConfigureAwait(false)
                || !inventory.Inspect(paths).HasOnlySessionAgent)
            {
                return false;
            }

            using ILaunchedDeploymentProcess? tray = processes.Launch(
                InstalledProcessLaunchPlan.Tray(paths));
            return tray is not null
                && await tray.RemainsAliveAsync(SessionAgentSurvivalWindow)
                    .ConfigureAwait(false)
                && inventory.Inspect(paths).HasExactFinalState;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException
            or TimeoutException or Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
