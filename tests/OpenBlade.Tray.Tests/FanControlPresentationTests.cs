using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class FanControlPresentationTests
{
    [Fact]
    public void ModeOptions_PreserveItemIdentityAcrossPresentationRefreshes()
    {
        var admitted = new HashSet<FanMode>
        {
            FanMode.Automatic,
            FanMode.Fixed,
            FanMode.ManualCurve,
        };

        IReadOnlyList<FanModeOption> first = FanModePresentation.CreateOptions(admitted);
        IReadOnlyList<FanModeOption> second = FanModePresentation.CreateOptions(admitted);

        Assert.All(first, option => Assert.Same(
            option,
            second.Single(candidate => candidate.Value == option.Value)));
    }

    [Fact]
    public void CurvePoint_SliderValueUpdatesVisibleLabelAndQuantizesFirmwareStep()
    {
        var point = new FanCurveEditorPoint(new FanCurvePoint(60, 2_600));
        List<string?> changed = [];
        point.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        point.SpeedRpm = 3_447;

        Assert.Equal(3_400, point.SpeedRpm);
        Assert.Equal("3,400 RPM", point.SpeedLabel);
        Assert.Contains(nameof(FanCurveEditorPoint.SpeedRpm), changed);
        Assert.Contains(nameof(FanCurveEditorPoint.SpeedLabel), changed);
    }
    [Theory]
    [InlineData(4300, 5500, 5500)]
    [InlineData(3700, 5300, 5300)]
    public void CurvePoint_UsesReviewedIntentBoundsForSliderAndSavedValue(
        int minimum, int maximum, int expected)
    {
        var range = new FanSpeedRange(minimum, maximum, 100);
        var point = new FanCurveEditorPoint(new FanCurvePoint(90, maximum), range);

        point.SpeedRpm = 5500;

        Assert.Equal(minimum, point.MinimumRpm);
        Assert.Equal(maximum, point.MaximumRpm);
        Assert.Equal(100, point.StepRpm);
        Assert.Equal(expected, point.SpeedRpm);
        Assert.Equal(expected, point.ToValue().SpeedRpm);
    }
}
