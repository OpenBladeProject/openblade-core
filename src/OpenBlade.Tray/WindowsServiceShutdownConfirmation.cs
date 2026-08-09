using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace OpenBlade.Tray;

internal static class WindowsServiceShutdownConfirmation
{
    private const string ServiceName = "OpenBlade";
    private const int ServiceDoesNotExistError = 1060;
    private const int AccessDeniedError = 5;
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    internal static async Task<bool> WaitForStoppedAsync(
        CancellationToken cancellationToken)
    {
        using var service = new ServiceController(ServiceName);
        var confirmation = new ServiceShutdownConfirmation(
            () => ReadState(service),
            Task.Delay,
            maximumPolls: checked((int)(ConfirmationTimeout / PollInterval)),
            PollInterval);
        return await confirmation.WaitForStoppedAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static ObservedServiceShutdownState ReadState(ServiceController service)
    {
        try
        {
            service.Refresh();
            return MapStatus(service.Status);
        }
        catch (Exception exception)
        {
            ObservedServiceShutdownState? mappedState = MapQueryError(exception);
            if (mappedState is null)
            {
                throw;
            }

            int errorCode = GetNativeErrorCode(exception)!.Value;
            string reason = errorCode == ServiceDoesNotExistError
                ? "the configured Windows service was not found"
                : "Windows denied access to Service Control Manager state";
            Trace.TraceWarning(
                $"OpenBlade service shutdown could not be confirmed because {reason}.");
            return mappedState.Value;
        }
    }

    internal static ObservedServiceShutdownState MapStatus(
        ServiceControllerStatus status) => status switch
        {
            ServiceControllerStatus.Stopped => ObservedServiceShutdownState.Stopped,
            ServiceControllerStatus.StopPending => ObservedServiceShutdownState.StopPending,
            _ => ObservedServiceShutdownState.Running,
        };

    internal static ObservedServiceShutdownState? MapQueryError(
        Exception exception) => GetNativeErrorCode(exception) switch
        {
            ServiceDoesNotExistError or AccessDeniedError =>
                ObservedServiceShutdownState.Unavailable,
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
