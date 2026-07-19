using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>
/// Sends physical performance-shortcut presses sequentially. The small bounded
/// queue preserves deliberate presses while preventing a faulty input source
/// from creating an unbounded control backlog.
/// </summary>
public sealed class PerformanceShortcutTriggerPump : IAsyncDisposable
{
    public const int DefaultCapacity = 8;

    private readonly Func<PerformanceShortcutSource, CancellationToken, Task> sendAsync;
    private readonly Action<Exception>? errorHandler;
    private readonly int capacity;
    private readonly Queue<PerformanceShortcutSource> pending = [];
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly Task worker;
    private bool accepting = true;
    private int disposed;

    public PerformanceShortcutTriggerPump(
        Func<PerformanceShortcutSource, CancellationToken, Task> sendAsync,
        int capacity = DefaultCapacity,
        Action<Exception>? errorHandler = null)
    {
        this.sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        this.errorHandler = errorHandler;
        worker = RunAsync(lifetime.Token);
    }

    public bool TryEnqueue(PerformanceShortcutSource source)
    {
        if (!Enum.IsDefined(source))
        {
            return false;
        }

        lock (sync)
        {
            if (!accepting || pending.Count >= capacity)
            {
                return false;
            }

            pending.Enqueue(source);
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
            PerformanceShortcutSource source;
            lock (sync)
            {
                if (pending.Count == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }

                source = pending.Dequeue();
            }

            try
            {
                await sendAsync(source, cancellationToken).ConfigureAwait(false);
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
}
