using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// The eighteen logical RGB slots in one captured cooling-pad lighting row.
/// Slot order is protocol order and does not claim a physical origin or direction.
/// </summary>
internal sealed class CoolingPadLightingFrame
{
    internal const int RowCount = 1;
    internal const int SlotCount = 18;

    private readonly RgbColor[] slots;

    internal CoolingPadLightingFrame(ReadOnlySpan<RgbColor> slots)
    {
        if (slots.Length != SlotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slots),
                slots.Length,
                $"A cooling-pad lighting frame must contain exactly {SlotCount} RGB slots.");
        }

        this.slots = slots.ToArray();
    }

    internal RgbColor this[int index] => slots[index];

    internal RgbColor[] ToArray() => (RgbColor[])slots.Clone();
}
