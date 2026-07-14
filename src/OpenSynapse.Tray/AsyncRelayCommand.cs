using System.Windows.Input;

namespace OpenSynapse.Tray;

public sealed class AsyncRelayCommand : ICommand, IAsyncDisposable
{
    private readonly Func<object?, CancellationToken, Task> execute;
    private readonly Predicate<object?> canExecute;
    private readonly CancellationToken lifetimeToken;
    private CancellationTokenSource? executionCancellation;
    private Task currentExecution = Task.CompletedTask;
    private int disposed;
    private int running;

    public AsyncRelayCommand(
        Func<object?, CancellationToken, Task> execute,
        Predicate<object?> canExecute,
        CancellationToken lifetimeToken)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        this.lifetimeToken = lifetimeToken;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => Volatile.Read(ref running) != 0;

    public bool CanExecute(object? parameter) =>
        Volatile.Read(ref disposed) == 0
        && !IsRunning
        && canExecute(parameter);

    public void Execute(object? parameter) => _ = ExecuteAsync(parameter);

    public Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter)
            || Interlocked.CompareExchange(ref running, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        Volatile.Write(ref executionCancellation, cancellation);
        NotifyCanExecuteChanged();
        Task task = RunAsync(parameter, cancellation);
        Volatile.Write(ref currentExecution, task);
        return task;
    }

    public void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Volatile.Read(ref executionCancellation)?.Cancel();
        try
        {
            await Volatile.Read(ref currentExecution);
        }
        catch (OperationCanceledException)
        {
        }
        NotifyCanExecuteChanged();
    }

    private async Task RunAsync(
        object? parameter,
        CancellationTokenSource cancellation)
    {
        try
        {
            await execute(parameter, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref executionCancellation, null, cancellation);
            cancellation.Dispose();
            Interlocked.Exchange(ref running, 0);
            NotifyCanExecuteChanged();
        }
    }
}
