using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class CoolingPadCurvePlannerTests
{
    private static CoolingPadDemandCurve Curve(params (int Temperature, int Rpm)[] points) =>
        new(points.Select(p => new CoolingPadCurvePoint(p.Temperature, new(p.Rpm))));

    private static CoolingPadCurveDecision Plan(CoolingPadDemandCurve cpu, CoolingPadDemandCurve gpu,
        FanTemperatureReadings readings, TimeSpan? age = null, bool stop = false) =>
        CoolingPadCurvePlanner.Plan(cpu, gpu, readings, age ?? TimeSpan.Zero, TimeSpan.FromSeconds(5), stop);

    [Theory]
    [InlineData(0, 500)]
    [InlineData(40, 500)]
    [InlineData(41, 550)]
    [InlineData(42, 550)]
    [InlineData(43, 600)]
    [InlineData(44, 600)]
    [InlineData(115, 600)]
    public void Interpolation_ClampsAndQuantizesUpwardWithoutFloatingPoint(int temperature, int rpm)
    {
        var curve = Curve((40, 500), (44, 600));
        var decision = Plan(curve, curve, new(temperature, null, GpuInactive: true));
        Assert.Equal(rpm, decision.Demand!.Value.Rpm);
        Assert.Equal(CoolingPadCurveDecisionReason.DemandPlanned, decision.Reason);
    }

    [Fact]
    public void SeparateCurves_SelectLargerDemandRatherThanHotterTemperature()
    {
        var cpu = Curve((0, 500), (115, 500));
        var gpu = Curve((0, 2200), (115, 2200));
        Assert.Equal(2200, Plan(cpu, gpu, new(100, 40)).Demand!.Value.Rpm);
        Assert.Equal(500, Plan(cpu, gpu, new(100, 999, GpuInactive: true)).Demand!.Value.Rpm);
    }

    [Fact]
    public void Curve_CopiesMutableInputsAndExposesReadOnlyPoints()
    {
        var input = new[] { new CoolingPadCurvePoint(0, new(500)), new CoolingPadCurvePoint(115, new(2200)) };
        var curve = new CoolingPadDemandCurve(input);
        input[1] = new(115, new(3200));
        Assert.Equal(2200, Plan(curve, curve, new(115, null, true)).Demand!.Value.Rpm);
        Assert.Throws<NotSupportedException>(() => ((IList<CoolingPadCurvePoint>)curve.Points)[0] = input[1]);
    }

    [Fact]
    public void Curve_RejectsMalformedInputsIncludingDefaultDemand()
    {
        Assert.Throws<ArgumentNullException>(() => new CoolingPadDemandCurve(null!));
        Assert.Throws<ArgumentException>(() => Curve((0, 500)));
        Assert.Throws<ArgumentException>(() => Curve(Enumerable.Range(0, 11).Select(t => (t, 500)).ToArray()));
        Assert.Throws<ArgumentException>(() => Curve((-1, 500), (115, 2200)));
        Assert.Throws<ArgumentException>(() => Curve((0, 500), (116, 2200)));
        Assert.Throws<ArgumentException>(() => Curve((40, 500), (40, 2200)));
        Assert.Throws<ArgumentException>(() => Curve((80, 500), (40, 2200)));
        Assert.Throws<ArgumentException>(() => Curve((0, 2200), (115, 500)));
        Assert.Throws<ArgumentException>(() => new CoolingPadDemandCurve([new(0, default), new(115, new(2200))]));
    }

    [Theory]
    [InlineData(null, 50, false, CoolingPadCurveDecisionReason.MissingCpu)]
    [InlineData(-1, 50, false, CoolingPadCurveDecisionReason.InvalidCpu)]
    [InlineData(116, 50, false, CoolingPadCurveDecisionReason.InvalidCpu)]
    [InlineData(50, null, false, CoolingPadCurveDecisionReason.MissingActiveGpu)]
    [InlineData(50, -1, false, CoolingPadCurveDecisionReason.InvalidActiveGpu)]
    [InlineData(50, 116, false, CoolingPadCurveDecisionReason.InvalidActiveGpu)]
    public void InvalidOrMissingRequiredSensor_ReturnsNoDemand(int? cpu, int? gpu, bool inactive,
        CoolingPadCurveDecisionReason reason)
    {
        var curve = Curve((0, 500), (115, 3200));
        var decision = Plan(curve, curve, new(cpu, gpu, inactive));
        Assert.Null(decision.Demand);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void AgeAndStop_ReturnNoDemandWithoutInventingZeroOrHandback()
    {
        var curve = Curve((0, 500), (115, 3200));
        foreach (var (age, stop, reason) in new[] {
            (TimeSpan.FromTicks(-1), false, CoolingPadCurveDecisionReason.InvalidSampleAge),
            (TimeSpan.FromSeconds(5), false, CoolingPadCurveDecisionReason.StaleSample),
            (TimeSpan.FromSeconds(6), false, CoolingPadCurveDecisionReason.StaleSample),
            (TimeSpan.Zero, true, CoolingPadCurveDecisionReason.Stopped) })
        {
            var decision = Plan(curve, curve, new(50, 50), age, stop);
            Assert.Null(decision.Demand);
            Assert.Equal(reason, decision.Reason);
        }
        Assert.NotNull(Plan(curve, curve, new(50, 50), TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1)).Demand);
        Assert.Throws<ArgumentOutOfRangeException>(() => CoolingPadCurvePlanner.Plan(
            curve, curve, new(50, 50), TimeSpan.Zero, TimeSpan.Zero));
    }
}
