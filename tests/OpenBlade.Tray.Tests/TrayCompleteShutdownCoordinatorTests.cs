namespace OpenBlade.Tray.Tests;

public sealed class TrayCompleteShutdownCoordinatorTests
{
    [Fact]
    public async Task CompletedRequestStopsSessionAgentAndExitsWithoutReadback()
    {
        RecordingShutdownSteps steps = CreateSteps(ElevatedServiceControlOutcome.Completed);

        TrayCompleteShutdownResult result = await steps.Coordinator.ShutdownAsync(
            CancellationToken.None);

        Assert.Equal(TrayCompleteShutdownResult.Completed, result);
        Assert.Equal(0, steps.ConfirmationCalls);
        Assert.Equal(1, steps.StopCalls);
        Assert.Equal(1, steps.ExitCalls);
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

    [Theory]
    [InlineData(false, (int)TrayCompleteShutdownResult.Unconfirmed, 0, 0)]
    [InlineData(true, (int)TrayCompleteShutdownResult.Completed, 1, 1)]
    public async Task IndeterminateRequestRequiresStoppedReadback(
        bool serviceStopped,
        int expectedValue,
        int expectedStopCalls,
        int expectedExitCalls)
    {
        var expected = (TrayCompleteShutdownResult)expectedValue;
        RecordingShutdownSteps steps = CreateSteps(
            ElevatedServiceControlOutcome.Indeterminate,
            serviceStopped);

        TrayCompleteShutdownResult result = await steps.Coordinator.ShutdownAsync(
            CancellationToken.None);

        Assert.Equal(expected, result);
        Assert.Equal(1, steps.ConfirmationCalls);
        Assert.Equal(expectedStopCalls, steps.StopCalls);
        Assert.Equal(expectedExitCalls, steps.ExitCalls);
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
