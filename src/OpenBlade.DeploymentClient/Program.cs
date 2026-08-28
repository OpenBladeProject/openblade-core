using OpenBlade.DeploymentClient;

if (args.Length != 0)
{
    return 1;
}

WindowsInstalledProcessInventory inventory = new();
DeploymentClientCoordinator coordinator = new(
    new BrokerPipeChannel(
        new WindowsBrokerPipeConnector(),
        new WindowsProtectedBrokerIdentityVerifier()),
    new WindowsClientIntegritySource(),
    new WindowsClientSessionSource(),
    new WindowsLegacyStartupCleanup(),
    new WindowsInstalledPayloadVerifier(),
    inventory,
    new WindowsInstalledProcessShutdown(
        new WindowsInstalledShutdownEventSource(),
        inventory),
    new WindowsDeploymentProcessLauncher(),
    new CryptographicRequestIdSource(),
    Console.Error.WriteLine);
return await coordinator.ApplyFixedInboxAsync(CancellationToken.None) ? 0 : 1;
