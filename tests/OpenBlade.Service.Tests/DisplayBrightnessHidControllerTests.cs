using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class DisplayBrightnessHidControllerTests
{
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

    [Theory]
    [InlineData(DisplayBrightnessHidUsage.Released, 0x00, 0x00)]
    [InlineData(DisplayBrightnessHidUsage.Increment, 0x6F, 0x00)]
    [InlineData(DisplayBrightnessHidUsage.Decrement, 0x70, 0x00)]
    public void FeatureReport_IsExactlyThreeBytesAndLittleEndian(
        DisplayBrightnessHidUsage usage,
        byte lowByte,
        byte highByte)
    {
        Assert.Equal(
            [WindowsDisplayBrightnessHidTransport.FeatureReportId, lowByte, highByte],
            WindowsDisplayBrightnessHidTransport.CreateFeatureReport(usage));
    }

    [Fact]
    public void NativeHidCapabilitiesLayout_IsTheDocumentedSixtyFourBytes()
    {
        Assert.Equal(64, WindowsDisplayBrightnessHidTransport.HidCapabilitiesSize);
    }

    private sealed class StubTransport(DisplayBrightnessHidWriteResult result)
        : IDisplayBrightnessHidTransport
    {
        public DisplayBrightnessHidUsage? Usage { get; private set; }

        public DisplayBrightnessHidWriteResult SetUsage(DisplayBrightnessHidUsage usage)
        {
            Usage = usage;
            return result;
        }
    }
}
