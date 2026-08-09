using System.ComponentModel;
using System.ServiceProcess;

namespace OpenBlade.Tray.Tests;

public sealed class WindowsServiceShutdownConfirmationTests
{
    [Fact]
    public void MapStatusRecognizesStoppedAndStopPending()
    {
        Assert.Equal(
            ObservedServiceShutdownState.Stopped,
            WindowsServiceShutdownConfirmation.MapStatus(ServiceControllerStatus.Stopped));
        Assert.Equal(
            ObservedServiceShutdownState.StopPending,
            WindowsServiceShutdownConfirmation.MapStatus(ServiceControllerStatus.StopPending));
    }

    [Theory]
    [InlineData(ServiceControllerStatus.ContinuePending)]
    [InlineData(ServiceControllerStatus.Paused)]
    [InlineData(ServiceControllerStatus.PausePending)]
    [InlineData(ServiceControllerStatus.Running)]
    [InlineData(ServiceControllerStatus.StartPending)]
    public void MapStatusTreatsOtherServiceStatesAsRunning(
        ServiceControllerStatus status)
    {
        Assert.Equal(
            ObservedServiceShutdownState.Running,
            WindowsServiceShutdownConfirmation.MapStatus(status));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1060)]
    public void MapQueryErrorTreatsDirectAndWrappedUnavailableErrorsAsUnconfirmed(
        int errorCode)
    {
        var win32Exception = new Win32Exception(errorCode);
        var wrappedException = new InvalidOperationException(
            "Service query failed.",
            win32Exception);

        Assert.Equal(
            ObservedServiceShutdownState.Unavailable,
            WindowsServiceShutdownConfirmation.MapQueryError(win32Exception));
        Assert.Equal(
            ObservedServiceShutdownState.Unavailable,
            WindowsServiceShutdownConfirmation.MapQueryError(wrappedException));
    }

    [Fact]
    public void MapQueryErrorLeavesUnrelatedExceptionsUnhandled()
    {
        Assert.Null(
            WindowsServiceShutdownConfirmation.MapQueryError(
                new InvalidOperationException("Service query failed.")));
    }
}
