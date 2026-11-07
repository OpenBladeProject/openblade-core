using System.Buffers.Binary;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class FanStopTemperatureSourceTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public async Task UnknownOrReportedD3NeverOpensTemperatureSession(int power, bool poweredDown)
    {
        var gpu = new Gpu();
        using var source = Create(gpu, () => (FanStopGpuPowerState)power);
        FanTemperatureReadings reading = await source.ReadForFanStopAsync(default);
        Assert.Equal(40, reading.CpuCelsius);
        Assert.Null(reading.GpuCelsius);
        Assert.True(reading.GpuInactive); // Workload absence is separate evidence.
        Assert.Equal(poweredDown, reading.GpuPoweredDown);
        Assert.Equal(0, gpu.Reads);
    }

    [Fact]
    public async Task IdleD0UsesActualTemperatureAndReleasesItsSession()
    {
        var gpu = new Gpu();
        using var source = Create(gpu, () => FanStopGpuPowerState.PoweredOn);
        FanTemperatureReadings reading = await source.ReadForFanStopAsync(default);
        Assert.Equal(42, reading.GpuCelsius);
        Assert.True(reading.GpuInactive);
        Assert.False(reading.GpuPoweredDown);
        Assert.Equal(1, gpu.Reads);
        Assert.Equal(1, gpu.Releases);
    }

    [Fact]
    public async Task FailedActiveTemperatureCannotBeReportedAsSleepingGpu()
    {
        var gpu = new Gpu { Temperature = null };
        using var source = Create(gpu, () => FanStopGpuPowerState.PoweredOn);
        FanTemperatureReadings reading = await source.ReadForFanStopAsync(default);
        Assert.Null(reading.GpuCelsius);
        Assert.False(reading.GpuPoweredDown);
    }

    [Fact]
    public async Task PowerTransitionDuringTemperatureReadInvalidatesTheSample()
    {
        int reads = 0;
        var gpu = new Gpu();
        using var source = Create(gpu, () => ++reads == 1 ? FanStopGpuPowerState.PoweredOn : FanStopGpuPowerState.PoweredDown);
        FanTemperatureReadings reading = await source.ReadForFanStopAsync(default);
        Assert.Null(reading.GpuCelsius);
        Assert.False(reading.GpuPoweredDown);
    }

    [Fact]
    public async Task OrdinaryCurveSamplingRetainsWorkloadBasedNoWakeBehavior()
    {
        var gpu = new Gpu();
        using var source = Create(gpu, () => throw new InvalidOperationException("Ordinary samples must not use the new source."));
        FanTemperatureReadings reading = await source.ReadAsync(default);
        Assert.True(reading.GpuInactive);
        Assert.False(reading.GpuPoweredDown);
        Assert.Equal(0, gpu.Reads);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 2)]
    [InlineData(5, 0)]
    public void OnlyOsReportedD0AndD3HaveEstablishedMeaning(uint native, int expected) =>
        Assert.Equal((FanStopGpuPowerState)expected, WindowsNvidiaGpuPowerState.Classify(native));

    [Fact]
    public void MalformedPowerDataCannotEstablishSleepingGpu()
    {
        byte[] data = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 4);
        Assert.Equal(FanStopGpuPowerState.PoweredDown, WindowsNvidiaGpuPowerState.ClassifyPowerData(data, 3));
        Assert.Equal(FanStopGpuPowerState.Unknown, WindowsNvidiaGpuPowerState.ClassifyPowerData(data, 1));
        Assert.Equal(FanStopGpuPowerState.Unknown, WindowsNvidiaGpuPowerState.ClassifyPowerData(data.AsSpan(0, 8), 3));
        data[0] = 55;
        Assert.Equal(FanStopGpuPowerState.Unknown, WindowsNvidiaGpuPowerState.ClassifyPowerData(data, 3));
    }

    private static SafeWindowsFanTemperatureSource Create(Gpu gpu, Func<FanStopGpuPowerState> power) =>
        new(new Inactive(), () => 40, gpu, TimeProvider.System, gpuPowerState: power);
    private sealed class Inactive : IGpuActivitySource
    { public ValueTask<bool?> IsActiveAsync(CancellationToken token) => ValueTask.FromResult<bool?>(false); }
    private sealed class Gpu : IGpuTemperatureSession
    {
        internal int? Temperature = 42;
        internal int Reads, Releases;
        public int? TryRead() { Reads++; return Temperature; }
        public void Release() => Releases++;
    }
}
