using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class GpuClockOffsetProbeCommandTests
{
    [Fact]
    public void Run_PrintsBothCapabilityRanges()
    {
        var output = new StringWriter();
        var probe = new StubProbe(new GpuClockOffsetCapabilities(
            "NVIDIA Test GPU",
            new GpuClockOffsetRange(-500, 1000, 150),
            new GpuClockOffsetRange(-1000, 3000, 150)));

        int result = GpuClockOffsetProbeCommand.Run(probe, output);

        Assert.Equal(0, result);
        Assert.Contains("Device=NVIDIA Test GPU", output.ToString());
        Assert.Contains("CoreOffsetMHz=150;Min=-500;Max=1000", output.ToString());
        Assert.Contains("MemoryOffsetMHz=150;Min=-1000;Max=3000", output.ToString());
        Assert.Contains("Success=True", output.ToString());
    }

    [Fact]
    public void Run_ReportsUnsupportedProbeWithoutThrowing()
    {
        var output = new StringWriter();
        var probe = new ThrowingProbe();

        int result = GpuClockOffsetProbeCommand.Run(probe, output);

        Assert.Equal(2, result);
        Assert.Contains("Failure=Clock offsets are unavailable.", output.ToString());
        Assert.Contains("Success=False", output.ToString());
    }

    private sealed class StubProbe(GpuClockOffsetCapabilities value) : IGpuClockOffsetProbe
    {
        public GpuClockOffsetCapabilities Query() => value;
    }

    private sealed class ThrowingProbe : IGpuClockOffsetProbe
    {
        public GpuClockOffsetCapabilities Query() =>
            throw new InvalidOperationException("Clock offsets are unavailable.");
    }
}
