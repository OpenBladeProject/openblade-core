namespace OpenBlade.Tray;

/// <summary>
/// Serializes function-row edges without allowing queue pressure or a status
/// reset to strand a native key in its pressed state. Repeat edges are the only
/// disposable work; releases and resets are always accepted.
/// </summary>
public sealed class FunctionRowInputPump : IAsyncDisposable
{
    public const int DefaultCapacity = 16;

    private readonly Func<FunctionRowInputEvent, CancellationToken, Task> executeAsync;
    private readonly Func<CancellationToken, Task> resetAsync;
    private readonly Action<Exception>? errorHandler;
    private readonly int capacity;
    private readonly Queue<PendingInput> pending = [];
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly Task worker;
    private bool accepting = true;
    private bool stopping;
    private bool resetPending;
    private int disposed;

    public FunctionRowInputPump(
        Func<FunctionRowInputEvent, CancellationToken, Task> executeAsync,
        Func<CancellationToken, Task> resetAsync,
        int capacity = DefaultCapacity,
        Action<Exception>? errorHandler = null)
    {
        this.executeAsync = executeAsync
            ?? throw new ArgumentNullException(nameof(executeAsync));
        this.resetAsync = resetAsync ?? throw new ArgumentNullException(nameof(resetAsync));
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        this.errorHandler = errorHandler;
        worker = RunAsync(lifetime.Token);
    }

    public bool TryEnqueue(FunctionRowInputEvent inputEvent)
    {
        if (!Enum.IsDefined(inputEvent.Action)
            || !Enum.IsDefined(inputEvent.Phase))
        {
            return false;
        }

        lock (sync)
        {
            if (!accepting)
            {
                return false;
            }

            if (inputEvent.Phase == FunctionRowInputPhase.Repeat
                && pending.Count >= capacity)
            {
                // Windows auto-repeat may outrun WMI or service IPC. Dropping a
                // repeat is preferable to delaying the matching release.
                return true;
            }
            if (pending.Count >= capacity)
            {
                if (inputEvent.Phase == FunctionRowInputPhase.Down)
                {
                    return false;
                }

                if (!RemoveFirstPending(candidate =>
                        candidate.Phase == FunctionRowInputPhase.Repeat
                        && candidate.ScanCode == inputEvent.ScanCode)
                    && !RemoveFirstPending(candidate =>
                        candidate.Phase == FunctionRowInputPhase.Repeat))
                {
                    bool removedUnexecutedPress = RemovePendingForScanCode(
                        inputEvent.ScanCode);
                    if (removedUnexecutedPress)
                    {
                        return true;
                    }

                    // The matching press is already in flight. Collapse the
                    // saturated queue into a single global reset rather than
                    // exceed the bound or risk losing the release.
                    pending.Clear();
                    bool wakeReset = !resetPending;
                    resetPending = true;
                    if (wakeReset)
                    {
                        SignalWorker();
                    }
                    return true;
                }
            }

            bool wakeWorker = pending.Count == 0 && !resetPending;
            pending.Enqueue(new(inputEvent));
            if (wakeWorker)
            {
                SignalWorker();
            }
            return true;
        }
    }

    public void Reset()
    {
        lock (sync)
        {
            if (!accepting)
            {
                return;
            }

            pending.Clear();
            bool wakeWorker = !resetPending;
            resetPending = true;
            if (wakeWorker)
            {
                SignalWorker();
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
            stopping = true;
            pending.Clear();
            resetPending = true;
            SignalWorker();
        }

        await worker.ConfigureAwait(false);
        lifetime.Cancel();
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
                PendingInput input = default;
                bool reset;
                bool stop;
                lock (sync)
                {
                    reset = resetPending;
                    if (reset)
                    {
                        resetPending = false;
                    }
                    else if (pending.Count != 0)
                    {
                        input = pending.Dequeue();
                    }

                    stop = stopping && !reset && pending.Count == 0;
                    if (!reset && input.Event is null)
                    {
                        if (stop)
                        {
                            return;
                        }
                        break;
                    }
                }

                try
                {
                    if (reset)
                    {
                        await resetAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await executeAsync(input.Event!.Value, cancellationToken)
                            .ConfigureAwait(false);
                    }
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
        }
    }

    private bool RemoveFirstPending(Func<FunctionRowInputEvent, bool> predicate)
    {
        bool removed = false;
        int count = pending.Count;
        for (int index = 0; index < count; index++)
        {
            PendingInput candidate = pending.Dequeue();
            if (!removed
                && candidate.Event is { } inputEvent
                && predicate(inputEvent))
            {
                removed = true;
                continue;
            }

            pending.Enqueue(candidate);
        }
        return removed;
    }

    private bool RemovePendingForScanCode(uint scanCode)
    {
        bool removedDown = false;
        int count = pending.Count;
        for (int index = 0; index < count; index++)
        {
            PendingInput candidate = pending.Dequeue();
            if (candidate.Event is { } inputEvent
                && inputEvent.ScanCode == scanCode)
            {
                removedDown |= inputEvent.Phase == FunctionRowInputPhase.Down;
                continue;
            }

            pending.Enqueue(candidate);
        }
        return removedDown;
    }

    private readonly record struct PendingInput(FunctionRowInputEvent? Event);
}
