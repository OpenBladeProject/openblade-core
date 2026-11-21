using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class FanCurveTemperatureFilterTests
{
    [Fact]
    public void FirstSampleSeedsEachSensorUnchanged()
    {
        var (filter, _) = Create();
        Assert.Equal(new FanTemperatureReadings(62, 48, true, false), filter.Filter(new(62, 48, true, false)));
    }

    [Fact]
    public void BriefSpikeMovesTheOutputOnlyAFewDegrees()
    {
        var (filter, clock) = Create();
        filter.Filter(new(60, 50));
        clock.Advance(1);
        // One 1 s sample of +30 C moves a 5 s exponential filter by 30 * (1 - e^-0.2) = 5.4 C.
        Assert.Equal(65, filter.Filter(new(90, 50)).CpuCelsius);
        clock.Advance(1);
        Assert.Equal(70, filter.Filter(new(90, 50)).CpuCelsius);
        clock.Advance(1);
        // The falling reading stays inside the 3 C deadband, so the output holds.
        Assert.Equal(70, filter.Filter(new(60, 50)).CpuCelsius);
        // A stepped curve with its next step above 75 C never sees this spike.
        ManualFanCurve curve = Stepped();
        var raw = FanCurveCalculator.Calculate(curve, new(90, 50));
        var filtered = FanCurveCalculator.Calculate(curve, new(70, 50));
        Assert.NotEqual(raw, filtered);
        Assert.Equal(FanCurveCalculator.Calculate(curve, new(60, 50)), filtered);
    }

    [Fact]
    public void SustainedRiseIsFollowedWithinFifteenSeconds()
    {
        var (filter, clock) = Create();
        filter.Filter(new(50, 40));
        FanTemperatureReadings output = default;
        for (int second = 0; second < 15; second++)
        {
            clock.Advance(1);
            output = filter.Filter(new(70, 60));
        }
        Assert.Equal(69, output.CpuCelsius);
        Assert.Equal(59, output.GpuCelsius);
    }

    [Fact]
    public void FallingTemperaturesHoldUntilTheyDropPastTheDeadband()
    {
        var (filter, clock) = Create();
        filter.Filter(new(80, 60));
        // A 2 C fall stays inside the deadband however long it lasts.
        for (int second = 0; second < 30; second++)
        {
            clock.Advance(1);
            Assert.Equal(80, filter.Filter(new(78, 60)).CpuCelsius);
        }
        var outputs = new List<int>();
        for (int second = 0; second < 30; second++)
        {
            clock.Advance(1);
            outputs.Add(filter.Filter(new(60, 60)).CpuCelsius!.Value);
        }
        // Past the deadband the output tracks smoothed + 3 C, never rising on the way down.
        Assert.True(outputs.Zip(outputs.Skip(1)).All(pair => pair.Second <= pair.First));
        Assert.Equal(63, outputs[^1]);
        // A small rise inside the deadband does not raise the held value.
        clock.Advance(1);
        Assert.Equal(63, filter.Filter(new(62, 60)).CpuCelsius);
    }

    [Fact]
    public void GapMissingSensorAndResetSeedFromTheNextRawSample()
    {
        var (filter, clock) = Create();
        filter.Filter(new(50, 50));
        clock.Advance(11);
        Assert.Equal(new FanTemperatureReadings(90, 90), filter.Filter(new(90, 90)));

        clock.Advance(1);
        Assert.Equal(new FanTemperatureReadings(90, null, false, true), filter.Filter(new(90, null, false, true)));
        clock.Advance(1);
        // The returning GPU seeds from its raw value; the CPU keeps its history.
        FanTemperatureReadings returned = filter.Filter(new(50, 40));
        Assert.Equal(40, returned.GpuCelsius);
        Assert.Equal(86, returned.CpuCelsius);

        filter.Reset();
        clock.Advance(1);
        Assert.Equal(new FanTemperatureReadings(50, 40), filter.Filter(new(50, 40)));
    }

    [Fact]
    public void ManualCurveKeepsFailSafeOnRawReadings()
    {
        ManualFanCurve curve = Stepped();
        FanSpeedRange range = FanSpeedRange.Standard;
        FanTemperatureReadings quiet = new(60, 50);

        Assert.True(ServiceControlRuntime.CalculateManualFanCurveTargets(curve, new(95, 50), quiet, range).FailSafe);
        Assert.True(ServiceControlRuntime.CalculateManualFanCurveTargets(curve, new(60, 87), quiet, range).FailSafe);
        Assert.True(ServiceControlRuntime.CalculateManualFanCurveTargets(curve, new(null, 50), quiet, range).FailSafe);
        Assert.True(ServiceControlRuntime.CalculateManualFanCurveTargets(curve, new(60, null), quiet, range).FailSafe);
        // Confirmed GPU power-down substitutes the filtered CPU reading for the GPU.
        FanTargetSpeeds poweredDown = ServiceControlRuntime.CalculateManualFanCurveTargets(
            curve, new(80, null, false, true), new(60, null, false, true), range);
        Assert.Equal(FanCurveCalculator.Calculate(curve, new(60, 60), range), poweredDown);
        Assert.Equal(FanCurveCalculator.Calculate(curve, quiet, range),
            ServiceControlRuntime.CalculateManualFanCurveTargets(curve, new(90, 50), quiet, range));
    }

    private static ManualFanCurve Stepped() => new(
        [new(40, 2000), new(75, 2000), new(80, 4000)],
        [new(40, 2000), new(75, 2000), new(80, 4000)],
        useHigherTemperatureForBoth: false);

    private static (FanCurveTemperatureFilter Filter, ManualClock Clock) Create()
    {
        var clock = new ManualClock();
        return (new FanCurveTemperatureFilter(clock), clock);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        internal void Advance(double seconds) => ticks += (long)(seconds * TimeSpan.TicksPerSecond);
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}
