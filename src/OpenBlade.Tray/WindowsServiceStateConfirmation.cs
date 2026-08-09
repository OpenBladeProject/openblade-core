using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

internal static class WindowsServiceStateConfirmation
{
    internal const string ServiceName = OpenBladeWindowsService.Name;

    private const int ServiceDoesNotExistError = 1060;
    private const int AccessDeniedError = 5;
    internal static readonly TimeSpan StartConfirmationTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan StopConfirmationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    internal static Task<bool> WaitForRunningAsync(CancellationToken cancellationToken) =>
        WaitForAsync(
            ObservedServiceState.Running,
            StartConfirmationTimeout,
            cancellationToken);

    internal static Task<bool> WaitForStoppedAsync(CancellationToken cancellationToken) =>
        WaitForAsync(
            ObservedServiceState.Stopped,
            StopConfirmationTimeout,
            cancellationToken);

    private static async Task<bool> WaitForAsync(
        ObservedServiceState expectedState,
        TimeSpan confirmationTimeout,
        CancellationToken cancellationToken)
    {
        using var service = new ServiceController(ServiceName);
        var confirmation = new ServiceStateConfirmation(
            () => ReadState(service),
            Task.Delay,
            maximumPolls: checked((int)(confirmationTimeout / PollInterval)),
            PollInterval);
        return await confirmation.WaitForAsync(expectedState, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static ObservedServiceState ReadCurrentState()
    {
        using var service = new ServiceController(ServiceName);
        return ReadState(service);
    }

    private static ObservedServiceState ReadState(ServiceController service)
    {
        try
        {
            service.Refresh();
            return MapStatus(service.Status);
        }
        catch (Exception exception)
        {
            ObservedServiceState? mappedState = MapQueryError(exception);
            if (mappedState is null)
            {
                throw;
            }

            int errorCode = GetNativeErrorCode(exception)!.Value;
            string reason = errorCode == ServiceDoesNotExistError
                ? "the configured Windows service was not found"
                : "Windows denied access to Service Control Manager state";
            Trace.TraceWarning(
                $"OpenBlade service state could not be confirmed because {reason}.");
            return mappedState.Value;
        }
    }

    internal static ObservedServiceState MapStatus(
        ServiceControllerStatus status) => status switch
        {
            ServiceControllerStatus.Running => ObservedServiceState.Running,
            ServiceControllerStatus.StartPending => ObservedServiceState.StartPending,
            ServiceControllerStatus.ContinuePending => ObservedServiceState.ContinuePending,
            ServiceControllerStatus.StopPending => ObservedServiceState.StopPending,
            ServiceControllerStatus.Stopped => ObservedServiceState.Stopped,
            _ => ObservedServiceState.Other,
        };

    internal static ObservedServiceState? MapQueryError(
        Exception exception) => GetNativeErrorCode(exception) switch
        {
            ServiceDoesNotExistError or AccessDeniedError =>
                ObservedServiceState.Unavailable,
            _ => null,
        };

    private static int? GetNativeErrorCode(Exception exception) => exception switch
    {
        Win32Exception win32Exception => win32Exception.NativeErrorCode,
        InvalidOperationException { InnerException: Win32Exception win32Exception } =>
            win32Exception.NativeErrorCode,
        _ => null,
    };
}
