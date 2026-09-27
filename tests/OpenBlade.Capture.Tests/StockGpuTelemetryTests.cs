using System.Text;
using OpenBlade.Service;

namespace OpenBlade.Capture.Tests;

public sealed class StockGpuTelemetryTests
{
    [Fact]
    public void ReadsUtilizationAndTemperatureFromSameExactHandleWithStatuses()
    {
        var api = new Api();
        var handles = new List<nint>();
        var value = StockGpuTelemetryReader.Read(api,
            handle => { handles.Add(handle); return (0, 100); },
            handle => { handles.Add(handle); return (0, 65); });
        Assert.Equal(new nint[] { 42, 42 }, handles);
        Assert.True(value.ExactDeviceConfirmed);
        Assert.Null(value.Failure);
        Assert.Equal(100u, value.GpuUtilizationPercent);
        Assert.Equal(65, value.GpuCelsius);
        Assert.Equal(0, value.NativeStatuses["Utilization"]);
        Assert.Equal(0, value.NativeStatuses["Temperature"]);
        Assert.True(value.CompletedAtUtc >= value.StartedAtUtc);
        Assert.Equal(1, api.Shutdowns);
    }

    [Theory]
    [InlineData(3, 65)]
    [InlineData(0, 0)]
    [InlineData(0, 126)]
    public void NativeFailureOrInvalidTemperatureIsNeverAdmitted(int status, uint temperature)
    {
        var value = StockGpuTelemetryReader.Read(new Api(), _ => (0, 100), _ => (status, temperature));
        Assert.Equal("NvmlTemperatureUnavailableOrInvalid", value.Failure);
        Assert.Null(value.GpuCelsius);
        Assert.Equal(status, value.NativeStatuses["Temperature"]);
    }

    [Theory]
    [InlineData(3, 100)]
    [InlineData(0, 101)]
    public void FailedOrInvalidUtilizationRemainsAReadFailure(int status, uint load)
    {
        var value = StockGpuTelemetryReader.Read(new Api(), _ => (status, load), _ => (0, 65));
        Assert.Equal("NvmlUtilizationUnavailableOrInvalid", value.Failure);
        Assert.Null(value.GpuUtilizationPercent);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public void WrongCountOrNameNeverReachesSensorCalls(uint count, bool nameMatches)
    {
        var api = new Api { Count = count, NameMatches = nameMatches };
        var value = StockGpuTelemetryReader.Read(api, _ => throw new InvalidOperationException("Must not query"),
            _ => throw new InvalidOperationException("Must not query"));
        Assert.False(value.ExactDeviceConfirmed);
        Assert.DoesNotContain("Utilization", value.NativeStatuses.Keys);
        Assert.DoesNotContain("Temperature", value.NativeStatuses.Keys);
        Assert.Equal(1, api.Shutdowns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task FalseOrUnknownWddmActivityCannotMaskFreshNativeTemperature(bool? activity)
    {
        var cpu = new CpuSource(activity);
        var native = StockGpuTelemetryReader.Read(new Api(), _ => (0, 100), _ => (0, 65));
        var value = await Rz090528StockLoadBaselineCommand.ReadTelemetryAsync(cpu, () => native, CancellationToken.None);
        Assert.Equal(45, value.CpuCelsius);
        Assert.Equal(65, value.Gpu.GpuCelsius);
        Assert.Equal(0, cpu.ActivityQueries);
        Assert.False(cpu.IncludeGpu);
    }

    private sealed class CpuSource(bool? activity) : IFanTemperatureSource
    {
        internal int ActivityQueries;
        internal bool IncludeGpu;
        public ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken cancellationToken)
        {
            ActivityQueries++;
            return ValueTask.FromResult(new FanTemperatureReadings(45, null, activity is false));
        }
        public ValueTask<FanTemperatureReadings> ReadAsync(bool includeCpu, bool includeGpu, CancellationToken cancellationToken)
        {
            IncludeGpu = includeGpu;
            return includeGpu ? ReadAsync(cancellationToken) : ValueTask.FromResult(new FanTemperatureReadings(45, null));
        }
    }

    private sealed class Api : INvidiaNvmlGpuClockOffsetValidationApi
    {
        internal uint Count = 1;
        internal bool NameMatches = true;
        internal int Shutdowns;
        public int Initialize() => 0;
        public int Shutdown() { Shutdowns++; return 0; }
        public int GetDeviceCount(out uint count) { count = Count; return 0; }
        public int GetDeviceHandleByIndex(uint index, out nint device) { device = 42; return 0; }
        public int GetDeviceName(nint device, StringBuilder name, uint length)
        {
            name.Append(NameMatches ? GpuClockOffsetValidationPlan.Rz090528UiStep.ExpectedDeviceName : "Other GPU");
            return 0;
        }
        public int GetClockOffsets(nint device, ref NvmlValidationClockOffset info) => throw new InvalidOperationException("No offset query needed here.");
        public int SetClockOffsets(nint device, ref NvmlValidationClockOffset info) => throw new InvalidOperationException("No writes allowed.");
    }
}
