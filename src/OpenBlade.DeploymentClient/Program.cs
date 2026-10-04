using OpenBlade.DeploymentClient;

if (args is ["--deployment-status"])
{
    return await DeploymentStatusCommand.RunAsync(Console.Out, Console.Error, CancellationToken.None);
}

if (!DeploymentClientArguments.TryParse(args, out bool cpuVoltage))
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
    Console.Error.WriteLine,
    cpuVoltageHost: new WindowsCpuVoltageHostStatusVerifier(Console.Out.WriteLine));
return (cpuVoltage
    ? await coordinator.ApplyCpuVoltageInboxAsync(CancellationToken.None)
    : await coordinator.ApplyFixedInboxAsync(CancellationToken.None)) ? 0 : 1;
