using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class FanControlPresentationTests
{
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
}
