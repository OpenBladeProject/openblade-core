using System.ComponentModel;
using System.ServiceProcess;

namespace OpenBlade.Tray.Tests;

public sealed class WindowsServiceStateConfirmationTests
{
    [Fact]
    public void StartAndStopConfirmationsKeepTheirReviewedBounds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), WindowsServiceStateConfirmation.StartConfirmationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), WindowsServiceStateConfirmation.StopConfirmationTimeout);
    }

    [Theory]
    [InlineData(ServiceControllerStatus.Running, (int)ObservedServiceState.Running)]
    [InlineData(ServiceControllerStatus.StartPending, (int)ObservedServiceState.StartPending)]
    [InlineData(ServiceControllerStatus.ContinuePending, (int)ObservedServiceState.ContinuePending)]
    [InlineData(ServiceControllerStatus.StopPending, (int)ObservedServiceState.StopPending)]
    [InlineData(ServiceControllerStatus.Stopped, (int)ObservedServiceState.Stopped)]
    [InlineData(ServiceControllerStatus.Paused, (int)ObservedServiceState.Other)]
    [InlineData(ServiceControllerStatus.PausePending, (int)ObservedServiceState.Other)]
    public void MapStatusRecognizesEveryServiceState(
        ServiceControllerStatus status,
        int expectedValue)
    {
        Assert.Equal(
            (ObservedServiceState)expectedValue,
            WindowsServiceStateConfirmation.MapStatus(status));
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
            ObservedServiceState.Unavailable,
            WindowsServiceStateConfirmation.MapQueryError(win32Exception));
        Assert.Equal(
            ObservedServiceState.Unavailable,
            WindowsServiceStateConfirmation.MapQueryError(wrappedException));
    }

    [Fact]
    public void MapQueryErrorLeavesUnrelatedExceptionsUnhandled()
    {
        Assert.Null(
            WindowsServiceStateConfirmation.MapQueryError(
                new InvalidOperationException("Service query failed.")));
    }
}
