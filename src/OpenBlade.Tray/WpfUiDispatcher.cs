using System.Windows.Threading;

namespace OpenBlade.Tray;

internal sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    private readonly Dispatcher dispatcher = dispatcher
        ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task InvokeAsync(Action action, CancellationToken token)
    {
        if (dispatcher.CheckAccess())
        {
            token.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(
            action,
            DispatcherPriority.DataBind,
            token).Task;
    }
}
