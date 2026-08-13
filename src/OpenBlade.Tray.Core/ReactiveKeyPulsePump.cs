using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>
/// Sends transient Reactive-lighting pulses sequentially on a bounded queue.
/// Fresh key presses are preferred over stale backlog when delivery falls
/// behind. The pump accepts only data-minimized matrix slots, not translated
/// text, virtual-key values, or app/window identity.
/// </summary>
public sealed class ReactiveKeyPulsePump : IAsyncDisposable
{
    public const int DefaultCapacity = 1024;
    public const int DefaultMaxDeliveryAttempts = 3;
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly Func<ReactiveKeyPulse, CancellationToken, Task> sendAsync;
    private readonly Action<Exception>? errorHandler;
    private readonly int capacity;
    private readonly int maxDeliveryAttempts;
    private readonly TimeSpan retryDelay;
    private readonly Queue<ReactiveKeyPulse> pending = [];
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly Task worker;
    private bool accepting = true;
    private int disposed;

    public ReactiveKeyPulsePump(
        Func<ReactiveKeyPulse, CancellationToken, Task> sendAsync,
        int capacity = DefaultCapacity,
        Action<Exception>? errorHandler = null,
        int maxDeliveryAttempts = DefaultMaxDeliveryAttempts,
        TimeSpan? retryDelay = null)
    {
        this.sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        if (maxDeliveryAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDeliveryAttempts));
        }

        this.capacity = capacity;
        this.errorHandler = errorHandler;
        this.maxDeliveryAttempts = maxDeliveryAttempts;
        this.retryDelay = retryDelay ?? DefaultRetryDelay;
        if (this.retryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
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

            if (pending.Count >= capacity)
            {
                int trimTarget = Math.Max(0, capacity / 4);
                while (pending.Count > trimTarget)
                {
                    _ = pending.Dequeue();
                }
            }

            bool wakeWorker = pending.Count == 0;
            pending.Enqueue(pulse);
            if (wakeWorker)
            {
                SignalWorker();
            }
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
            if (Volatile.Read(ref disposed) == 0)
            {
                _ = signal.Wait(0);
            }
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
        SignalWorker();
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
            while (true)
            {
                ReactiveKeyPulse queued;
                lock (sync)
                {
                    if (pending.Count == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        break;
                    }

                    queued = pending.Dequeue();
                }

                await SendWithBoundedRetryAsync(queued, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private void SignalWorker()
    {
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already pending. The worker drains the queue after it wakes.
        }
    }

    private async Task SendWithBoundedRetryAsync(
        ReactiveKeyPulse queued,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (int attempt = 1; attempt <= maxDeliveryAttempts; attempt++)
        {
            try
            {
                await sendAsync(queued, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                if (attempt == maxDeliveryAttempts)
                {
                    break;
                }

                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        if (lastFailure is not null)
        {
            ReportFailure(lastFailure);
        }
    }

    private void ReportFailure(Exception exception)
    {
        try
        {
            errorHandler?.Invoke(exception);
        }
        catch
        {
        }
    }

}
