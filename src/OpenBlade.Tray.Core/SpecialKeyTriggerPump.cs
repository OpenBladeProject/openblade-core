namespace OpenBlade.Tray;

/// <summary>
/// Runs special-key actions sequentially and bounds the number of pending
/// presses so a faulty input source cannot create an unbounded action queue.
/// </summary>
public sealed class SpecialKeyTriggerPump : IAsyncDisposable
{
    public const int DefaultCapacity = 16;

    private readonly Func<SpecialKeySlot, CancellationToken, Task> executeAsync;
    private readonly Action<Exception>? errorHandler;
    private readonly int capacity;
    private readonly Queue<SpecialKeySlot> pending = [];
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly Task worker;
    private bool accepting = true;
    private int disposed;

    public SpecialKeyTriggerPump(
        Func<SpecialKeySlot, CancellationToken, Task> executeAsync,
        int capacity = DefaultCapacity,
        Action<Exception>? errorHandler = null)
    {
        this.executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        this.errorHandler = errorHandler;
        worker = RunAsync(lifetime.Token);
    }

    public bool TryEnqueue(SpecialKeySlot slot)
    {
        if (!Enum.IsDefined(slot))
        {
            return false;
        }

        lock (sync)
        {
            if (!accepting || pending.Count >= capacity)
            {
                return false;
            }

            pending.Enqueue(slot);
            signal.Release();
            return true;
        }
    }

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
            SpecialKeySlot slot;
            lock (sync)
            {
                if (pending.Count == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }
                slot = pending.Dequeue();
            }

            try
            {
                await executeAsync(slot, cancellationToken).ConfigureAwait(false);
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
