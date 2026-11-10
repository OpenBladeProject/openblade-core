using OpenBlade.Deployment;
using OpenBlade.Setup;

if (args.Length != 0)
{
    return 2;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
using var input = Console.OpenStandardInput();
try
{
    // The parent writes this locator only after publishing the child's PID.
    byte[] locator = new byte[16];
    await input.ReadExactlyAsync(locator, deadline.Token);
    await WindowsCandidateSetupReceiver.CommitScratchPayloadAsync(new Guid(locator), deadline.Token);
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
    or OpenBladeReleaseVerificationException)
{
    Console.Error.WriteLine(error);
    return 3;
}
