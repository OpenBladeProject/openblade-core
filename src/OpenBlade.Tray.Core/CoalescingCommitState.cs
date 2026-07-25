namespace OpenBlade.Tray;

public enum CoalescingCommitBeginStatus
{
    NoWork,
    InProgress,
    Started,
}

/// <summary>
/// Owns the state for a debounced UI commit. Values scheduled before a commit
/// starts collapse to the latest value. Values scheduled while a commit is in
/// progress become one follow-up commit.
/// </summary>
public sealed class CoalescingCommitState<T> : IDisposable
{
    private readonly object sync = new();
    private T? pending;
    private bool hasPending;
    private bool inProgress;
    private bool disposed;

    public bool Schedule(T value)
    {
        lock (sync)
        {
            if (disposed)
            {
                return false;
            }

            pending = value;
            hasPending = true;
            return true;
        }
    }

    public bool TryPeekPending(out T? value)
    {
        lock (sync)
        {
            value = pending;
            return !disposed && hasPending;
        }
    }

    public void ClearPending()
    {
        lock (sync)
        {
            pending = default;
            hasPending = false;
        }
    }

    public CoalescingCommitBeginStatus TryBegin(out T? value)
    {
        lock (sync)
        {
            value = default;
            if (disposed || !hasPending)
            {
                return CoalescingCommitBeginStatus.NoWork;
            }
            if (inProgress)
            {
                return CoalescingCommitBeginStatus.InProgress;
            }

            value = pending;
            pending = default;
            hasPending = false;
            inProgress = true;
            return CoalescingCommitBeginStatus.Started;
        }
    }

    public bool Complete()
    {
        lock (sync)
        {
            if (!inProgress)
            {
                throw new InvalidOperationException(
                    "A deferred commit can only complete after it starts.");
            }

            inProgress = false;
            return !disposed && hasPending;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            pending = default;
            hasPending = false;
        }
    }
}
