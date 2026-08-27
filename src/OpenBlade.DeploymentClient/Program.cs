using OpenBlade.DeploymentClient;

if (args.Length != 0)
{
    return 1;
}

DeploymentClientCoordinator coordinator = new(
    new BrokerPipeChannel(
        new WindowsBrokerPipeConnector(),
        new WindowsProtectedBrokerIdentityVerifier()),
    new WindowsClientIntegritySource(),
    new WindowsClientSessionSource(),
    new WindowsInstalledPayloadVerifier(),
    new WindowsInstalledProcessInventory(),
    new WindowsDeploymentProcessLauncher(),
    new CryptographicRequestIdSource());
return await coordinator.ApplyFixedInboxAsync(CancellationToken.None) ? 0 : 1;
