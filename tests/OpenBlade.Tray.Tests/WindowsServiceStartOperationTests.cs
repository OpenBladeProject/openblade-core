using System.ComponentModel;
using System.ServiceProcess;

namespace OpenBlade.Tray.Tests;

public sealed class WindowsServiceStartOperationTests
{
    [Theory]
    [InlineData((int)ObservedServiceState.Running, (int)WindowsServiceStartPreflightResult.AlreadyRunning)]
    [InlineData((int)ObservedServiceState.Stopped, (int)WindowsServiceStartPreflightResult.RequiresElevation)]
    [InlineData((int)ObservedServiceState.StopPending, (int)WindowsServiceStartPreflightResult.RequiresElevation)]
    [InlineData((int)ObservedServiceState.Other, (int)WindowsServiceStartPreflightResult.Unavailable)]
    [InlineData((int)ObservedServiceState.Unavailable, (int)WindowsServiceStartPreflightResult.Unavailable)]
    public async Task PreflightMapsStableStatesWithoutWaiting(
        int stateValue,
        int expectedValue)
    {
        int waits = 0;

        WindowsServiceStartPreflightResult result =
            await WindowsServiceStartOperation.EvaluatePreflightAsync(
                _ => Task.FromResult((ObservedServiceState)stateValue),
                _ =>
                {
                    waits++;
                    return Task.FromResult(true);
                },
                CancellationToken.None);

        Assert.Equal((WindowsServiceStartPreflightResult)expectedValue, result);
        Assert.Equal(0, waits);
    }

    [Theory]
    [InlineData((int)ObservedServiceState.StartPending, true, (int)WindowsServiceStartPreflightResult.AlreadyRunning)]
    [InlineData((int)ObservedServiceState.StartPending, false, (int)WindowsServiceStartPreflightResult.Unavailable)]
    [InlineData((int)ObservedServiceState.ContinuePending, true, (int)WindowsServiceStartPreflightResult.AlreadyRunning)]
    [InlineData((int)ObservedServiceState.ContinuePending, false, (int)WindowsServiceStartPreflightResult.Unavailable)]
    public async Task PreflightWaitsForPendingStart(
        int stateValue,
        bool reachesRunning,
        int expectedValue)
    {
        int waits = 0;

        WindowsServiceStartPreflightResult result =
            await WindowsServiceStartOperation.EvaluatePreflightAsync(
                _ => Task.FromResult((ObservedServiceState)stateValue),
                _ =>
                {
                    waits++;
                    return Task.FromResult(reachesRunning);
                },
                CancellationToken.None);

        Assert.Equal((WindowsServiceStartPreflightResult)expectedValue, result);
        Assert.Equal(1, waits);
    }

