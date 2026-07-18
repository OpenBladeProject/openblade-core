using OpenBlade.Display;

namespace OpenBlade.Display.Tests;

public sealed class RefreshRateClassifierTests
{
    private static readonly DisplayRefreshOption[] Options =
    [
        new(DisplayRefreshKeys.Fixed(60), "60 Hz", 60, 60, 60, false),
        new(DisplayRefreshKeys.Fixed(120), "120 Hz", 120, 120, 120, false),
        new(DisplayRefreshKeys.Fixed(240), "240 Hz", 240, 240, 240, false),
        new(DisplayRefreshKeys.Dynamic(48, 240), "Dynamic (48-240 Hz)", 48, 240, 60, true),
    ];

    [Fact]
    public void Classify_ReportsDynamicModeWhenBoostFlagIsPresent()
    {
        string? key = RefreshRateClassifier.Classify(
            WindowsDisplayNative.DisplayConfigPathBoostRefreshRate,
            60,
            239.96,
            Options);

        Assert.Equal(DisplayRefreshKeys.Dynamic(48, 240), key);
    }

    [Theory]
    [InlineData(59.94, 60)]
    [InlineData(60, 60)]
    [InlineData(119.88, 120)]
    [InlineData(239.96, 240)]
    [InlineData(240, 240)]
    public void Classify_ReportsDriverEnumeratedFixedRate(double hertz, int expectedHertz)
    {
        string? key = RefreshRateClassifier.Classify(0, hertz, hertz, Options);

        Assert.Equal(DisplayRefreshKeys.Fixed(expectedHertz), key);
    }

    [Fact]
    public void Classify_UsesVirtualRateForFixedMode()
    {
        string? key = RefreshRateClassifier.Classify(0, 60, 240, Options);

        Assert.Equal(DisplayRefreshKeys.Fixed(60), key);
    }

    [Fact]
    public void Classify_ReturnsNullForUnvalidatedRate()
    {
        Assert.Null(RefreshRateClassifier.Classify(0, 144, 144, Options));
        Assert.Null(RefreshRateClassifier.Classify(
            WindowsDisplayNative.DisplayConfigPathBoostRefreshRate,
            60,
            120,
            Options));
    }
}
