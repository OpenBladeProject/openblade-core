using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record PerformanceShortcutIntent(
    PerformanceShortcutSource Source,
    PowerSource? PowerSource,
    PerformancePreset? Target,
    long Generation);

/// <summary>
/// Coalesces physical performance-shortcut presses. Selection feedback is
/// published before enqueueing; only the final intent after a short quiet
/// period reaches the comparatively slow firmware path.
/// </summary>
public sealed class PerformanceShortcutTriggerPump : IAsyncDisposable
{
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMilliseconds(1250);

    private readonly Func<PerformanceShortcutIntent, CancellationToken, Task> sendAsync;
    private readonly Action<Exception>? errorHandler;
    private readonly TimeSpan quietPeriod;
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly Task worker;
    private PerformanceShortcutIntent? pending;
    private long pendingRevision;
    private bool accepting = true;
    private int disposed;

    public PerformanceShortcutTriggerPump(
        Func<PerformanceShortcutIntent, CancellationToken, Task> sendAsync,
        TimeSpan? quietPeriod = null,
        Action<Exception>? errorHandler = null)
    {
        this.sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
        this.quietPeriod = quietPeriod ?? DefaultQuietPeriod;
        if (this.quietPeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(quietPeriod));
        }

        this.errorHandler = errorHandler;
        worker = RunAsync(lifetime.Token);
    }

    public bool TryEnqueue(PerformanceShortcutIntent intent)
    {
        if (!Enum.IsDefined(intent.Source)
            || intent.Generation <= 0
            || intent.PowerSource is { } powerSource && !Enum.IsDefined(powerSource)
            || intent.Target is { } target && !Enum.IsDefined(target))
        {
            return false;
        }

        bool wakeWorker;
        lock (sync)
        {
            if (!accepting)
            {
                return false;
            }

            wakeWorker = pending is null;
            pending = intent;
            pendingRevision++;
        }

        if (wakeWorker)
        {
            signal.Release();
        }

        return true;
    }

    public PerformanceShortcutIntent? ClearPending()
    {
        lock (sync)
        {
            PerformanceShortcutIntent? discarded = pending;
            pending = null;
            pendingRevision++;
            return discarded;
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
            pending = null;
            pendingRevision++;
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
            PerformanceShortcutIntent? intent = await TakeAfterQuietPeriodAsync(
                cancellationToken).ConfigureAwait(false);
            if (intent is null)
            {
                continue;
            }

            try
            {
                await sendAsync(intent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }
    }

    private async Task<PerformanceShortcutIntent?> TakeAfterQuietPeriodAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            long observedRevision;
            lock (sync)
            {
                if (pending is null)
                {
                    return null;
                }

                observedRevision = pendingRevision;
            }

            await Task.Delay(quietPeriod, cancellationToken).ConfigureAwait(false);

            lock (sync)
            {
                if (pending is null)
                {
                    return null;
                }
                if (observedRevision != pendingRevision)
                {
                    continue;
                }

                PerformanceShortcutIntent intent = pending;
                pending = null;
                return intent;
            }
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
