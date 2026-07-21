using System.Runtime.CompilerServices;
using System.Threading.Channels;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>
/// Fans one service subscription out to the independent session features.
/// Each consumer receives only the latest pending status, matching the service's
/// own bounded status-subscription semantics without opening one pipe per feature.
/// </summary>
public sealed class SharedServiceStatusFeed(IServiceStatusWatcher upstream)
    : IServiceStatusWatcher, IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<long, Channel<ServiceWatchUpdate>> subscribers = [];
    private ServiceWatchUpdate? latest;
    private Exception? completionError;
    private long nextSubscriberId;
    private int runStarted;
    private int completed;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref runStarted, 1) != 0)
        {
            throw new InvalidOperationException("The shared service status feed is already running.");
        }

        Exception? failure = null;
        try
        {
            await foreach (ServiceWatchUpdate update in upstream
                .WatchStatusAsync(cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                Publish(update);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            Complete(failure);
        }
    }

    public async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
        [EnumeratorCancellation] CancellationToken token)
    {
        Channel<ServiceWatchUpdate> channel = Channel.CreateBounded<ServiceWatchUpdate>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        long id = 0;
        lock (sync)
        {
            if (Volatile.Read(ref completed) != 0)
            {
                channel.Writer.TryComplete(completionError);
            }
            else
            {
                id = ++nextSubscriberId;
                subscribers.Add(id, channel);
                if (latest is not null)
                {
                    channel.Writer.TryWrite(latest);
                }
            }
        }

        try
        {
            await foreach (ServiceWatchUpdate update in channel.Reader.ReadAllAsync(token))
            {
                yield return update;
            }
        }
        finally
        {
            if (id != 0)
            {
                lock (sync)
                {
                    subscribers.Remove(id);
                }
            }
            channel.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync()
    {
        Complete(null);
        return ValueTask.CompletedTask;
    }

    internal int SubscriberCount
    {
        get
        {
            lock (sync)
            {
                return subscribers.Count;
            }
        }
    }

    internal ServiceWatchUpdate? LatestUpdate
    {
        get
        {
            lock (sync)
            {
                return latest;
            }
        }
    }

    private void Publish(ServiceWatchUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (sync)
        {
            if (Volatile.Read(ref completed) != 0)
            {
                return;
            }

            latest = update;
            foreach (Channel<ServiceWatchUpdate> subscriber in subscribers.Values)
            {
                subscriber.Writer.TryWrite(update);
            }
        }
    }

    private void Complete(Exception? error)
    {
        Channel<ServiceWatchUpdate>[] targets;
        lock (sync)
        {
            if (completed != 0)
            {
                return;
            }

            completionError = error;
            Volatile.Write(ref completed, 1);
            targets = subscribers.Values.ToArray();
            subscribers.Clear();
        }
        foreach (Channel<ServiceWatchUpdate> target in targets)
        {
            target.Writer.TryComplete(error);
        }
    }
}
