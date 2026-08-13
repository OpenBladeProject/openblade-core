namespace OpenBlade.Tray;

/// <summary>
/// Runs valid enum actions sequentially with a bounded, reject-on-full queue.
/// Input hooks only enqueue work and never perform IPC, WMI, or injected input
/// while Windows is waiting on them.
/// </summary>
public sealed class BoundedEnumActionPump<T> : IAsyncDisposable
    where T : struct, Enum
{
    public const int DefaultCapacity = 16;

    private readonly Func<T, CancellationToken, Task> executeAsync;
    private readonly Action<Exception>? errorHandler;
    private readonly int capacity;
    private readonly Queue<T> pending = [];
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly Task worker;
    private bool accepting = true;
    private int disposed;

    public BoundedEnumActionPump(
        Func<T, CancellationToken, Task> executeAsync,
        int capacity = DefaultCapacity,
        Action<Exception>? errorHandler = null)
    {
        this.executeAsync = executeAsync
            ?? throw new ArgumentNullException(nameof(executeAsync));
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        this.errorHandler = errorHandler;
        worker = RunAsync(lifetime.Token);
    }

    public bool TryEnqueue(T value)
    {
        if (!Enum.IsDefined(value))
        {
            return false;
        }

        lock (sync)
        {
            if (!accepting || pending.Count >= capacity)
            {
                return false;
            }

            bool wakeWorker = pending.Count == 0;
            pending.Enqueue(value);
            if (wakeWorker)
            {
                SignalWorker();
            }
            return true;
        }
    }

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
                T value;
                lock (sync)
                {
                    if (pending.Count == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        break;
                    }

                    value = pending.Dequeue();
                }

                try
                {
                    await executeAsync(value, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
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
}
