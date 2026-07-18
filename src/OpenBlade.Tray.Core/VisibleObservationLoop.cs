namespace OpenBlade.Tray;

public sealed class VisibleObservationLoop : IAsyncDisposable
{
    private static readonly TimeSpan DefaultObservationInterval = TimeSpan.FromSeconds(2);

    private readonly Func<CancellationToken, Task> observeAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly Action<Exception>? errorHandler;
    private readonly Func<TimeSpan> observationIntervalProvider;
    private readonly object sync = new();
    private CancellationTokenSource? loopCancellation;
    private Task? loopTask;
    private TaskCompletionSource? wakeSignal;
    private bool visible;
    private bool disposed;

    public VisibleObservationLoop(
        Func<CancellationToken, Task> observeAsync,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action<Exception>? errorHandler = null,
        Func<TimeSpan>? observationIntervalProvider = null)
    {
        this.observeAsync = observeAsync ?? throw new ArgumentNullException(nameof(observeAsync));
        this.delayAsync = delayAsync
            ?? Task.Delay;
        this.errorHandler = errorHandler;
        this.observationIntervalProvider = observationIntervalProvider
            ?? (() => DefaultObservationInterval);
    }

    public void StartOrWake()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            visible = true;
            if (loopTask is null)
            {
                StartLoopLocked();
                return;
            }

            if (loopCancellation?.IsCancellationRequested == false)
            {
                wakeSignal?.TrySetResult();
            }
        }
    }

    public void Stop()
    {
        lock (sync)
        {
            visible = false;
            loopCancellation?.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? activeLoop;
        lock (sync)
        {
            if (!disposed)
            {
                disposed = true;
                visible = false;
                loopCancellation?.Cancel();
            }

            activeLoop = loopTask;
        }

        if (activeLoop is not null)
        {
            await activeLoop.ConfigureAwait(false);
        }
    }

    private void StartLoopLocked()
    {
        var cancellation = new CancellationTokenSource();
        loopCancellation = cancellation;
        loopTask = RunOwnedLoopAsync(cancellation);
    }

    private async Task RunOwnedLoopAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await RunLoopAsync(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(loopCancellation, cancellation))
                {
                    wakeSignal = null;
                    loopCancellation = null;
                    loopTask = null;
                    cancellation.Dispose();
                    if (visible && !disposed)
                    {
                        StartLoopLocked();
                    }
                }
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (true)
        {
            TaskCompletionSource wake;
            lock (sync)
            {
                if (!visible || disposed || token.IsCancellationRequested)
                {
                    return;
                }

                wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wakeSignal = wake;
            }

            try
            {
                await observeAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
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

            if (token.IsCancellationRequested)
            {
                return;
            }

            Task delay;
            try
            {
                TimeSpan interval = observationIntervalProvider();
                if (interval <= TimeSpan.Zero || interval == Timeout.InfiniteTimeSpan)
                {
                    interval = DefaultObservationInterval;
                }
                delay = delayAsync(interval, token);
                Task completed = await Task.WhenAny(delay, wake.Task).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
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
