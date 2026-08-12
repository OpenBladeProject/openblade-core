using System.ServiceProcess;

namespace OpenBlade.Tray;

internal enum WindowsServiceStartPlan
{
    AlreadyRunning,
    Start,
    WaitForRunning,
    WaitForStoppedThenStart,
    Unsupported,
}

internal enum WindowsServiceStartPreflightResult
{
    AlreadyRunning,
    RequiresElevation,
    Unavailable,
}

internal static class WindowsServiceStartOperation
{
    internal static async Task<WindowsServiceStartPreflightResult> PreflightAsync(
        CancellationToken cancellationToken)
    {
        return await EvaluatePreflightAsync(
                token => Task.Run(
                    WindowsServiceStateConfirmation.ReadCurrentState,
                    token),
                token => Task.Run(
                    () => WindowsServiceStateConfirmation.WaitForRunningAsync(token),
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<WindowsServiceStartPreflightResult> EvaluatePreflightAsync(
        Func<CancellationToken, Task<ObservedServiceState>> readStateAsync,
        Func<CancellationToken, Task<bool>> waitForRunningAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readStateAsync);
        ArgumentNullException.ThrowIfNull(waitForRunningAsync);
        ObservedServiceState state = await readStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state == ObservedServiceState.Running)
        {
            return WindowsServiceStartPreflightResult.AlreadyRunning;
        }
        if (state is ObservedServiceState.Stopped or ObservedServiceState.StopPending)
        {
            return WindowsServiceStartPreflightResult.RequiresElevation;
        }
        if (state is ObservedServiceState.StartPending
            or ObservedServiceState.ContinuePending)
        {
            bool running = await waitForRunningAsync(cancellationToken)
                .ConfigureAwait(false);
            return running
                ? WindowsServiceStartPreflightResult.AlreadyRunning
                : WindowsServiceStartPreflightResult.Unavailable;
        }

        return WindowsServiceStartPreflightResult.Unavailable;
    }

    internal static async Task StartAsync(CancellationToken cancellationToken)
    {
        using var service = new ServiceController(WindowsServiceStateConfirmation.ServiceName);
        await ExecuteAsync(
                () =>
                {
                    service.Refresh();
                    return service.Status;
                },
                service.Start,
                WindowsServiceStateConfirmation.WaitForRunningAsync,
                WindowsServiceStateConfirmation.WaitForStoppedAsync,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task ExecuteAsync(
        Func<ServiceControllerStatus> readStatus,
        Action start,
        Func<CancellationToken, Task<bool>> waitForRunningAsync,
        Func<CancellationToken, Task<bool>> waitForStoppedAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readStatus);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(waitForRunningAsync);
        ArgumentNullException.ThrowIfNull(waitForStoppedAsync);
        cancellationToken.ThrowIfCancellationRequested();

        ServiceControllerStatus status = readStatus();
        WindowsServiceStartPlan plan = Plan(status);
        if (plan == WindowsServiceStartPlan.AlreadyRunning)
        {
            return;
        }
        if (plan == WindowsServiceStartPlan.Unsupported)
        {
            throw new InvalidOperationException(
                $"OpenBlade service cannot be started from state {status}.");
        }
        bool shouldStart = plan == WindowsServiceStartPlan.Start;
        if (plan == WindowsServiceStartPlan.WaitForStoppedThenStart)
        {
            if (!await waitForStoppedAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "OpenBlade service did not finish stopping before the start request timed out.");
            }

            status = readStatus();
            if (status == ServiceControllerStatus.Running)
            {
                return;
            }
            if (status != ServiceControllerStatus.Stopped)
            {
                throw new InvalidOperationException(
                    $"OpenBlade service cannot be started from state {status}.");
            }

            shouldStart = true;
        }

        if (shouldStart)
        {
            cancellationToken.ThrowIfCancellationRequested();
            start();
        }

        if (!await waitForRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "OpenBlade service did not reach Running before the start request timed out.");
        }
    }

    internal static WindowsServiceStartPlan Plan(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Running => WindowsServiceStartPlan.AlreadyRunning,
        ServiceControllerStatus.Stopped => WindowsServiceStartPlan.Start,
        ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending =>
            WindowsServiceStartPlan.WaitForRunning,
        ServiceControllerStatus.StopPending =>
            WindowsServiceStartPlan.WaitForStoppedThenStart,
        _ => WindowsServiceStartPlan.Unsupported,
    };
}
