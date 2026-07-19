using System.Threading.Channels;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public interface IStatusSubscriptionHub
{
    StatusSubscriptionLease Subscribe(CancellationToken cancellationToken);

    void Publish(ServiceStatusSnapshot snapshot);
}

public sealed class StatusSubscriptionHub : IStatusSubscriptionHub
{
    internal const int MaximumSubscribers = 8;
    private static readonly TimeSpan MaximumConnectionLifetime = TimeSpan.FromMinutes(15);
    private readonly object sync = new();
    private readonly Dictionary<long, SubscriptionState> subscriptions = [];
    private readonly TimeProvider timeProvider;
    private ServiceStatusSnapshot? latest;
    private int subscriberCount;
    private long nextSubscriptionId;

    public StatusSubscriptionHub(
        ServiceStatusSnapshot initialSnapshot,
        TimeProvider timeProvider)
    {
        latest = initialSnapshot ?? throw new ArgumentNullException(nameof(initialSnapshot));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public StatusSubscriptionHub(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public StatusSubscriptionLease Subscribe(CancellationToken cancellationToken)
    {
        ReserveSlot();
        try
        {
            long id = Interlocked.Increment(ref nextSubscriptionId);
            Channel<ServiceStatusSnapshot> channel =
                Channel.CreateBounded<ServiceStatusSnapshot>(new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
            var state = new SubscriptionState(id, channel, Release);
            state.AttachLifetime(timeProvider, MaximumConnectionLifetime);
            state.AttachCallerCancellation(cancellationToken);
            lock (sync)
            {
                subscriptions.Add(id, state);
                if (latest is not null)
                {
                    state.TryPublish(latest);
                }
            }

            return new StatusSubscriptionLease(state);
        }
        catch
        {
            Interlocked.Decrement(ref subscriberCount);
            throw;
        }
    }

    public void Publish(ServiceStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SubscriptionState[] targets;
        lock (sync)
        {
            if (latest is not null && snapshot.Revision <= latest.Revision)
            {
                return;
            }

            latest = snapshot;
            targets = subscriptions.Values.ToArray();
        }

        foreach (SubscriptionState target in targets)
        {
            target.TryPublish(snapshot);
        }
    }

    private void ReserveSlot()
    {
        while (true)
        {
            int observed = Volatile.Read(ref subscriberCount);
            if (observed >= MaximumSubscribers)
            {
                throw new InvalidOperationException("The maximum number of status subscribers is active.");
            }

            if (Interlocked.CompareExchange(
                    ref subscriberCount,
                    observed + 1,
                    observed) == observed)
            {
                return;
            }
        }
    }

    private void Release(long id)
    {
        bool removed;
        lock (sync)
        {
            removed = subscriptions.Remove(id);
        }

        if (removed)
        {
            Interlocked.Decrement(ref subscriberCount);
        }
    }

    internal sealed class SubscriptionState(
        long id,
        Channel<ServiceStatusSnapshot> channel,
        Action<long> release)
    {
        private CancellationTokenRegistration callerCancellation;
        private CancellationToken callerToken;
        private ITimer? lifetimeTimer;
        private int disposed;

        public ChannelReader<ServiceStatusSnapshot> Reader => channel.Reader;

        public void AttachCallerCancellation(CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                return;
            }

            callerToken = cancellationToken;
            callerCancellation = cancellationToken.Register(
                static state => ((SubscriptionState)state!).Cancel(),
                this);
        }

        public void AttachLifetime(TimeProvider timeProvider, TimeSpan lifetime)
        {
            lifetimeTimer = timeProvider.CreateTimer(
                static state => ((SubscriptionState)state!).Expire(),
                this,
                lifetime,
                Timeout.InfiniteTimeSpan);
        }

        public void TryPublish(ServiceStatusSnapshot snapshot) =>
            channel.Writer.TryWrite(snapshot);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            callerCancellation.Dispose();
            lifetimeTimer?.Dispose();
            channel.Writer.TryComplete();
            release(id);
        }

        private void Cancel() => channel.Writer.TryComplete(
            new OperationCanceledException(
                "The status subscription was canceled.",
                innerException: null,
                token: callerToken));

        private void Expire() => channel.Writer.TryComplete();
    }
}

public sealed class StatusSubscriptionLease : IAsyncDisposable
{
    private StatusSubscriptionHub.SubscriptionState? state;

    internal StatusSubscriptionLease(StatusSubscriptionHub.SubscriptionState state)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public IAsyncEnumerable<ServiceStatusSnapshot> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        StatusSubscriptionHub.SubscriptionState current = Volatile.Read(ref state)
            ?? throw new ObjectDisposedException(nameof(StatusSubscriptionLease));
        return current.Reader.ReadAllAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        StatusSubscriptionHub.SubscriptionState? current = Interlocked.Exchange(ref state, null);
        current?.Dispose();
        return ValueTask.CompletedTask;
    }
}
