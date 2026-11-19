using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// The upstream-declared 6-by-16 frame envelope for admitted experimental models.
/// Grid effects address only this bounded envelope; key-triggered effects
/// additionally require their own input-to-light map.
/// </summary>
internal static class ExperimentalBladeLightingFrame
{
    internal const int RowCount = 6;
    internal const int ColumnCount = 16;
    internal const int RowPayloadLength = 4 + ColumnCount * 3;

    internal static byte[] CreateUniformRow(int row, RgbColor color, bool padded = false)
    {
        if ((uint)row >= RowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        byte[] payload = new byte[padded ? 70 : RowPayloadLength];
        payload[0] = 0xFF;
        payload[1] = checked((byte)row);
        payload[2] = 0;
        payload[3] = ColumnCount - 1;
        for (int column = 0; column < ColumnCount; column++)
        {
            int offset = 4 + column * 3;
            payload[offset] = color.Red;
            payload[offset + 1] = color.Green;
            payload[offset + 2] = color.Blue;
        }

        return payload;
    }
    internal static byte[] CreateRow(int row, IReadOnlyList<RgbColor> colors, bool padded)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colors.Count != RowCount * ColumnCount)
        {
            throw new ArgumentException("A grid frame must contain exactly 96 RGB addresses.", nameof(colors));
        }
        byte[] payload = CreateUniformRow(row, default, padded);
        for (int column = 0; column < ColumnCount; column++)
        {
            RgbColor color = colors[row * ColumnCount + column];
            int offset = 4 + column * 3;
            payload[offset] = color.Red;
            payload[offset + 1] = color.Green;
            payload[offset + 2] = color.Blue;
        }
        return payload;
    }
}
