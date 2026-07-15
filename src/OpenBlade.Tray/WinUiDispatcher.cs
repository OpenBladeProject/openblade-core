using Microsoft.UI.Dispatching;

namespace OpenBlade.Tray;

public sealed class WinUiDispatcher(DispatcherQueue dispatcherQueue) : IUiDispatcher
{
    private readonly DispatcherQueue dispatcherQueue = dispatcherQueue
        ?? throw new ArgumentNullException(nameof(dispatcherQueue));

    public Task InvokeAsync(Action action, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(action);
        token.ThrowIfCancellationRequested();

        if (dispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested)
                {
                    completion.TrySetCanceled(token);
                    return;
                }

                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetCanceled(token.IsCancellationRequested
                ? token
                : new CancellationToken(canceled: true));
        }

        return completion.Task;
    }
}
