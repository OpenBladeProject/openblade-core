using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>
/// Sends transient Reactive-lighting pulses sequentially on a bounded queue.
/// Every accepted key press is retained in arrival order; inputs that waited
/// too long to remain visually responsive are discarded. The pump
/// accepts only data-minimized matrix slots, not translated text, virtual-key
/// values, or app/window identity.
/// </summary>
public sealed class ReactiveKeyPulsePump : IAsyncDisposable
{
    public const int DefaultCapacity = 256;
    public static readonly TimeSpan DefaultMaximumQueueAge = TimeSpan.FromMilliseconds(100);

    private readonly Func<ReactiveKeyPulse, CancellationToken, Task> sendAsync;
    private readonly Action<Exception>? errorHandler;
    private readonly int capacity;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan maximumQueueAge;
    private readonly Queue<PendingPulse> pending = [];
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly Task worker;
    private bool accepting = true;
    private int disposed;

    public ReactiveKeyPulsePump(
        Func<ReactiveKeyPulse, CancellationToken, Task> sendAsync,
        int capacity = DefaultCapacity,
        Action<Exception>? errorHandler = null,
        TimeProvider? timeProvider = null,
        TimeSpan? maximumQueueAge = null)
    {
        this.sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        this.errorHandler = errorHandler;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.maximumQueueAge = maximumQueueAge ?? DefaultMaximumQueueAge;
        if (this.maximumQueueAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumQueueAge));
        }
        worker = RunAsync(lifetime.Token);
    }

    public bool TryEnqueue(ReactiveKeyPulse pulse)
    {
        lock (sync)
        {
            if (!accepting)
            {
                return false;
            }

            long enqueuedAt = timeProvider.GetTimestamp();
            if (pending.Count >= capacity)
            {
                return false;
            }

            pending.Enqueue(new PendingPulse(pulse, enqueuedAt));
            signal.Release();
            return true;
        }
    }

    /// <summary>
    /// Drops pulses that have not started sending. An already in-flight send is
    /// allowed to finish; the service still validates that Reactive is active.
    /// </summary>
    public void ClearPending()
    {
        lock (sync)
        {
            pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lock (sync)
        {
            accepting = false;
            pending.Clear();
        }

        lifetime.Cancel();
        signal.Release();
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        signal.Dispose();
        lifetime.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            PendingPulse queued;
            lock (sync)
            {
                if (pending.Count == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }

                queued = pending.Dequeue();
            }

            TimeSpan elapsed = timeProvider.GetElapsedTime(queued.EnqueuedAt);
            if (elapsed >= maximumQueueAge)
            {
                continue;
            }

            using var deadline = new CancellationTokenSource(
                maximumQueueAge - elapsed,
                timeProvider);
            using var delivery = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
            try
            {
                await sendAsync(queued.Pulse, delivery.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // The visual response is no longer useful. Cancel the whole
                // connect/request round trip so it cannot apply stale input or
                // hold newer key presses behind it.
            }
            catch (Exception exception)
            {
                errorHandler?.Invoke(exception);
            }
        }
    }

    private readonly record struct PendingPulse(ReactiveKeyPulse Pulse, long EnqueuedAt);
}
