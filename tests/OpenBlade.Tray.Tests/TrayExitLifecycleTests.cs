namespace OpenBlade.Tray.Tests;

public sealed class TrayExitLifecycleTests
{
    [Fact]
    public async Task ExitStartsSynchronouslyRejectsActivationAndIsIdempotent()
    {
        var disposeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDispose = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int exits = 0;
        int activations = 0;
        var lifecycle = new TrayExitLifecycle(
            CreateSteps(
                disposeViewModelAsync: async () =>
                {
                    disposeStarted.SetResult();
                    await allowDispose.Task;
                },
                exitApplication: () => exits++),
            _ => { });

        Assert.True(lifecycle.TryRunWhileActive(() => activations++));

        Task firstExit = lifecycle.ExitAsync();
        await disposeStarted.Task;

        Assert.True(lifecycle.ExitRequested);
        Assert.False(lifecycle.TryRunWhileActive(() => activations++));
        Assert.Same(firstExit, lifecycle.ExitAsync());

        allowDispose.SetResult();
        await firstExit;

        Assert.Equal(1, activations);
        Assert.Equal(1, exits);
    }

    [Fact]
    public async Task CleanupFaultsAreReportedAndDoNotSkipLaterStepsOrEscape()
    {
        var attempted = new List<string>();
        var reported = new List<Exception>();
        var lifecycle = new TrayExitLifecycle(
            new TrayExitSteps(
                UnregisterShutdown: () => Throw("registration"),
                DisposeShutdownEvent: () => Throw("event"),
                DisposeTrayIcon: () => Throw("tray"),
                DisposeViewModelAsync: () => ThrowAsync("viewmodel"),
                CloseWindow: () => Throw("window"),
                DisposeInstanceCoordinatorAsync: () => ThrowAsync("coordinator"),
                ExitApplication: () => Throw("application")),
            reported.Add);

        await lifecycle.ExitAsync();

        Assert.Equal(
            ["registration", "event", "tray", "viewmodel", "window", "coordinator", "application"],
            attempted);
        Assert.Equal(attempted, reported.Select(exception => exception.Message));

        void Throw(string step)
        {
            attempted.Add(step);
            throw new InvalidOperationException(step);
        }

        ValueTask ThrowAsync(string step)
        {
            attempted.Add(step);
            return ValueTask.FromException(new InvalidOperationException(step));
        }
    }

    [Fact]
    public async Task ReporterFaultDoesNotPreventRemainingCleanupOrApplicationExit()
    {
        var attempted = new List<string>();
        var lifecycle = new TrayExitLifecycle(
            CreateSteps(
                unregisterShutdown: () =>
                {
                    attempted.Add("registration");
                    throw new InvalidOperationException("cleanup");
                },
                disposeShutdownEvent: () => attempted.Add("event"),
                exitApplication: () => attempted.Add("application")),
            _ => throw new InvalidOperationException("reporter"));

        await lifecycle.ExitAsync();

        Assert.Equal(["registration", "event", "application"], attempted);
    }

    private static TrayExitSteps CreateSteps(
        Action? unregisterShutdown = null,
        Action? disposeShutdownEvent = null,
        Action? disposeTrayIcon = null,
        Func<ValueTask>? disposeViewModelAsync = null,
        Action? closeWindow = null,
        Func<ValueTask>? disposeInstanceCoordinatorAsync = null,
        Action? exitApplication = null) =>
        new(
            unregisterShutdown ?? (() => { }),
            disposeShutdownEvent ?? (() => { }),
            disposeTrayIcon ?? (() => { }),
            disposeViewModelAsync ?? (() => ValueTask.CompletedTask),
            closeWindow ?? (() => { }),
            disposeInstanceCoordinatorAsync ?? (() => ValueTask.CompletedTask),
            exitApplication ?? (() => { }));
}
