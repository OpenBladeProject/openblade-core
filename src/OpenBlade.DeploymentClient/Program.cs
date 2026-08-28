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
    new WindowsLegacyStartupCleanup(),
    new WindowsInstalledPayloadVerifier(),
    new WindowsInstalledProcessInventory(),
    new WindowsDeploymentProcessLauncher(),
    new CryptographicRequestIdSource(),
    Console.Error.WriteLine);
return await coordinator.ApplyFixedInboxAsync(CancellationToken.None) ? 0 : 1;
