using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

// KI8110's seventh legacy row belongs to LED38, not the keyboard. Keep the
// keyboard-only bounds and activation separate from the combined vendor canvas.
// See docs/rz09-0529-lighting-expansion.md for the pinned MCU/source review.
internal static class Rz090529KeyboardLightingFrame
{
    internal const int Rows = 6;
    internal const int Columns = 19;
    internal const int PayloadLength = 4 + Columns * 3;
    internal const int VaporChamberTransportRow = 6;
    internal const int VaporChamberColumns = 18;

    internal static byte[] EncodeRow(int row, IReadOnlyList<RgbColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Rows);
        if (colors.Count != Rows * Columns)
        {
            throw new ArgumentException("A KI8110 keyboard frame requires exactly 114 colors.", nameof(colors));
        }
        return Encode(row, column => colors[row * Columns + column]);
    }

    // The Product 711 manifest maps logical VC row 0, columns 0..17, to transport
    // row 6 columns 1..18. Transport column 0 carries no VC address and stays black.
    internal static byte[] EncodeVaporChamberRow(IReadOnlyList<RgbColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colors.Count != VaporChamberColumns)
        {
            throw new ArgumentException("A KI8110 Vapor Chamber row requires exactly 18 colors.", nameof(colors));
        }
        return Encode(VaporChamberTransportRow, column => column == 0 ? default : colors[column - 1]);
    }

    private static byte[] Encode(int row, Func<int, RgbColor> color)
    {
        byte[] payload = new byte[PayloadLength];
        payload[0] = 0xFF; // Payload frame ID; not the report transaction ID.
        payload[1] = checked((byte)row);
        payload[3] = Columns - 1;
        for (int column = 0; column < Columns; column++)
        {
            RgbColor value = color(column);
            int offset = 4 + column * 3;
            payload[offset] = value.Red;
            payload[offset + 1] = value.Green;
            payload[offset + 2] = value.Blue;
        }
        return payload;
    }
}
