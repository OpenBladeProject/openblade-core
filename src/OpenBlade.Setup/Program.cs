using OpenBlade.Setup;

try
{
    return await new SetupCommandRunner(new WindowsSetupPlatform())
        .RunAsync(args, CancellationToken.None);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"OpenBlade setup failed: {exception.Message}");
    return 1;
}
