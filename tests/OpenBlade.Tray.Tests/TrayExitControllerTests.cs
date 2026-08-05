namespace OpenBlade.Tray.Tests;

public sealed class TrayExitControllerTests
{
    [Theory]
    [InlineData(TrayExitChoice.Cancel, 0, 0)]
    [InlineData(TrayExitChoice.KeepBackgroundRunning, 1, 0)]
    [InlineData(TrayExitChoice.ShutDownCompletely, 0, 1)]
    public async Task ChoiceRoutesToOnlyTheSelectedAction(
        TrayExitChoice choice,
        int expectedKeepRunningCalls,
        int expectedShutdownCalls)
    {
        int keepRunningCalls = 0;
        int shutdownCalls = 0;
        var controller = new TrayExitController(
            () => choice,
            () =>
            {
                keepRunningCalls++;
                return Task.CompletedTask;
            },
            () =>
            {
                shutdownCalls++;
                return Task.CompletedTask;
            });

        await controller.RequestExitAsync();

        Assert.Equal(expectedKeepRunningCalls, keepRunningCalls);
        Assert.Equal(expectedShutdownCalls, shutdownCalls);
    }

    [Fact]
    public async Task ReentrantRequestIsIgnoredWhileChoiceDialogIsActive()
    {
        int choiceRequests = 0;
        TrayExitController? controller = null;
        controller = new TrayExitController(
            () =>
            {
                choiceRequests++;
                controller!.RequestExitAsync().GetAwaiter().GetResult();
                return TrayExitChoice.Cancel;
            },
            () => Task.CompletedTask,
            () => Task.CompletedTask);

        await controller.RequestExitAsync();

        Assert.Equal(1, choiceRequests);
    }

    [Fact]
    public async Task FailureReleasesGuardForASecondRequest()
    {
        int choiceRequests = 0;
        var controller = new TrayExitController(
            () => ++choiceRequests == 1
                ? throw new InvalidOperationException("dialog")
                : TrayExitChoice.Cancel,
            () => Task.CompletedTask,
            () => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(controller.RequestExitAsync);
        await controller.RequestExitAsync();

        Assert.Equal(2, choiceRequests);
    }
}
