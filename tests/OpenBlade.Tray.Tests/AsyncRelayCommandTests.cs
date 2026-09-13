using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public void ExecuteAsync_RetainsTheCallerSynchronizationContext()
    {
        PumpingSynchronizationContext.Run(async context =>
        {
            var executionStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseExecution = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var observedContexts = new List<SynchronizationContext?>();
            var command = new AsyncRelayCommand(
                async (_, _) =>
                {
                    observedContexts.Add(SynchronizationContext.Current);
                    executionStarted.TrySetResult();
                    await releaseExecution.Task;
                    observedContexts.Add(SynchronizationContext.Current);
                },
                _ => true,
                CancellationToken.None);
            command.CanExecuteChanged += (_, _) =>
                observedContexts.Add(SynchronizationContext.Current);

            Task running = command.ExecuteAsync(null);
            await executionStarted.Task;
            releaseExecution.TrySetResult();
            await running;
            await command.DisposeAsync();

            Assert.NotEmpty(observedContexts);
            Assert.All(observedContexts, observed => Assert.Same(context, observed));
        });
    }

    [Fact]
    public async Task DisposeAsync_SerializesWithStartupAndAwaitsCanceledExecution()
    {
        var canExecuteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCanExecute = new ManualResetEventSlim();
        var executionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(
            async (_, token) =>
            {
                executionStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    executionCanceled.TrySetResult();
                    throw;
                }
            },
            _ =>
            {
                canExecuteEntered.TrySetResult();
                releaseCanExecute.Wait();
                return true;
            },
            CancellationToken.None);

        Task<Task> start = Task.Factory.StartNew(
            () => command.ExecuteAsync(null),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        await canExecuteEntered.Task;
        Task disposal = Task.Run(async () => await command.DisposeAsync());

        Assert.False(disposal.IsCompleted);
        releaseCanExecute.Set();
        Task execution = await start;
        await disposal;
        await execution;

        Assert.True(
            !executionStarted.Task.IsCompleted || executionCanceled.Task.IsCompleted,
            "An execution that starts during disposal must observe cancellation before disposal returns.");
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task ExecuteAsync_AfterDisposalNeverStartsExecution()
    {
        int executions = 0;
        var command = new AsyncRelayCommand(
            (_, _) =>
            {
                Interlocked.Increment(ref executions);
                return Task.CompletedTask;
            },
            _ => true,
            CancellationToken.None);
        await command.DisposeAsync();

        await command.ExecuteAsync(null);

        Assert.Equal(0, executions);
    }
}
