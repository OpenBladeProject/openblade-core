using OpenBlade.CpuTemperature;
using OpenBlade.Platform;

namespace OpenBlade.CpuTemperature.Tests;

public sealed class CpuTemperatureTests
{
    [Fact]
    public void DistanceAndAverageAreNotTemperaturesForStop() =>
        Assert.Null(CpuTemperatureSelection.Select([new(CpuThermalChannelKind.Other, 40)]));

    [Fact]
    public void PackageCoversMissingCoreButHottestMeasuredCoreStillWins()
    {
        Assert.Equal(55000, CpuTemperatureSelection.Select([Package(50), Core(null), Core(55)]));
        Assert.Equal(50000, CpuTemperatureSelection.Select([Package(50), Core(null)]));
    }

    [Fact]
    public void MissingPackageRequiresEveryExposedCore()
    {
        Assert.Null(CpuTemperatureSelection.Select([Package(null), Core(40), Core(null)]));
        Assert.Equal(50000, CpuTemperatureSelection.Select([Package(null), Core(40), Core(50)]));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(126)]
    public void InvalidMeasuredValueCannotBecomeCool(float temperature) =>
        Assert.Null(CpuTemperatureSelection.Select([Package(40), Core(temperature)]));

    [Fact]
    public void FailedOrAlteredIntelCalibrationIsRejected()
    {
        Assert.Null(CpuTemperatureSelection.Select([new(CpuThermalChannelKind.IntelPackage, 40, 0, 1)]));
        Assert.Null(CpuTemperatureSelection.Select([new(CpuThermalChannelKind.IntelCore, 40, 100, 0.5f)]));
    }

    [Fact]
    public void AmdUsesCorrectedDieRatherThanOffsetControlValue()
    {
        Assert.Equal(45000, CpuTemperatureSelection.Select([
            new(CpuThermalChannelKind.AmdDie, 45), new(CpuThermalChannelKind.AmdControl, 65)]));
        Assert.Null(CpuTemperatureSelection.Select([
            new(CpuThermalChannelKind.AmdDie, null), new(CpuThermalChannelKind.AmdControl, 65)]));
    }

    [Fact]
    public async Task NoHardwareOperationBeforeACompleteValidRequest()
    {
        int opened = 0;
        int verified = 0;
        using var output = new MemoryStream();
        Assert.Equal(0, await CpuTemperatureWorker.RunAsync(new MemoryStream(), output,
            () => { opened++; return new FakeSession(); }, () => verified++));
        Assert.Equal(3, await CpuTemperatureWorker.RunAsync(new MemoryStream(new byte[8]), output,
            () => { opened++; return new FakeSession(); }, () => verified++));
        Assert.Equal(0, opened);
        Assert.Equal(0, verified);
    }

    [Fact]
    public async Task ReadFailureEmitsUnavailableAndClosesWithoutReusingSuccess()
    {
        var session = new FakeSession { FailAfter = 1 };
        byte[] requests = [.. CpuTemperatureProtocol.EncodeRequest(1), .. CpuTemperatureProtocol.EncodeRequest(2)];
        using var output = new MemoryStream();
        Assert.Equal(4, await CpuTemperatureWorker.RunAsync(new MemoryStream(requests), output, () => session, () => { }));
        byte[] replies = output.ToArray();
        Assert.True(CpuTemperatureProtocol.TryDecodeReply(replies.AsSpan(0, 20), 1, out int? first));
        Assert.Equal(46, first);
        Assert.True(CpuTemperatureProtocol.TryDecodeReply(replies.AsSpan(20, 20), 2, out int? second));
        Assert.Null(second);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task TrustFailureDoesNotOpenLibrary()
    {
        using var output = new MemoryStream();
        int opened = 0;
        int result = await CpuTemperatureWorker.RunAsync(new MemoryStream(CpuTemperatureProtocol.EncodeRequest(1)),
            output, () => { opened++; return new FakeSession(); }, () => throw new IOException("Untrusted driver"));
        Assert.Equal(4, result);
        Assert.Equal(0, opened);
        Assert.True(CpuTemperatureProtocol.TryDecodeReply(output.ToArray(), 1, out int? temperature));
        Assert.Null(temperature);
    }

    private static CpuThermalChannel Package(float? value) => new(CpuThermalChannelKind.IntelPackage, value, 100, 1);
    private static CpuThermalChannel Core(float? value) => new(CpuThermalChannelKind.IntelCore, value, 100, 1);
    private sealed class FakeSession : ICpuTemperatureSession
    {
        public int FailAfter { get; init; } = int.MaxValue;
        public bool Disposed { get; private set; }
        private int reads;
        public int? ReadMilliCelsius() => ++reads > FailAfter ? throw new IOException("failed") : 45123;
        public void Dispose() => Disposed = true;
    }
}
