using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// The upstream-declared 6-by-16 frame envelope used only for uniform colors on
/// experimental models. Spatial effects remain excluded because this geometry
/// does not establish a physical-key mask.
/// </summary>
internal static class ExperimentalBladeLightingFrame
{
    internal const int RowCount = 6;
    internal const int ColumnCount = 16;
    internal const int RowPayloadLength = 4 + ColumnCount * 3;

    internal static byte[] CreateUniformRow(int row, RgbColor color)
    {
        if ((uint)row >= RowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        byte[] payload = new byte[RowPayloadLength];
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
}
