namespace OpenBlade.Service;

public interface IServiceShutdownSignal
{
    void RequestShutdownAfterResponse();
}

public sealed class HostServiceShutdownSignal : IServiceShutdownSignal
{
    internal static readonly TimeSpan ResponseGracePeriod = TimeSpan.FromMilliseconds(500);

    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly Func<TimeSpan, Task> delay;
    private int requested;

    public HostServiceShutdownSignal(IHostApplicationLifetime applicationLifetime)
        : this(
            applicationLifetime,
            static gracePeriod => Task.Delay(gracePeriod))
    {
    }

    internal HostServiceShutdownSignal(
        IHostApplicationLifetime applicationLifetime,
        Func<TimeSpan, Task> delay)
    {
        this.applicationLifetime = applicationLifetime
            ?? throw new ArgumentNullException(nameof(applicationLifetime));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

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
        await delay(ResponseGracePeriod).ConfigureAwait(false);
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
