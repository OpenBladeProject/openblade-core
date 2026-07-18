namespace OpenBlade.Display;

internal static class RefreshRateVisibilityFilter
{
    internal static int[] SelectFixedRates(
        IEnumerable<int> driverRates,
        IEnumerable<int> advertisedDetailedTimingRates,
        int currentHertz,
        int physicalHertz)
    {
        ArgumentNullException.ThrowIfNull(driverRates);
        ArgumentNullException.ThrowIfNull(advertisedDetailedTimingRates);

        HashSet<int> driver = driverRates.ToHashSet();
        HashSet<int> advertised = advertisedDetailedTimingRates.ToHashSet();

        return driver
            .Where(advertised.Contains)
            .Append(currentHertz)
            .Append(physicalHertz)
            .Where(driver.Contains)
            .Distinct()
            .Order()
            .ToArray();
    }
}
