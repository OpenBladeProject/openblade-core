using OpenBlade.Display;

namespace OpenBlade.Display.Tests;

public sealed class RefreshRateVisibilityFilterTests
{
    [Fact]
    public void SelectFixedRates_HidesDriverModesWithoutDetailedPanelTiming()
    {
        int[] result = RefreshRateVisibilityFilter.SelectFixedRates(
            driverRates: [48, 50, 59, 60, 120, 144, 165, 240],
            advertisedDetailedTimingRates: [60, 240],
            currentHertz: 60,
            physicalHertz: 240);

        Assert.Equal([60, 240], result);
    }

    [Fact]
    public void SelectFixedRates_PreservesCurrentAndPhysicalModesReportedByDriver()
    {
        int[] result = RefreshRateVisibilityFilter.SelectFixedRates(
            driverRates: [60, 120, 240],
            advertisedDetailedTimingRates: [],
            currentHertz: 60,
            physicalHertz: 240);

        Assert.Equal([60, 240], result);
    }

    [Fact]
    public void SelectFixedRates_DoesNotInventUnreportedModes()
    {
        int[] result = RefreshRateVisibilityFilter.SelectFixedRates(
            driverRates: [60],
            advertisedDetailedTimingRates: [60, 240],
            currentHertz: 60,
            physicalHertz: 240);

        Assert.Equal([60], result);
    }
}
