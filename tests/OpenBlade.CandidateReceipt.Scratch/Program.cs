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
    // The direct launcher releases this inherited channel only after binding.
    byte[] locator = new byte[16];
    byte[] secret = new byte[WindowsCandidateSetupReceiver.LaunchSecretLength];
    await input.ReadExactlyAsync(locator, deadline.Token);
    await input.ReadExactlyAsync(secret, deadline.Token);
    await WindowsCandidateSetupReceiver.CommitScratchPayloadAsync(new Guid(locator), secret, deadline.Token);
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
    or OpenBladeReleaseVerificationException or OperationCanceledException)
{
    Console.Error.WriteLine(error);
    return 3;
}