    [Fact]
    public async Task RunningReturnsWithoutStartOrWait()
    {
        int starts = 0;
        int waits = 0;

        await WindowsServiceStartOperation.ExecuteAsync(
            () => ServiceControllerStatus.Running,
            () => starts++,
            _ =>
            {
                waits++;
                return Task.FromResult(true);
            },
            _ =>
            {
                waits++;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(0, starts);
        Assert.Equal(0, waits);
    }

    [Fact]
    public async Task StoppedStartsExactlyOnceThenWaitsForRunning()
    {
        int starts = 0;
        int runningWaits = 0;

        await WindowsServiceStartOperation.ExecuteAsync(
            () => ServiceControllerStatus.Stopped,
            () => starts++,
            _ =>
            {
                runningWaits++;
                return Task.FromResult(true);
            },
            _ => throw new InvalidOperationException("Unexpected stopped wait."),
            CancellationToken.None);

        Assert.Equal(1, starts);
        Assert.Equal(1, runningWaits);
    }

    [Theory]
    [InlineData(ServiceControllerStatus.StartPending)]
    [InlineData(ServiceControllerStatus.ContinuePending)]
    public async Task PendingStartWaitsForRunningWithoutAnotherStart(
        ServiceControllerStatus status)
    {
        int starts = 0;
        int runningWaits = 0;

        await WindowsServiceStartOperation.ExecuteAsync(
            () => status,
            () => starts++,
            _ =>
            {
                runningWaits++;
                return Task.FromResult(true);
            },
            _ => throw new InvalidOperationException("Unexpected stopped wait."),
            CancellationToken.None);

        Assert.Equal(0, starts);
        Assert.Equal(1, runningWaits);
    }

    [Fact]
    public async Task StopPendingWaitsForStoppedThenStartsExactlyOnce()
    {
        var states = new Queue<ServiceControllerStatus>(
        [
            ServiceControllerStatus.StopPending,
            ServiceControllerStatus.Stopped,
        ]);
        int starts = 0;
        int stoppedWaits = 0;
        int runningWaits = 0;

        await WindowsServiceStartOperation.ExecuteAsync(
            states.Dequeue,
            () => starts++,
            _ =>
            {
                runningWaits++;
                return Task.FromResult(true);
            },
            _ =>
            {
                stoppedWaits++;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(1, starts);
        Assert.Equal(1, stoppedWaits);
        Assert.Equal(1, runningWaits);
        Assert.Empty(states);
    }

    [Fact]
    public async Task ConcurrentStartAfterStopWaitReturnsWithoutAnotherStart()
    {
        var states = new Queue<ServiceControllerStatus>(
        [
            ServiceControllerStatus.StopPending,
            ServiceControllerStatus.Running,
        ]);
        int starts = 0;
        int runningWaits = 0;

        await WindowsServiceStartOperation.ExecuteAsync(
            states.Dequeue,
            () => starts++,
            _ =>
            {
                runningWaits++;
                return Task.FromResult(true);
            },
            _ => Task.FromResult(true),
            CancellationToken.None);

        Assert.Equal(0, starts);
        Assert.Equal(0, runningWaits);
        Assert.Empty(states);
    }

    [Theory]
    [InlineData(ServiceControllerStatus.Paused)]
    [InlineData(ServiceControllerStatus.PausePending)]
    public async Task UnsupportedStateFailsWithoutStart(ServiceControllerStatus status)
    {
        int starts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsServiceStartOperation.ExecuteAsync(
                () => status,
                () => starts++,
                _ => Task.FromResult(true),
                _ => Task.FromResult(true),
                CancellationToken.None));

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task StopPendingTimeoutFailsWithoutStart()
    {
        int starts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsServiceStartOperation.ExecuteAsync(
                () => ServiceControllerStatus.StopPending,
                () => starts++,
                _ => Task.FromResult(true),
                _ => Task.FromResult(false),
                CancellationToken.None));

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task RunningTimeoutDoesNotRetryStart()
    {
        int starts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsServiceStartOperation.ExecuteAsync(
                () => ServiceControllerStatus.Stopped,
                () => starts++,
                _ => Task.FromResult(false),
                _ => Task.FromResult(true),
                CancellationToken.None));

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task StartFailurePropagatesWithoutRetry()
    {
        int starts = 0;

        Win32Exception exception = await Assert.ThrowsAsync<Win32Exception>(() =>
            WindowsServiceStartOperation.ExecuteAsync(
                () => ServiceControllerStatus.Stopped,
                () =>
                {
                    starts++;
                    throw new Win32Exception(1058);
                },
                _ => Task.FromResult(true),
                _ => Task.FromResult(true),
                CancellationToken.None));

        Assert.Equal(1058, exception.NativeErrorCode);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task PreCanceledOperationDoesNotReadOrStart()
    {
        int reads = 0;
        int starts = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsServiceStartOperation.ExecuteAsync(
                () =>
                {
                    reads++;
                    return ServiceControllerStatus.Stopped;
                },
                () => starts++,
                _ => Task.FromResult(true),
                _ => Task.FromResult(true),
                cancellation.Token));

        Assert.Equal(0, reads);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task CancellationDuringStateReadPreventsStart()
    {
        int starts = 0;
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsServiceStartOperation.ExecuteAsync(
                () =>
                {
                    cancellation.Cancel();
                    return ServiceControllerStatus.Stopped;
                },
                () => starts++,
                _ => Task.FromResult(true),
                _ => Task.FromResult(true),
                cancellation.Token));

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task CancellationWhileStoppingPreventsStart()
    {
        var states = new Queue<ServiceControllerStatus>(
        [
            ServiceControllerStatus.StopPending,
            ServiceControllerStatus.Stopped,
        ]);
        int starts = 0;
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsServiceStartOperation.ExecuteAsync(
                states.Dequeue,
                () => starts++,
                _ => Task.FromResult(true),
                _ =>
                {
                    cancellation.Cancel();
                    return Task.FromResult(true);
                },
                cancellation.Token));

        Assert.Equal(0, starts);
        Assert.Empty(states);
    }
}
