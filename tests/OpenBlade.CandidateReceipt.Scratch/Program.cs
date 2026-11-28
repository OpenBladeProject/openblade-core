using System.IO.Pipes;
using OpenBlade.DeploymentBroker;
using OpenBlade.Setup;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
NamedPipeClientStream[]? channels = null;
Exception? failure = null;
int result = 0;
try
{
    channels = ScratchChildChannels.OpenChild(args);
    using var diagnostics = new StreamWriter(channels[2], leaveOpen: true) { AutoFlush = true };
    Console.SetError(diagnostics);
    var input = channels[0];
    var output = channels[1];
    // The parent authenticates every channel before releasing this data.
    byte[] locator = new byte[16];
    byte[] secret = new byte[WindowsCandidateSetupReceiver.LaunchSecretLength];
    await input.ReadExactlyAsync(locator, deadline.Token);
    await input.ReadExactlyAsync(secret, deadline.Token);
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
}
catch (Exception error) when (ScratchFixtureFailure.IsRefusal(error))
{
    failure = error;
    result = 3;
}
try
{
    ScratchFixtureFailure.Cleanup(failure,
        () => ScratchReceiverClosure.DisposeRetainedResources(channels?.Take(2) ?? []));
}
catch (Exception error) when (ScratchFixtureFailure.IsRefusal(error))
{
    result = 3;
    if (channels is not null)
    {
        try
        {
            using var diagnostics = new StreamWriter(channels[2], leaveOpen: true) { AutoFlush = true };
            diagnostics.WriteLine(error);
        }
        catch (IOException) { result = 3; } // Closed diagnostic transport cannot confirm success.
    }
}
try { channels?.ElementAtOrDefault(2)?.Dispose(); }
catch (Exception error) when (ScratchFixtureFailure.IsRefusal(error)) { result = 3; }
return result;
