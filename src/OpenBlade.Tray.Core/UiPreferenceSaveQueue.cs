using System.Runtime.ExceptionServices;

namespace OpenBlade.Tray;

public sealed class UiPreferenceSaveQueue : IAsyncDisposable
{
    private readonly Func<UiPreferences, CancellationToken, Task> saveAsync;
    private readonly Action<Exception> reportFailure;
    private readonly CoalescingCommitState<UiPreferences> commits = new();
    private readonly object sync = new();
    private readonly Task worker;
    private TaskCompletionSource wake = NewWake();
    private Task? disposal;
    private bool accepting = true;
    private Exception? workerFailure;

    public UiPreferenceSaveQueue(
        Func<UiPreferences, CancellationToken, Task> saveAsync,
        Action<Exception> reportFailure)
    {
        this.saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        this.reportFailure = reportFailure
            ?? throw new ArgumentNullException(nameof(reportFailure));
        worker = RunAsync();
    }

    public void Enqueue(UiPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        TaskCompletionSource signal;
        lock (sync)
        {
            // A terminal worker failure must not be reported as ordinary disposal.
            if (workerFailure is { } failure)
            {
                throw new InvalidOperationException(
                    "The preference save worker failed; preferences are no longer being persisted.",
                    failure);
            }

            ObjectDisposedException.ThrowIf(!accepting, this);
            commits.Schedule(preferences);
            signal = wake;
        }

        signal.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        Task drain;
        lock (sync)
        {
            accepting = false;
            wake.TrySetResult();
            drain = disposal ??= DrainAsync();
        }

        return new ValueTask(drain);
    }

    private async Task DrainAsync()
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        finally
        {
            commits.Dispose();
        }

        Exception? failure;
        lock (sync)
        {
            failure = workerFailure;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                Task wakeTask;
                lock (sync)
                {
                    // The exit check and wake replacement must be atomic to prevent a missed signal.
                    if (!accepting && !commits.TryPeekPending(out _))
                    {
                        return;
                    }

                    wake = NewWake();
                    wakeTask = wake.Task;
                }

                if (await SaveAllPendingAsync().ConfigureAwait(false))
                {
                    continue;
                }

                await wakeTask.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                workerFailure = exception;
                accepting = false;
            }

            Report(exception);
        }
    }

    private async Task<bool> SaveAllPendingAsync()
    {
        // InProgress is unreachable with one worker; retrying it here would spin.
        if (commits.TryBegin(out UiPreferences? preferences)
            != CoalescingCommitBeginStatus.Started)
        {
            return false;
        }

        while (true)
        {
            bool morePending;
            try
            {
                await saveAsync(preferences!, CancellationToken.None).ConfigureAwait(false);
            }
            catch (UiPreferenceSaveException exception)
            {
                Report(exception);
            }
            finally
            {
                morePending = commits.Complete();
            }

            if (!morePending
                || commits.TryBegin(out preferences) != CoalescingCommitBeginStatus.Started)
            {
                return true;
            }
        }
    }

    private void Report(Exception exception)
    {
        try
        {
            reportFailure(exception);
        }
        catch
        {
        }
    }

    private static TaskCompletionSource NewWake() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
