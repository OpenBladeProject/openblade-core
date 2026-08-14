using Microsoft.Extensions.Logging;
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

    [Fact]
    public void Transport_CachesResolvedControlCollectionPath()
    {
        int resolutionCount = 0;
        var writtenPaths = new List<string>();
        var transport = new WindowsDisplayBrightnessHidTransport(
            () =>
            {
                resolutionCount++;
                return (
                    "cached-path",
                    new DisplayBrightnessHidWriteResult(
                        DisplayBrightnessHidWriteStatus.Applied));
            },
            (path, _) =>
            {
                writtenPaths.Add(path);
                return new DisplayBrightnessHidWriteResult(
                    DisplayBrightnessHidWriteStatus.Applied);
            });

        DisplayBrightnessHidWriteResult first = transport.SetUsage(
            DisplayBrightnessHidUsage.Increment);
        DisplayBrightnessHidWriteResult second = transport.SetUsage(
            DisplayBrightnessHidUsage.Released);

        Assert.Equal(DisplayBrightnessHidWriteStatus.Applied, first.Status);
        Assert.Equal(DisplayBrightnessHidWriteStatus.Applied, second.Status);
        Assert.Equal(1, resolutionCount);
        Assert.Equal(["cached-path", "cached-path"], writtenPaths);
    }

    [Fact]
    public void Transport_RediscoversPathWhenCachedCollectionDisappears()
    {
        int resolutionCount = 0;
        int firstPathWrites = 0;
        var transport = new WindowsDisplayBrightnessHidTransport(
            () =>
            {
                resolutionCount++;
                return (
                    $"path-{resolutionCount}",
                    new DisplayBrightnessHidWriteResult(
                        DisplayBrightnessHidWriteStatus.Applied));
            },
            (path, _) =>
            {
                if (path == "path-1" && ++firstPathWrites == 2)
                {
                    return new DisplayBrightnessHidWriteResult(
                        DisplayBrightnessHidWriteStatus.Unavailable);
                }

                return new DisplayBrightnessHidWriteResult(
                    DisplayBrightnessHidWriteStatus.Applied);
            });

        _ = transport.SetUsage(DisplayBrightnessHidUsage.Increment);
        DisplayBrightnessHidWriteResult result = transport.SetUsage(
            DisplayBrightnessHidUsage.Decrement);

        Assert.Equal(DisplayBrightnessHidWriteStatus.Applied, result.Status);
        Assert.Equal(2, resolutionCount);
    }

    [Fact]
    public void Transport_DoesNotRetryIndeterminateCachedWrite()
    {
        int resolutionCount = 0;
        int writeCount = 0;
        var transport = new WindowsDisplayBrightnessHidTransport(
            () =>
            {
                resolutionCount++;
                return (
                    "cached-path",
                    new DisplayBrightnessHidWriteResult(
                        DisplayBrightnessHidWriteStatus.Applied));
            },
            (_, _) =>
            {
                writeCount++;
                return new DisplayBrightnessHidWriteResult(
                    writeCount == 1
                        ? DisplayBrightnessHidWriteStatus.Applied
                        : DisplayBrightnessHidWriteStatus.Failed,
                    writeCount == 1 ? null : "indeterminate write");
            });

        _ = transport.SetUsage(DisplayBrightnessHidUsage.Increment);
        DisplayBrightnessHidWriteResult result = transport.SetUsage(
            DisplayBrightnessHidUsage.Increment);

        Assert.Equal(DisplayBrightnessHidWriteStatus.Failed, result.Status);
        Assert.Equal("indeterminate write", result.Error);
        Assert.Equal(1, resolutionCount);
        Assert.Equal(2, writeCount);
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
