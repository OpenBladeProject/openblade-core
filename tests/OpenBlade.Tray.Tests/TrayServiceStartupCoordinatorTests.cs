namespace OpenBlade.Tray.Tests;

public sealed class TrayServiceStartupCoordinatorTests
{
    [Theory]
    [InlineData((int)ObservedServiceState.Stopped, 1)]
    [InlineData((int)ObservedServiceState.Running, 0)]
    [InlineData((int)ObservedServiceState.StartPending, 0)]
    [InlineData((int)ObservedServiceState.ContinuePending, 0)]
    [InlineData((int)ObservedServiceState.StopPending, 0)]
    [InlineData((int)ObservedServiceState.Other, 0)]
    [InlineData((int)ObservedServiceState.Unavailable, 0)]
    public async Task StartupAttemptsOnceOnlyForConfirmedStopped(int state, int expectedStarts)
    {
        int reads = 0;
        int starts = 0;
        var coordinator = new TrayServiceStartupCoordinator(
            _ =>
            {
                reads++;
                return Task.FromResult((ObservedServiceState)state);
            },
            _ =>
            {
                starts++;
                return Task.FromResult(StartOpenBladeResult.Started);
            });

        await coordinator.TryStartOnceAsync(CancellationToken.None);
        await coordinator.TryStartOnceAsync(CancellationToken.None);

        Assert.Equal(1, reads);
        Assert.Equal(expectedStarts, starts);
    }

    [Fact]
    public async Task ConcurrentStartupCallsReadAndRequestOnlyOnce()
    {
        var observed = new TaskCompletionSource<ObservedServiceState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        int starts = 0;
        var coordinator = new TrayServiceStartupCoordinator(
            _ =>
            {
                reads++;
                return observed.Task;
            },
            _ =>
            {
                starts++;
                return Task.FromResult(StartOpenBladeResult.Started);
            });

        Task first = coordinator.TryStartOnceAsync(CancellationToken.None);
        await coordinator.TryStartOnceAsync(CancellationToken.None);
        observed.SetResult(ObservedServiceState.Stopped);
        await first;

        Assert.Equal(1, reads);
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledStartConsumesAutomaticAttempt(bool fail)
    {
        int starts = 0;
        var coordinator = new TrayServiceStartupCoordinator(
            _ => Task.FromResult(ObservedServiceState.Stopped),
            _ =>
            {
                starts++;
                return fail
                    ? Task.FromException<StartOpenBladeResult>(new InvalidOperationException())
                    : Task.FromResult(StartOpenBladeResult.Canceled);
            });

        if (fail)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.TryStartOnceAsync(CancellationToken.None));
        }
        else
        {
            await coordinator.TryStartOnceAsync(CancellationToken.None);
        }
        await coordinator.TryStartOnceAsync(CancellationToken.None);

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task FailedStateReadDoesNotStartOrRetry()
    {
        int reads = 0;
        var coordinator = new TrayServiceStartupCoordinator(
            _ =>
            {
                reads++;
                return Task.FromException<ObservedServiceState>(new InvalidOperationException());
            },
            _ => throw new InvalidOperationException("Must not start after failed observation."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.TryStartOnceAsync(CancellationToken.None));
        await coordinator.TryStartOnceAsync(CancellationToken.None);

        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ExitCancellationDuringObservationPreventsStart()
    {
        using var cancellation = new CancellationTokenSource();
        var observed = new TaskCompletionSource<ObservedServiceState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new TrayServiceStartupCoordinator(
            _ => observed.Task,
            _ => throw new InvalidOperationException("Must not start after exit."));

        Task startup = coordinator.TryStartOnceAsync(cancellation.Token);
        cancellation.Cancel();
        observed.SetResult(ObservedServiceState.Stopped);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        await coordinator.TryStartOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ManualRequestSharesAutomaticStartAndCanRetryAfterCompletion()
    {
        var completed = new TaskCompletionSource<StartOpenBladeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        var coordinator = new TrayServiceStartupCoordinator(
            _ => Task.FromResult(ObservedServiceState.Stopped),
            _ =>
            {
                starts++;
                return completed.Task;
            });

        Task startup = coordinator.TryStartOnceAsync(CancellationToken.None);
        Task<StartOpenBladeResult> manual = coordinator.StartAsync(CancellationToken.None);
        Assert.Same(completed.Task, manual);
        Assert.Equal(1, starts);
        completed.SetResult(StartOpenBladeResult.Canceled);
        await startup;
        await manual;
        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.TryStartOnceAsync(CancellationToken.None);

        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task CompletedManualRequestSupersedesPendingAutomaticObservation()
    {
        var observed = new TaskCompletionSource<ObservedServiceState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        var coordinator = new TrayServiceStartupCoordinator(
            _ => observed.Task,
            _ =>
            {
                starts++;
                return Task.FromResult(StartOpenBladeResult.Canceled);
            });

        Task startup = coordinator.TryStartOnceAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);
        observed.SetResult(ObservedServiceState.Stopped);
        await startup;

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task ShutdownDuringObservationSuppressesAutomaticStart()
    {
        var observed = new TaskCompletionSource<ObservedServiceState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new TrayServiceStartupCoordinator(
            _ => observed.Task,
            _ => throw new InvalidOperationException("Shutdown must win."));

        Task startup = coordinator.TryStartOnceAsync(CancellationToken.None);
        await coordinator.SuspendStartsAsync();
        observed.SetResult(ObservedServiceState.Stopped);
        await startup;
        Assert.Equal(StartOpenBladeResult.Canceled,
            await coordinator.StartAsync(CancellationToken.None));
        coordinator.ResumeManualStarts();
        await coordinator.TryStartOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ShutdownWaitsForCommittedHelperEvenWhenStartFails()
    {
        var completed = new TaskCompletionSource<StartOpenBladeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        var coordinator = new TrayServiceStartupCoordinator(
            _ => Task.FromResult(ObservedServiceState.Stopped),
            _ =>
            {
                starts++;
                return completed.Task;
            });

        Task startup = coordinator.TryStartOnceAsync(CancellationToken.None);
        Task shutdownBarrier = coordinator.SuspendStartsAsync();
        Assert.False(shutdownBarrier.IsCompleted);
        Assert.Equal(StartOpenBladeResult.Canceled,
            await coordinator.StartAsync(CancellationToken.None));
        completed.SetException(new InvalidOperationException());
        await Assert.ThrowsAsync<InvalidOperationException>(() => startup);
        await shutdownBarrier;

        Assert.Equal(1, starts);
    }
}
