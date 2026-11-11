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
    using var output = Console.OpenStandardOutput();
    await WindowsCandidateSetupReceiver.CommitScratchPayloadAsync(new Guid(locator), secret, async (commitment, token) =>
    {
        await output.WriteAsync(commitment, token);
        await output.FlushAsync(token);
        // EOF lets the observation finish. Only Setup's private pipe exchange
        // with the retained Rust fixture can supply readiness.
        if (await input.ReadAsync(new byte[1], token) != 0)
        {
            throw new InvalidDataException("Unexpected data on the scratch observation channel.");
        }
    }, deadline.Token);
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
    or OpenBladeReleaseVerificationException or OperationCanceledException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine(error);
    return 3;
}
