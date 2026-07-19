namespace OpenBlade.Service;

public interface IServiceShutdownSignal
{
    void RequestShutdownAfterResponse();
}

public sealed class HostServiceShutdownSignal(
    IHostApplicationLifetime applicationLifetime) : IServiceShutdownSignal
{
    private static readonly TimeSpan ResponseGracePeriod = TimeSpan.FromMilliseconds(500);
    private int requested;

    public void RequestShutdownAfterResponse()
    {
        if (Interlocked.Exchange(ref requested, 1) != 0)
        {
            return;
        }

        _ = StopAfterResponseAsync();
    }

    private async Task StopAfterResponseAsync()
    {
        await Task.Delay(ResponseGracePeriod).ConfigureAwait(false);
        applicationLifetime.StopApplication();
    }
}

internal sealed class NullServiceShutdownSignal : IServiceShutdownSignal
{
    public static NullServiceShutdownSignal Instance { get; } = new();

    private NullServiceShutdownSignal()
    {
    }

    public void RequestShutdownAfterResponse()
    {
    }
}
