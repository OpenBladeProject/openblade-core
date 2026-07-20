using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>
/// Sends transient Reactive-lighting pulses sequentially on a bounded queue.
/// Pending repeats for the same matrix slot are coalesced, and inputs that
/// waited too long to remain visually responsive are discarded. The pump
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
    private readonly LinkedList<PendingPulse> pending = [];
    private readonly Dictionary<int, LinkedListNode<PendingPulse>> pendingBySlot = [];
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
            if (pendingBySlot.TryGetValue(pulse.MatrixSlot, out LinkedListNode<PendingPulse>? existing))
            {
                existing.Value = new PendingPulse(pulse, enqueuedAt);
                pending.Remove(existing);
                pending.AddLast(existing);
                return true;
            }

            if (pendingBySlot.Count >= capacity)
            {
                return false;
            }

            LinkedListNode<PendingPulse> added = pending.AddLast(
                new PendingPulse(pulse, enqueuedAt));
            pendingBySlot.Add(pulse.MatrixSlot, added);
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
            pendingBySlot.Clear();
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
            pendingBySlot.Clear();
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

                LinkedListNode<PendingPulse> first = pending.First!;
                queued = first.Value;
                pending.RemoveFirst();
                pendingBySlot.Remove(queued.Pulse.MatrixSlot);
            }

            if (timeProvider.GetElapsedTime(queued.EnqueuedAt) > maximumQueueAge)
            {
                continue;
            }

            try
            {
                await sendAsync(queued.Pulse, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                errorHandler?.Invoke(exception);
            }
        }
    }

    private readonly record struct PendingPulse(ReactiveKeyPulse Pulse, long EnqueuedAt);
}
