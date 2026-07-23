namespace OpenBlade.Tray;

/// <summary>
/// Runs reconstructed function-row actions sequentially and bounds pending
/// key presses. The low-level keyboard hook only enqueues work and never
/// performs IPC, WMI, or injected input while Windows is waiting on it.
/// </summary>
public sealed class FunctionRowActionPump : IAsyncDisposable
{
    public const int DefaultCapacity = 16;

    private readonly Func<FunctionRowAction, CancellationToken, Task> executeAsync;
    private readonly Action<Exception>? errorHandler;
    private readonly int capacity;
    private readonly Queue<FunctionRowAction> pending = [];
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly Task worker;
    private bool accepting = true;
    private int disposed;

    public FunctionRowActionPump(
        Func<FunctionRowAction, CancellationToken, Task> executeAsync,
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

    public bool TryEnqueue(FunctionRowAction action)
    {
        if (!Enum.IsDefined(action))
        {
            return false;
        }

        lock (sync)
        {
            if (!accepting || pending.Count >= capacity)
            {
                return false;
            }

            pending.Enqueue(action);
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
            FunctionRowAction action;
            lock (sync)
            {
                if (pending.Count == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }

                action = pending.Dequeue();
            }

            try
            {
                await executeAsync(action, cancellationToken).ConfigureAwait(false);
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
