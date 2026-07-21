using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>
/// Sends transient Reactive-lighting pulses sequentially on a bounded queue.
/// Every accepted key press is retained in arrival order until delivered or
/// the owning session is stopped. The pump accepts only data-minimized matrix
/// slots, not translated text, virtual-key values, or app/window identity.
/// </summary>
public sealed class ReactiveKeyPulsePump : IAsyncDisposable
{
    public const int DefaultCapacity = 256;
    public const int DefaultMaxDeliveryAttempts = 3;
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly Func<ReactiveKeyPulse, CancellationToken, Task> sendAsync;
    private readonly Action<Exception>? errorHandler;
    private readonly int capacity;
    private readonly int maxDeliveryAttempts;
    private readonly TimeSpan retryDelay;
    private readonly Queue<ReactiveKeyPulse> pending = [];
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
                return false;
            }

            pending.Enqueue(pulse);
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
            ReactiveKeyPulse queued;
            lock (sync)
            {
                if (pending.Count == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }

                queued = pending.Dequeue();
            }

            await SendWithBoundedRetryAsync(queued, cancellationToken)
                .ConfigureAwait(false);
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
