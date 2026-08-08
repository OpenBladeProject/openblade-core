using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class NvidiaGpuClockOffsetControllerTests
{
    [Fact]
    public void Query_ReturnsDriverRangesAndCurrentOffsets()
    {
        var api = new FakeNvmlApi
        {
            CoreCurrent = 75,
            MemoryCurrent = 300,
        };

        GpuClockOffsetCapabilities? actual =
            new NvidiaGpuClockOffsetController(api).Query();

        Assert.NotNull(actual);
        Assert.Equal("Test NVIDIA GPU", actual.DeviceName);
        Assert.Equal(new GpuClockOffsetRange(-1000, 1000, 75), actual.Core);
        Assert.Equal(new GpuClockOffsetRange(-1000, 3000, 150), actual.Memory);
        Assert.Equal(1, api.InitializeCalls);
        Assert.Equal(1, api.ShutdownCalls);
    }

    [Fact]
    public void Query_ReturnsUnavailableWhenNvmlCannotInitialize()
    {
        var api = new FakeNvmlApi { InitializeStatus = 3 };

        GpuClockOffsetCapabilities? actual =
            new NvidiaGpuClockOffsetController(api).Query();

        Assert.Null(actual);
        Assert.Equal(0, api.ShutdownCalls);
    }

    [Fact]
    public void Set_WritesBothOffsetsAndConfirmsReadback()
    {
        var api = new FakeNvmlApi();
        var requested = new GpuClockOffsetSettings(125, 500);

        new NvidiaGpuClockOffsetController(api).Set(requested);

        Assert.Equal(125, api.CoreCurrent);
        Assert.Equal(1000, api.MemoryCurrent);
        Assert.Equal(
            [
                (NvmlClockType.Graphics, 125),
                (NvmlClockType.Memory, 1000),
            ],
            api.Writes);
        Assert.Equal(1, api.ShutdownCalls);
    }

    [Fact]
    public void Set_RejectsOffsetsOutsideDriverRangeWithoutWriting()
    {
        var api = new FakeNvmlApi();

        BladeWriteException error = Assert.Throws<BladeWriteException>(() =>
            new NvidiaGpuClockOffsetController(api).Set(
                new GpuClockOffsetSettings(1001, 0)));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Empty(api.Writes);
        Assert.Equal(1, api.ShutdownCalls);
    }

    [Fact]
    public void Set_RestoresCoreWhenMemoryWriteFails()
    {
        var api = new FakeNvmlApi
        {
            CoreCurrent = 50,
            MemoryCurrent = 400,
            FailFirstMemoryWrite = true,
        };

        BladeWriteException error = Assert.Throws<BladeWriteException>(() =>
            new NvidiaGpuClockOffsetController(api).Set(
                new GpuClockOffsetSettings(125, 500)));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Contains("restored", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(50, api.CoreCurrent);
        Assert.Equal(400, api.MemoryCurrent);
        Assert.Equal(
            [
                (NvmlClockType.Graphics, 125),
                (NvmlClockType.Memory, 1000),
                (NvmlClockType.Graphics, 50),
            ],
            api.Writes);
        Assert.Equal(1, api.ShutdownCalls);
    }

    private sealed class FakeNvmlApi : INvidiaNvmlClockOffsetApi
    {
        public int InitializeStatus { get; init; }

        public int InitializeCalls { get; private set; }

        public int ShutdownCalls { get; private set; }

        public int CoreCurrent { get; set; }

        public int MemoryCurrent { get; set; }

        public bool FailFirstMemoryWrite { get; init; }

        public List<(NvmlClockType Type, int Offset)> Writes { get; } = [];

        public int Initialize()
        {
            InitializeCalls++;
            return InitializeStatus;
        }

        public int Shutdown()
        {
            ShutdownCalls++;
            return 0;
        }

        public int GetDeviceCount(out uint count)
        {
            count = 1;
            return 0;
        }

        public int GetDeviceHandleByIndex(uint index, out nint device)
        {
            device = (nint)1;
            return 0;
        }

        public int GetDeviceName(nint device, StringBuilder name, uint length)
        {
            name.Append("Test NVIDIA GPU");
            return 0;
        }

        public int GetClockOffsets(nint device, ref NvmlClockOffset info)
        {
            if (info.Type == NvmlClockType.Graphics)
            {
                info.MinimumMegahertz = -1000;
                info.MaximumMegahertz = 1000;
                info.CurrentMegahertz = CoreCurrent;
            }
            else
            {
                info.MinimumMegahertz = -2000;
                info.MaximumMegahertz = 6000;
                info.CurrentMegahertz = MemoryCurrent;
            }

            return 0;
        }

        public int SetClockOffsets(nint device, ref NvmlClockOffset info)
        {
            Writes.Add((info.Type, info.CurrentMegahertz));
            if (info.Type == NvmlClockType.Memory
                && FailFirstMemoryWrite
                && Writes.Count(write => write.Type == NvmlClockType.Memory) == 1)
            {
                return 3;
            }

            if (info.Type == NvmlClockType.Graphics)
            {
                CoreCurrent = info.CurrentMegahertz;
            }
            else
            {
                MemoryCurrent = info.CurrentMegahertz;
            }

            return 0;
        }
    }
}
