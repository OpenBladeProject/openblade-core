using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>Exact product-672 row coordinates; no physical key-name mapping.</summary>
internal sealed class Blade18LightingFrame
{
    internal const int Rows = 6;
    internal const int Columns = 16;
    internal const int SlotCount = Rows * Columns;
    private readonly RgbColor[] slots;

    internal Blade18LightingFrame(IReadOnlyList<RgbColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colors.Count != SlotCount)
        {
            throw new ArgumentException("A Blade 18 frame must contain 96 RGB slots.", nameof(colors));
        }
        slots = colors.ToArray();
    }

    internal IReadOnlyList<RgbColor> Slots => Array.AsReadOnly(slots);

    internal byte[] EncodeRow(int row)
    {
        if ((uint)row >= Rows)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }
        var payload = new byte[4 + Columns * 3];
        payload[0] = 0xFF;
        payload[1] = (byte)row;
        payload[3] = Columns - 1;
        for (int column = 0; column < Columns; column++)
        {
            RgbColor color = slots[row * Columns + column];
            int offset = 4 + column * 3;
            payload[offset] = color.Red;
            payload[offset + 1] = color.Green;
            payload[offset + 2] = color.Blue;
        }
        return payload;
    }
}
