namespace OpenBlade.Display;

public static class DisplayBrightnessLevels
{
    public static byte SelectNearest(byte requested, IReadOnlyList<byte> levels)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requested, (byte)100);
        ArgumentNullException.ThrowIfNull(levels);
        return levels.Where(level => level <= 100).Distinct()
            .OrderBy(level => Math.Abs(level - requested)).ThenBy(level => level)
            .Select(level => (byte?)level).FirstOrDefault()
            ?? throw new InvalidOperationException("Windows did not report supported brightness levels.");
    }
}
