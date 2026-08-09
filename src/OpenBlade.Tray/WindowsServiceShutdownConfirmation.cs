using System.ComponentModel;
using System.ServiceProcess;

namespace OpenBlade.Tray;

internal static class WindowsServiceShutdownConfirmation
{
    private const string ServiceName = "OpenBlade";
    private const int ServiceDoesNotExistError = 1060;
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
            return service.Status switch
            {
                ServiceControllerStatus.Stopped => ObservedServiceShutdownState.Stopped,
                ServiceControllerStatus.StopPending => ObservedServiceShutdownState.StopPending,
                _ => ObservedServiceShutdownState.Running,
            };
        }
        catch (InvalidOperationException exception)
            when (exception.InnerException is Win32Exception
            {
                NativeErrorCode: ServiceDoesNotExistError,
            })
        {
            return ObservedServiceShutdownState.Stopped;
        }
        catch (Win32Exception exception)
            when (exception.NativeErrorCode == ServiceDoesNotExistError)
        {
            return ObservedServiceShutdownState.Stopped;
        }
    }
}
