using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadCurvePreviewCommandTests
{
    private static string[] Arguments(int count = 1) => ["--confirm-target",
        CoolingPadCurvePreviewCommand.ExactTargetConfirmation, "--samples", count.ToString()];
    private static HostIdentityReadResult ValidHost() => new(
        new("Razer", "Blade 16 - RZ09-0528", "RZ09-05289EN4", "future-provenance"), null);

    [Theory]
    [InlineData("0")]
    [InlineData("31")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("999999999999")]
    public async Task BadArgumentsNeverReadHostOrConstructTelemetry(string count)
    {
        var command = new CoolingPadCurvePreviewCommand(() => throw new Exception(),
            () => throw new Exception(), new StringWriter());
        var args = Arguments();
        args[3] = count;
        Assert.Equal(1, await command.RunAsync(args, default));
        args = Arguments();
        args[1] = "RZ09-0581";
        Assert.Equal(1, await command.RunAsync(args, default));
        Assert.Equal(1, await command.RunAsync([.. Arguments(), "--write"], default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnsupportedOrMissingHostPreventsTelemetryConstruction(bool wrongSku)
    {
        int opened = 0;
        var output = new StringWriter();
        var command = new CoolingPadCurvePreviewCommand(() => wrongSku
            ? new(new("Razer", "Blade 16 - RZ09-0528", "different", "2.02"), null)
            : new(null, HidSessionFailureKind.IdentityBlocked),
            () => { opened++; throw new Exception(); }, output);
        Assert.Equal(2, await command.RunAsync(Arguments(), default));
        Assert.Equal(0, opened);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("HostIdentityBlocked", json.RootElement.GetProperty("failure").GetString());
        Assert.False(json.RootElement.GetProperty("temperatureSourceConstructionStarted").GetBoolean());
    }

    [Fact]
    public async Task FinitePreviewChecksHostFirst_IncludesReadLatency_AndDisposesTelemetry()
    {
        var clock = new Clock();
        var events = new List<string>();
        var source = new Source(token =>
        {
            events.Add("read");
            clock.Advance(TimeSpan.FromSeconds(1));
            return ValueTask.FromResult(new FanTemperatureReadings(50, 50));
        });
        var output = new StringWriter();
        var command = new CoolingPadCurvePreviewCommand(() => { events.Add("host"); return ValidHost(); },
            () => { events.Add("open"); return source; }, output, clock,
            (duration, token) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(2), duration);
                events.Add("delay");
                clock.Advance(duration);
                return Task.CompletedTask;
            });
        Assert.Equal(0, await command.RunAsync(Arguments(3), default));
        Assert.Equal(new[] { "host", "open", "read", "delay", "read", "delay", "read" }, events);
        Assert.Equal(1, source.Disposals);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.True(root.GetProperty("readOnly").GetBoolean());
        Assert.True(root.GetProperty("completed").GetBoolean());
        Assert.True(root.GetProperty("temperatureSourceDisposed").GetBoolean());
        Assert.Equal("future-provenance", root.GetProperty("biosVersion").GetString());
        Assert.Equal("illustrative-OpenBlade-planning-policy", root.GetProperty("preset").GetString());
        foreach (string name in new[] { "padControlReportsSent", "padPresenceChecked", "productionAdmitted",
            "measuredRpmConfirmed", "actualTemperatureControlPerformed", "safeHandbackPerformed",
            "sourceOriginTimestampAvailable", "synapsePreset", "savedUserIntent", "recommendedForProduction" })
        { Assert.False(root.GetProperty(name).GetBoolean()); }
        Assert.Equal(3, root.GetProperty("samples").GetArrayLength());
        foreach (var sample in root.GetProperty("samples").EnumerateArray())
        {
            Assert.Equal(1000, sample.GetProperty("hostAcquisitionAgeMilliseconds").GetDouble());
            Assert.Equal(1500, sample.GetProperty("plannedConfiguredDemandRpm").GetInt32());
            Assert.Equal("DemandPlanned", sample.GetProperty("reason").GetString());
        }
    }

    [Theory]
    [InlineData(false, "MissingActiveGpu")]
    [InlineData(true, "StaleSample")]
    public async Task PlannerStopHasNoDemandAndNoFutureReadOrDelay(bool stale, string reason)
    {
        var clock = new Clock();
        var source = new Source(_ =>
        {
            if (stale) { clock.Advance(TimeSpan.FromSeconds(5)); }
            return ValueTask.FromResult(new FanTemperatureReadings(50, stale ? 50 : null));
        });
        var output = new StringWriter();
        var command = new CoolingPadCurvePreviewCommand(ValidHost, () => source, output, clock,
            (_, _) => throw new InvalidOperationException("No cadence after stop."));
        Assert.Equal(2, await command.RunAsync(Arguments(30), default));
        Assert.Equal(1, source.Reads);
        Assert.Equal(1, source.Disposals);
        using var json = JsonDocument.Parse(output.ToString());
        var sample = json.RootElement.GetProperty("samples")[0];
        Assert.Equal(reason, sample.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("plannedConfiguredDemandRpm").ValueKind);
    }

    [Fact]
    public async Task CancellationDuringReadDisposesAfterReadFinishes_AndNeverPlansLateSample()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<FanTemperatureReadings>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source(token =>
        {
            token.Register(() => notified.TrySetResult());
            entered.SetResult();
            return new ValueTask<FanTemperatureReadings>(late.Task);
        });
        var output = new StringWriter();
        var command = new CoolingPadCurvePreviewCommand(ValidHost, () => source, output);
        var running = command.RunAsync(Arguments(30), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, source.Disposals);
        late.SetResult(new(50, 50));
        Assert.Equal(4, await running.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, source.Reads);
        Assert.Equal(1, source.Disposals);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Empty(json.RootElement.GetProperty("samples").EnumerateArray());
        Assert.Equal("Canceled", json.RootElement.GetProperty("failure").GetString());
    }

    [Fact]
    public async Task AlreadyCanceledDoesNotReadHostOrConstructSource()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var command = new CoolingPadCurvePreviewCommand(() => throw new Exception(), () => throw new Exception(), new StringWriter());
        Assert.Equal(4, await command.RunAsync(Arguments(), cancellation.Token));
    }

    [Fact]
    public async Task TemperatureReadExceptionStopsAndDisposesWithoutPlanning()
    {
        var source = new Source(_ => throw new IOException());
        var output = new StringWriter();
        var command = new CoolingPadCurvePreviewCommand(ValidHost, () => source, output,
            delay: (_, _) => throw new InvalidOperationException("No cadence after failure."));
        Assert.Equal(2, await command.RunAsync(Arguments(30), default));
        Assert.Equal(1, source.Reads);
        Assert.Equal(1, source.Disposals);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Empty(json.RootElement.GetProperty("samples").EnumerateArray());
        Assert.Equal("ObservationFailed", json.RootElement.GetProperty("failure").GetString());
    }

    [Fact]
    public void PresetsAreExplicitAndCannotBeMutatedThroughTheirPointCollections()
    {
        var cpu = CoolingPadCurvePreviewCommand.CreateCpuPreset();
        var gpu = CoolingPadCurvePreviewCommand.CreateGpuPreset();
        Assert.Equal(new[] { (40, 1000), (90, 2200), (100, 3200) }, cpu.Points.Select(p => (p.TemperatureCelsius, p.Demand.Rpm)));
        Assert.Equal(new[] { (30, 1000), (80, 2200), (90, 3200) }, gpu.Points.Select(p => (p.TemperatureCelsius, p.Demand.Rpm)));
        if (cpu.Points is IList<CoolingPadCurvePoint> list)
        {
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list[0] = cpu.Points[1]);
        }
        Assert.Equal(1000, CoolingPadCurvePreviewCommand.CreateCpuPreset().Points[0].Demand.Rpm);
    }

    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref timestamp, amount.Ticks);
    }
    private sealed class Source(Func<CancellationToken, ValueTask<FanTemperatureReadings>> read) : IFanTemperatureSource, IDisposable
    {
        internal int Reads;
        internal int Disposals;
        public ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken token)
        { Interlocked.Increment(ref Reads); return read(token); }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
