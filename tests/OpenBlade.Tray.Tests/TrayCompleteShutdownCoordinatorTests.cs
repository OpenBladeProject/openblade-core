namespace OpenBlade.Tray.Tests;

public sealed class TrayCompleteShutdownCoordinatorTests
{
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

    [Theory]
    [InlineData((int)ElevatedServiceControlOutcome.Completed, false, (int)TrayCompleteShutdownResult.Unconfirmed, 0, 0)]
    [InlineData((int)ElevatedServiceControlOutcome.Indeterminate, false, (int)TrayCompleteShutdownResult.Unconfirmed, 0, 0)]
    [InlineData((int)ElevatedServiceControlOutcome.Completed, true, (int)TrayCompleteShutdownResult.Completed, 1, 1)]
    [InlineData((int)ElevatedServiceControlOutcome.Indeterminate, true, (int)TrayCompleteShutdownResult.Completed, 1, 1)]
    public async Task NonCanceledRequestRequiresStoppedReadback(
        int outcomeValue,
        bool serviceStopped,
        int expectedValue,
        int expectedStopCalls,
        int expectedExitCalls)
    {
        var expected = (TrayCompleteShutdownResult)expectedValue;
        RecordingShutdownSteps steps = CreateSteps(
            (ElevatedServiceControlOutcome)outcomeValue,
            serviceStopped);

        TrayCompleteShutdownResult result = await steps.Coordinator.ShutdownAsync(
            CancellationToken.None);

        Assert.Equal(expected, result);
        Assert.Equal(1, steps.ConfirmationCalls);
        Assert.Equal(expectedStopCalls, steps.StopCalls);
        Assert.Equal(expectedExitCalls, steps.ExitCalls);
    }

    [Fact]
    public async Task CompletedRequestWaitsForConfirmationBeforeOrderedTeardown()
    {
        var confirmation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var coordinator = new TrayCompleteShutdownCoordinator(
            _ =>
            {
                events.Add("request");
                return Task.FromResult(ElevatedServiceControlOutcome.Completed);
            },
            _ =>
            {
                events.Add("confirm");
                return confirmation.Task;
            },
            () => events.Add("stop-session-agent"),
            () =>
            {
                events.Add("exit-tray");
                return Task.CompletedTask;
            });

        Task<TrayCompleteShutdownResult> shutdown = coordinator.ShutdownAsync(
            CancellationToken.None);

        Assert.False(shutdown.IsCompleted);
        Assert.Equal(new[] { "request", "confirm" }, events);

        confirmation.SetResult(true);
        Assert.Equal(TrayCompleteShutdownResult.Completed, await shutdown);
        Assert.Equal(
            new[] { "request", "confirm", "stop-session-agent", "exit-tray" },
            events);
    }

    private static RecordingShutdownSteps CreateSteps(
        ElevatedServiceControlOutcome outcome,
        bool serviceStopped = false)
    {
        var steps = new RecordingShutdownSteps();
        steps.Coordinator = new TrayCompleteShutdownCoordinator(
            token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(outcome);
            },
            token =>
            {
                token.ThrowIfCancellationRequested();
                steps.ConfirmationCalls++;
                return Task.FromResult(serviceStopped);
            },
            () => steps.StopCalls++,
            () =>
            {
                steps.ExitCalls++;
                return Task.CompletedTask;
            });
        return steps;
    }

    private sealed class RecordingShutdownSteps
    {
        public TrayCompleteShutdownCoordinator Coordinator { get; set; } = null!;
        public int ConfirmationCalls { get; set; }
        public int StopCalls { get; set; }
        public int ExitCalls { get; set; }
    }
}
