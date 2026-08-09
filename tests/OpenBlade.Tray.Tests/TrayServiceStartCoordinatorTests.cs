namespace OpenBlade.Tray.Tests;

public sealed class TrayServiceStartCoordinatorTests
{
    [Fact]
    public async Task CompletedRequestDoesNotRequireReadback()
    {
        int confirmations = 0;
        var coordinator = new TrayServiceStartCoordinator(
            _ => Task.FromResult(ElevatedServiceControlOutcome.Completed),
            _ =>
            {
                confirmations++;
                return Task.FromResult(true);
            });

        TrayServiceStartResult result = await coordinator.StartAsync(
            CancellationToken.None);

        Assert.Equal(TrayServiceStartResult.Completed, result);
        Assert.Equal(0, confirmations);
    }

    [Fact]
    public async Task CanceledRequestLeavesTrayRunningWithoutReadback()
    {
        int confirmations = 0;
        var coordinator = new TrayServiceStartCoordinator(
            _ => Task.FromResult(ElevatedServiceControlOutcome.Canceled),
            _ =>
            {
                confirmations++;
                return Task.FromResult(true);
            });

        TrayServiceStartResult result = await coordinator.StartAsync(
            CancellationToken.None);

        Assert.Equal(TrayServiceStartResult.Canceled, result);
        Assert.Equal(0, confirmations);
    }

    [Theory]
    [InlineData(false, (int)TrayServiceStartResult.Unconfirmed)]
    [InlineData(true, (int)TrayServiceStartResult.Completed)]
    public async Task IndeterminateRequestRequiresRunningReadback(
        bool serviceRunning,
        int expectedValue)
    {
        int requests = 0;
        int confirmations = 0;
        var coordinator = new TrayServiceStartCoordinator(
            _ =>
            {
                requests++;
                return Task.FromResult(ElevatedServiceControlOutcome.Indeterminate);
            },
            _ =>
            {
                confirmations++;
                return Task.FromResult(serviceRunning);
            });

        TrayServiceStartResult result = await coordinator.StartAsync(
            CancellationToken.None);

        Assert.Equal((TrayServiceStartResult)expectedValue, result);
        Assert.Equal(1, requests);
        Assert.Equal(1, confirmations);
    }
}
