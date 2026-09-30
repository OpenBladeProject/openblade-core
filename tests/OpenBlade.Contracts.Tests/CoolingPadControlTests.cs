namespace OpenBlade.Contracts.Tests;

public sealed class CoolingPadControlTests
{
    [Fact]
    public void CurvesCopyInputAndCompareByValue()
    {
        CoolingPadCurvePoint[] points = [new(40, new(1000)), new(90, new(2200))];
        var curve = new CoolingPadDemandCurve(points);
        var equivalent = new CoolingPadDemandCurve(points);
        points[0] = new(0, new(500));
        Assert.Equal(40, curve.Points[0].TemperatureCelsius);
        Assert.Equal(curve, equivalent);
        Assert.Equal(curve.GetHashCode(), equivalent.GetHashCode());
        Assert.Throws<NotSupportedException>(() => ((IList<CoolingPadCurvePoint>)curve.Points)[0] = points[0]);
    }

    [Fact]
    public void StopIsEmptyIntentAndDoesNotCarryAnAutoCommand()
    {
        Assert.Equal(CoolingPadControlMode.Stop, new CoolingPadControlSettings().Mode);
        Assert.Same(CoolingPadControlSettings.Stopped, CoolingPadControlSettings.Default);
        Assert.Null(CoolingPadControlSettings.Default.FixedDemand);
        Assert.Null(CoolingPadControlSettings.Default.CpuCurve);
        Assert.Null(CoolingPadControlSettings.Default.GpuCurve);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void UnknownModesAreRejected(int mode) =>
        Assert.Throws<ArgumentException>(() => new CoolingPadControlSettings((CoolingPadControlMode)mode));

    [Fact]
    public void InvalidModeShapesAndDefaultDemandAreRejected()
    {
        var curve = new CoolingPadDemandCurve([new(40, new(1000)), new(90, new(2200))]);
        Assert.Throws<ArgumentException>(() => new CoolingPadControlSettings(CoolingPadControlMode.Stop, new(1500)));
        Assert.Throws<ArgumentException>(() => new CoolingPadControlSettings(CoolingPadControlMode.FixedDemand));
        Assert.Throws<ArgumentException>(() => new CoolingPadControlSettings(CoolingPadControlMode.FixedDemand, default(CoolingPadConfiguredRpmDemand)));
        Assert.Throws<ArgumentException>(() => new CoolingPadControlSettings(CoolingPadControlMode.FixedDemand, new(1500), curve));
        Assert.Throws<ArgumentException>(() => new CoolingPadControlSettings(CoolingPadControlMode.HostCurve, cpuCurve: curve));
        Assert.Throws<ArgumentException>(() => new CoolingPadControlSettings(CoolingPadControlMode.HostCurve, new(1500), curve, curve));
    }

    [Fact]
    public void CurvesRejectInvalidPointsAndExcessiveEnumeration()
    {
        Assert.Throws<ArgumentException>(() => new CoolingPadDemandCurve([new(40, default), new(90, new(2200))]));
        Assert.Throws<ArgumentException>(() => new CoolingPadDemandCurve([new(-1, new(1000)), new(90, new(2200))]));
        Assert.Throws<ArgumentException>(() => new CoolingPadDemandCurve([new(40, new(1000)), new(40, new(2200))]));
        Assert.Throws<ArgumentException>(() => new CoolingPadDemandCurve([new(40, new(2200)), new(90, new(1000))]));
        int enumerated = 0;
        IEnumerable<CoolingPadCurvePoint> Endless()
        {
            while (true) { enumerated++; yield return new(40, new(1000)); }
        }
        Assert.Throws<ArgumentException>(() => new CoolingPadDemandCurve(Endless()));
        Assert.Equal(11, enumerated);
    }
}
