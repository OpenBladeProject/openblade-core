namespace OpenBlade.Tray;

public interface IUiDispatcher
{
    Task InvokeAsync(Action action, CancellationToken token);
}
