namespace OpenBlade.Protocol;

internal sealed class PendingDisposalRegistry
{
    internal static PendingDisposalRegistry Shared { get; } = new();

    private readonly object sync = new();
    private readonly HashSet<Task> pending = [];
    private TaskCompletionSource empty = CompletedSignal();

    internal int Count
    {
        get
        {
            lock (sync)
            {
                return pending.Count;
            }
        }
    }

    internal void Retain(Task disposal)
    {
        ArgumentNullException.ThrowIfNull(disposal);
        lock (sync)
        {
            if (!pending.Add(disposal))
            {
                return;
            }

            if (pending.Count == 1)
            {
                empty = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        _ = disposal.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    _ = completed.Exception;
                }

                lock (sync)
                {
                    pending.Remove(completed);
                    if (pending.Count == 0)
                    {
                        empty.TrySetResult();
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal Task WhenEmptyAsync()
    {
        lock (sync)
        {
            return empty.Task;
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}
