using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class DisplayBrightnessHidControllerTests
{
    [Theory]
    [InlineData(DisplayBrightnessHidAvailability.Available)]
    [InlineData(DisplayBrightnessHidAvailability.Unavailable)]
    [InlineData(DisplayBrightnessHidAvailability.Unknown)]
    public void GetAvailability_UsesReadOnlyTransportStatus(
        DisplayBrightnessHidAvailability availability)
    {
        var transport = new StubTransport(
            new DisplayBrightnessHidWriteResult(DisplayBrightnessHidWriteStatus.Applied),
            availability);
        var controller = new DisplayBrightnessHidController(transport);

        Assert.Equal(availability, controller.GetAvailability());
        Assert.Null(transport.Usage);
    }

    [Theory]
    [InlineData((int)DisplayBrightnessHidWriteStatus.Applied, ControlOperationStatus.Applied)]
    [InlineData((int)DisplayBrightnessHidWriteStatus.Unavailable, ControlOperationStatus.Blocked)]
    [InlineData((int)DisplayBrightnessHidWriteStatus.Failed, ControlOperationStatus.Failed)]
    public async Task SetUsageAsync_MapsTransportStatusWithoutAmbiguousFallback(
        int transportStatusValue,
        ControlOperationStatus expectedStatus)
    {
        DisplayBrightnessHidWriteStatus transportStatus =
            (DisplayBrightnessHidWriteStatus)transportStatusValue;
        var transport = new StubTransport(new DisplayBrightnessHidWriteResult(
            transportStatus,
            transportStatus == DisplayBrightnessHidWriteStatus.Failed ? "readback failed" : null));
        var controller = new DisplayBrightnessHidController(transport);

        ControlOperationResult result = await controller.SetUsageAsync(
            DisplayBrightnessHidUsage.Increment,
            CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(DisplayBrightnessHidUsage.Increment, transport.Usage);
        if (expectedStatus == ControlOperationStatus.Failed)
        {
            Assert.Contains("readback failed", result.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SetUsageAsync_UnavailableErrorIsLoggedButNotShownToUser()
    {
        using var history = new DiagnosticHistory();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            builder.AddProvider(history));
        var transport = new StubTransport(new DisplayBrightnessHidWriteResult(
            DisplayBrightnessHidWriteStatus.Unavailable,
            "Access is denied."));
        var controller = new DisplayBrightnessHidController(
            transport,
            loggerFactory.CreateLogger<DisplayBrightnessHidController>());

        ControlOperationResult result = await controller.SetUsageAsync(
            DisplayBrightnessHidUsage.Increment,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.DoesNotContain("Access is denied", result.Message, StringComparison.Ordinal);
        Assert.Contains(
            history.Snapshot().LogLines,
            line => line.Contains("Access is denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetUsageAsync_RejectsUnknownUsageBeforeTransport()
    {
        var transport = new StubTransport(new DisplayBrightnessHidWriteResult(
            DisplayBrightnessHidWriteStatus.Applied));
        var controller = new DisplayBrightnessHidController(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            controller.SetUsageAsync((DisplayBrightnessHidUsage)0x0071, CancellationToken.None));

        Assert.Null(transport.Usage);
    }

    [Fact]
    public async Task SetUsageAsync_ObservesCancellationBeforeTransport()
    {
        var transport = new StubTransport(new DisplayBrightnessHidWriteResult(
            DisplayBrightnessHidWriteStatus.Applied));
        var controller = new DisplayBrightnessHidController(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.SetUsageAsync(DisplayBrightnessHidUsage.Decrement, cancellation.Token));

        Assert.Null(transport.Usage);
    }

    private sealed class StubTransport(
        DisplayBrightnessHidWriteResult result,
        DisplayBrightnessHidAvailability availability =
            DisplayBrightnessHidAvailability.Available)
        : IDisplayBrightnessHidTransport
    {
        public DisplayBrightnessHidUsage? Usage { get; private set; }

        public DisplayBrightnessHidAvailability GetAvailability() => availability;

        public DisplayBrightnessHidWriteResult SetUsage(DisplayBrightnessHidUsage usage)
        {
            Usage = usage;
            return result;
        }
    }
}
