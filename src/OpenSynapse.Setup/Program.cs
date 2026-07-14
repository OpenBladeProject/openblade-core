using OpenSynapse.Setup;

try
{
    return await new SetupCommandRunner(new WindowsSetupPlatform())
        .RunAsync(args, CancellationToken.None);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"OpenSynapse setup failed: {exception.Message}");
    return 1;
}
