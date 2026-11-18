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

    internal static byte[] EncodeRow(int row, IReadOnlyList<RgbColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Rows);
        if (colors.Count != Rows * Columns)
        {
            throw new ArgumentException("A KI8110 keyboard frame requires exactly 114 colors.", nameof(colors));
        }
        byte[] payload = new byte[PayloadLength];
        payload[0] = 0xFF; // Payload frame ID; not the report transaction ID.
        payload[1] = checked((byte)row);
        payload[3] = Columns - 1;
        for (int column = 0; column < Columns; column++)
        {
            RgbColor color = colors[row * Columns + column];
            int offset = 4 + column * 3;
            payload[offset] = color.Red;
            payload[offset + 1] = color.Green;
            payload[offset + 2] = color.Blue;
        }
        return payload;
    }
}
