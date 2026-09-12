using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace OpenBlade.Tray.Tests;

internal sealed class PumpingSynchronizationContext : SynchronizationContext, IDisposable
{
    private static readonly TimeSpan WorkTimeout = TimeSpan.FromSeconds(5);
    private readonly object lifecycleGate = new();
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> work = new();
    private readonly AutoResetEvent workAvailable = new(initialState: false);
    private readonly int ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly TimeSpan workTimeout;
    private bool disposed;

    internal PumpingSynchronizationContext(TimeSpan? workTimeout = null)
    {
        this.workTimeout = workTimeout ?? WorkTimeout;
    }

    public override void Post(SendOrPostCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (lifecycleGate)
        {
            if (disposed)
            {
                return;
            }

            work.Enqueue((callback, state));
            workAvailable.Set();
        }
    }

    public override void Send(SendOrPostCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (Environment.CurrentManagedThreadId == ownerThreadId)
        {
            callback(state);
            return;
        }

        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ExceptionDispatchInfo? failure = null;
        Post(
            queuedState =>
            {
                try
                {
                    callback(queuedState);
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    completed.TrySetResult();
                }
            },
            state);
        Assert.True(
            completed.Task.Wait(workTimeout),
            "Timed out waiting for synchronous context work to complete.");
        failure?.Throw();
    }

    internal static void Run(
        Func<PumpingSynchronizationContext, Task> action,
        TimeSpan? workTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        SynchronizationContext? previous = Current;
        using var context = new PumpingSynchronizationContext(workTimeout);
        try
        {
            SetSynchronizationContext(context);
            Task task = action(context);
            Task completionSignal = task.ContinueWith(
                _ => context.SignalWork(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            while (!task.IsCompleted)
            {
                if (!context.TryRunOne())
                {
                    Assert.True(
                        context.workAvailable.WaitOne(context.workTimeout),
                        "Timed out waiting for work posted to the synchronization context.");
                }
            }

            while (context.TryRunOne())
            {
            }
            completionSignal.GetAwaiter().GetResult();
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            workAvailable.Dispose();
        }
    }

    internal void SignalWork()
    {
        lock (lifecycleGate)
        {
            if (!disposed)
            {
                workAvailable.Set();
            }
        }
    }

    internal bool TryRunOne()
    {
        if (!work.TryDequeue(out var item))
        {
            return false;
        }

        item.Callback(item.State);
        return true;
    }
}
