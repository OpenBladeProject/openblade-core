namespace OpenBlade.Protocol;

/// <summary>
/// Deterministic host-software colors. Callers supply their own grid coordinates,
/// mask and transport; this pattern establishes no device or firmware authority.
/// It is not a captured or vendor-native Fire waveform.
/// </summary>
internal static class SoftwareFirePattern
{
    internal static MatrixRgb Render(int row, int column, TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        const long cycleTicks = 12 * TimeSpan.TicksPerSecond;
        double time = elapsed.Ticks % cycleTicks / (double)cycleTicks * 2 * Math.PI;
        double flicker = 0.5 + 0.25 * Math.Sin(column * 1.7 + row * 0.9 - time * 3)
            + 0.25 * Math.Sin(column * 0.6 - row * 1.3 + time * 5);
        double heat = Math.Clamp(0.2 + row * 0.1 + flicker * 0.3, 0, 1);
        return new(ToByte(heat * 255), ToByte(heat * heat * 140), 0);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
