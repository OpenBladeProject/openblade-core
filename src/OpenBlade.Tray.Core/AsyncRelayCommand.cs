using System.Windows.Input;

namespace OpenBlade.Tray;

public sealed class AsyncRelayCommand : ICommand, IAsyncDisposable
{
    private readonly Func<object?, CancellationToken, Task> execute;
    private readonly Predicate<object?> canExecute;
    private readonly CancellationToken lifetimeToken;
    private readonly Func<object?, object?>? captureParameter;
    private readonly object sync = new();
    private CancellationTokenSource? executionCancellation;
    private Task currentExecution = Task.CompletedTask;
    private bool disposed;
    private bool running;

    public AsyncRelayCommand(
        Func<object?, CancellationToken, Task> execute,
        Predicate<object?> canExecute,
        CancellationToken lifetimeToken,
        Func<object?, object?>? captureParameter = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        this.lifetimeToken = lifetimeToken;
        this.captureParameter = captureParameter;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning
    {
        get
        {
            lock (sync)
            {
                return running;
            }
        }
    }

    public bool CanExecute(object? parameter)
    {
        lock (sync)
        {
            return !disposed && !running && !lifetimeToken.IsCancellationRequested && canExecute(parameter);
        }
    }

    public void Execute(object? parameter) => _ = ExecuteAsync(parameter);

    public Task ExecuteAsync(object? parameter)
    {
        Task task;
        lock (sync)
        {
            if (disposed || running || lifetimeToken.IsCancellationRequested || !canExecute(parameter))
            {
                return Task.CompletedTask;
            }

            object? capturedParameter = captureParameter is null ? parameter : captureParameter(parameter);
            running = true;
            executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeToken);
            task = RunAsync(capturedParameter, executionCancellation);
            currentExecution = task;
        }

        NotifyCanExecuteChanged();
        return task;
    }

    public void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellation;
        Task activeExecution;
        lock (sync)
        {
            disposed = true;
            cancellation = executionCancellation;
            activeExecution = currentExecution;
        }

        cancellation?.Cancel();
        try
        {
            await activeExecution;
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
            await Task.Yield();
            cancellation.Token.ThrowIfCancellationRequested();
            await execute(parameter, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(executionCancellation, cancellation))
                {
                    executionCancellation = null;
                }
                running = false;
            }
            cancellation.Dispose();
            NotifyCanExecuteChanged();
        }
    }
}
