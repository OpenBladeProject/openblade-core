using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class FanControlEditorStateTests
{
    [Theory]
    [InlineData(1900, 2000)]
    [InlineData(2349, 2300)]
    [InlineData(2350, 2400)]
    [InlineData(5499, 5000)]
    public void FixedSpeed_UsesDeviceBoundsAndNearestHundred(int requested, int expected)
    {
        var editor = new FanControlEditorState();
        var range = new FanSpeedRange(2000, 5000, 100);

        Assert.Equal(FanControlEditorChanges.FixedSpeed, editor.SetFixedSpeed(requested, range));
        Assert.Equal(expected, editor.FixedSpeedRpm);
        Assert.Equal(FanControlEditorChanges.None, editor.SetFixedSpeed(expected + 1, range));
    }

    [Fact]
    public void Load_ClampsBothCurvesAndPreservesIdentityOnEquivalentRefresh()
    {
        var editor = new FanControlEditorState();
        var range = new FanSpeedRange(2300, 5000, 100);
        var settings = new FanControlSettings(FanMode.ManualCurve, 5400, ManualFanCurve.Default);

        Assert.Equal(
            FanControlEditorChanges.FixedSpeed | FanControlEditorChanges.CpuPoints
                | FanControlEditorChanges.GpuPoints,
            editor.Load(settings, range));
        FanControlSettings normalized = editor.ToSettings(FanMode.ManualCurve);
        Assert.Equal(5000, normalized.FixedSpeedRpm);
        Assert.Equal([2300, 2300, 2600, 3400, 4400, 5000],
            normalized.ManualCurve.Cpu.Select(point => point.SpeedRpm));
        Assert.Equal([2300, 2300, 2700, 3600, 4500, 5000],
            normalized.ManualCurve.Gpu.Select(point => point.SpeedRpm));
        IReadOnlyList<FanCurveEditorPoint> cpu = editor.CpuPoints;
        IReadOnlyList<FanCurveEditorPoint> gpu = editor.GpuPoints;

        Assert.Equal(FanControlEditorChanges.None, editor.Load(normalized, range));
        Assert.Equal(FanControlEditorChanges.None, editor.Load(settings, range));
        Assert.Same(cpu, editor.CpuPoints);
        Assert.Same(gpu, editor.GpuPoints);
        Assert.Same(cpu[0], editor.CpuPoints[0]);
        Assert.Same(gpu[0], editor.GpuPoints[0]);

        editor.Load(settings, FanSpeedRange.Standard);
        Assert.Equal(settings, editor.ToSettings(FanMode.ManualCurve));
        Assert.NotSame(cpu, editor.CpuPoints);
        Assert.NotSame(gpu, editor.GpuPoints);
    }

    [Theory]
    [InlineData(true, 1900, 2100)]
    [InlineData(true, 5400, 3400)]
    [InlineData(true, 2750, 2800)]
    [InlineData(false, 1900, 2100)]
    [InlineData(false, 5400, 3600)]
    [InlineData(false, 2750, 2800)]
    public void PointEdit_PreservesMonotonicSpeedsAndUneditedSide(
        bool cpu,
        int requested,
        int expected)
    {
        var editor = new FanControlEditorState();
        FanControlSettings before = editor.ToSettings(FanMode.ManualCurve);
        IReadOnlyList<FanCurveEditorPoint> edited = cpu ? editor.CpuPoints : editor.GpuPoints;
        IReadOnlyList<FanCurveEditorPoint> untouched = cpu ? editor.GpuPoints : editor.CpuPoints;

        Assert.Equal(cpu ? FanControlEditorChanges.CpuPoints : FanControlEditorChanges.GpuPoints,
            editor.UpdatePoint(edited[2], requested, FanSpeedRange.Standard));

        IReadOnlyList<FanCurveEditorPoint> updated = cpu ? editor.CpuPoints : editor.GpuPoints;
        Assert.Equal(expected, updated[2].SpeedRpm);
        Assert.NotSame(edited, updated);
        Assert.Same(untouched, cpu ? editor.GpuPoints : editor.CpuPoints);
        Assert.Equal(edited.Select(point => point.TemperatureCelsius),
            updated.Select(point => point.TemperatureCelsius));
        Assert.True(updated.Zip(updated.Skip(1), (left, right) => left.SpeedRpm <= right.SpeedRpm).All(value => value));
        Assert.Equal(ManualFanCurve.Default, before.ManualCurve);
        Assert.NotEqual(before, editor.ToSettings(FanMode.ManualCurve));
    }

    [Fact]
    public void PointEdit_RepublishesClampedValueAndRejectsStaleOrForeignPoints()
    {
        var editor = new FanControlEditorState();
        var range = new FanSpeedRange(2000, 5000, 100);
        editor.Load(FanControlSettings.Default, range);
        IReadOnlyList<FanCurveEditorPoint> original = editor.CpuPoints;
        FanCurveEditorPoint stale = original[^1];

        Assert.Equal(FanControlEditorChanges.CpuPoints, editor.UpdatePoint(stale, 5400, range));
        Assert.Equal(5000, editor.CpuPoints[^1].SpeedRpm);
        Assert.NotSame(original, editor.CpuPoints);
        FanControlSettings snapshot = editor.ToSettings(FanMode.Fixed);
        Assert.Throws<ArgumentException>(() => editor.UpdatePoint(stale, 4400, range));
        Assert.Throws<ArgumentException>(() => editor.UpdatePoint(
            new FanCurveEditorPoint(editor.CpuPoints[0].ToValue()), 2200, range));
        Assert.Throws<ArgumentNullException>(() => editor.UpdatePoint(null!, 2200, range));
        Assert.Equal(snapshot, editor.ToSettings(FanMode.Fixed));
    }

    [Fact]
    public void Load_RecognizesTemperatureAndCountChangesOnOnlyTheChangedSide()
    {
        var editor = new FanControlEditorState();
        IReadOnlyList<FanCurveEditorPoint> gpu = editor.GpuPoints;
        FanCurvePoint[] moved = ManualFanCurve.Default.Cpu
            .Select((point, index) => index == 0 ? new FanCurvePoint(39, point.SpeedRpm) : point)
            .ToArray();
        var settings = new FanControlSettings(FanMode.ManualCurve, 3700,
            new ManualFanCurve(moved, ManualFanCurve.Default.Gpu, true));

        Assert.Equal(FanControlEditorChanges.CpuPoints, editor.Load(settings, FanSpeedRange.Standard));
        Assert.Equal(39, editor.CpuPoints[0].TemperatureCelsius);
        Assert.Same(gpu, editor.GpuPoints);

        var fewer = new FanControlSettings(FanMode.ManualCurve, 3700,
            new ManualFanCurve([moved[0], moved[^1]], ManualFanCurve.Default.Gpu, true));
        Assert.Equal(FanControlEditorChanges.CpuPoints, editor.Load(fewer, FanSpeedRange.Standard));
        Assert.Equal(2, editor.CpuPoints.Count);
        Assert.Same(gpu, editor.GpuPoints);
    }

    [Fact]
    public void ScalarAndModeChanges_DoNotReplaceCurvePointsOrMutateSavedSnapshots()
    {
        var editor = new FanControlEditorState();
        IReadOnlyList<FanCurveEditorPoint> cpu = editor.CpuPoints;
        IReadOnlyList<FanCurveEditorPoint> gpu = editor.GpuPoints;
        FanControlSettings saved = editor.ToSettings(FanMode.Automatic);

        Assert.Equal(FanControlEditorChanges.UseHigherTemperature, editor.SetUseHigherTemperature(false));
        Assert.Equal(FanControlEditorChanges.None, editor.SetUseHigherTemperature(false));
        Assert.False(editor.ToSettings(FanMode.Fixed).ManualCurve.UseHigherTemperatureForBoth);
        Assert.True(saved.ManualCurve.UseHigherTemperatureForBoth);
        Assert.Equal(FanControlEditorChanges.UseHigherTemperature, editor.Load(saved, FanSpeedRange.Standard));
        Assert.Equal(saved, editor.ToSettings(FanMode.Automatic));
        Assert.Equal(FanMode.ManualCurve, editor.ToSettings(FanMode.ManualCurve).Mode);
        Assert.Same(cpu, editor.CpuPoints);
        Assert.Same(gpu, editor.GpuPoints);
    }
}
