using System.Threading.Channels;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>Never performs file I/O on the keyboard-hook thread.</summary>
internal sealed class SnapTapStatusPublisher : IDisposable
{
    private readonly Channel<SnapTapRuntimeStatus> channel = Channel.CreateBounded<SnapTapRuntimeStatus>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly Task writer;
    private int failed;
    private SnapTapRuntimeState? lastState;

    internal SnapTapStatusPublisher()
    {
        writer = Task.Run(async () =>
        {
            try
            {
                await foreach (SnapTapRuntimeStatus status in channel.Reader.ReadAllAsync()
                    .ConfigureAwait(false))
                {
                    SnapTapStatusStore.Write(status);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Volatile.Write(ref failed, 1);
            }
        });
    }

    internal bool Failed => Volatile.Read(ref failed) != 0;

    internal void Publish(SnapTapRuntimeState state)
    {
        if (lastState == state)
        {
            return;
        }
        lastState = state;
        channel.Writer.TryWrite(new SnapTapRuntimeStatus(
            state,
            DateTimeOffset.UtcNow,
            Environment.ProcessId));
    }

    public void Dispose()
    {
        channel.Writer.TryComplete();
        // The hook is already removed before waiting for the final status flush.
        try
        {
            writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            Volatile.Write(ref failed, 1);
        }
    }
}
