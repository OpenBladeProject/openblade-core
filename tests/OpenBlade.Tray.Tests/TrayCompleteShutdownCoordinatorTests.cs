namespace OpenBlade.Tray.Tests;

public sealed class TrayCompleteShutdownCoordinatorTests
{
    [Theory]
    [InlineData((int)ElevatedServiceControlOutcome.Completed, false)]
    [InlineData((int)ElevatedServiceControlOutcome.Completed, true)]
    [InlineData((int)ElevatedServiceControlOutcome.Indeterminate, false)]
    [InlineData((int)ElevatedServiceControlOutcome.Indeterminate, true)]
    public async Task ShutdownRequestRequiresStoppedReadback(
        int outcomeValue,
        bool serviceStopped)
    {
        RecordingShutdownSteps steps = CreateSteps(
            (ElevatedServiceControlOutcome)outcomeValue,
            serviceStopped);

        TrayCompleteShutdownResult result = await steps.Coordinator.ShutdownAsync(
            CancellationToken.None);

        Assert.Equal(
            serviceStopped
                ? TrayCompleteShutdownResult.Completed
                : TrayCompleteShutdownResult.Unconfirmed,
            result);
        Assert.Equal(1, steps.ConfirmationCalls);
        Assert.Equal(serviceStopped ? 1 : 0, steps.StopCalls);
        Assert.Equal(serviceStopped ? 1 : 0, steps.ExitCalls);
        Assert.Equal(
            serviceStopped
                ? new[] { "request", "confirm", "stop", "exit" }
                : new[] { "request", "confirm" },
            steps.CallOrder);
    }

    [Fact]
    public async Task CanceledRequestLeavesAllProcessesRunning()
    {
        RecordingShutdownSteps steps = CreateSteps(ElevatedServiceControlOutcome.Canceled);

        TrayCompleteShutdownResult result = await steps.Coordinator.ShutdownAsync(
            CancellationToken.None);

        Assert.Equal(TrayCompleteShutdownResult.Canceled, result);
        Assert.Equal(0, steps.ConfirmationCalls);
        Assert.Equal(0, steps.StopCalls);
        Assert.Equal(0, steps.ExitCalls);
    }

    [Fact]
    public async Task CompletedRequestWaitsForConfirmationBeforeStoppingComponents()
    {
        var confirmation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingShutdownSteps steps = CreateSteps(
            ElevatedServiceControlOutcome.Completed,
            confirmStoppedAsync: _ => confirmation.Task);

        Task<TrayCompleteShutdownResult> shutdown = steps.Coordinator.ShutdownAsync(
            CancellationToken.None);

        Assert.Equal(1, steps.ConfirmationCalls);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, steps.StopCalls);
        Assert.Equal(0, steps.ExitCalls);

        confirmation.SetResult(true);

        Assert.Equal(TrayCompleteShutdownResult.Completed, await shutdown);
        Assert.Equal(new[] { "request", "confirm", "stop", "exit" }, steps.CallOrder);
    }

    [Fact]
    public async Task ConfirmationExceptionLeavesComponentsRunning()
    {
        var exception = new InvalidOperationException("Service state query failed.");
        RecordingShutdownSteps steps = CreateSteps(
            ElevatedServiceControlOutcome.Completed,
            confirmStoppedAsync: _ => Task.FromException<bool>(exception));

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => steps.Coordinator.ShutdownAsync(CancellationToken.None));

        Assert.Same(exception, actual);
        Assert.Equal(1, steps.ConfirmationCalls);
        Assert.Equal(0, steps.StopCalls);
        Assert.Equal(0, steps.ExitCalls);
    }

    [Fact]
    public async Task CancellationDuringSuccessfulConfirmationLeavesComponentsRunning()
    {
        using var cancellation = new CancellationTokenSource();
        RecordingShutdownSteps steps = CreateSteps(
            ElevatedServiceControlOutcome.Completed,
            confirmStoppedAsync: _ =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => steps.Coordinator.ShutdownAsync(cancellation.Token));

        Assert.Equal(1, steps.ConfirmationCalls);
        Assert.Equal(0, steps.StopCalls);
        Assert.Equal(0, steps.ExitCalls);
    }

    private static RecordingShutdownSteps CreateSteps(
        ElevatedServiceControlOutcome outcome,
        bool serviceStopped = false,
        Func<CancellationToken, Task<bool>>? confirmStoppedAsync = null)
    {
        var steps = new RecordingShutdownSteps();
        steps.Coordinator = new TrayCompleteShutdownCoordinator(
            token =>
            {
                token.ThrowIfCancellationRequested();
                steps.CallOrder.Add("request");
                return Task.FromResult(outcome);
            },
            token =>
            {
                token.ThrowIfCancellationRequested();
                steps.ConfirmationCalls++;
                steps.CallOrder.Add("confirm");
                return confirmStoppedAsync?.Invoke(token) ?? Task.FromResult(serviceStopped);
            },
            () =>
            {
                steps.CallOrder.Add("stop");
                steps.StopCalls++;
            },
            () =>
            {
                steps.CallOrder.Add("exit");
                steps.ExitCalls++;
                return Task.CompletedTask;
            });
        return steps;
    }

    private sealed class RecordingShutdownSteps
    {
        public TrayCompleteShutdownCoordinator Coordinator { get; set; } = null!;
        public List<string> CallOrder { get; } = [];
        public int ConfirmationCalls { get; set; }
        public int StopCalls { get; set; }
        public int ExitCalls { get; set; }
    }
}
