namespace OpenBlade.Contracts;

/// <summary>
/// One locally sampled 6 by 17 RGB keyboard frame. It carries colors only;
/// the service remains the sole owner of the HID report envelope and handle.
/// </summary>
public sealed record AmbientLightingFrame(IReadOnlyList<RgbColor> Slots)
{
    public const int Rows = 6;
    public const int Columns = 17;
    public const int SlotCount = Rows * Columns;
    private static readonly int[] HoleSlots =
        [0, 17, 31, 34, 48, 51, 64, 65, 68, 70, 81, 82, 85, 89, 92, 93];

    public bool IsWellFormed()
    {
        if (Slots is not { Count: SlotCount })
        {
            return false;
        }

        foreach (int slot in HoleSlots)
        {
            if (Slots[slot] != default)
            {
                return false;
            }
        }

        return true;
    }

    public AmbientLightingFrame Copy() => new(Slots.ToArray());

    public static bool IsHole(int slot)
    {
        if ((uint)slot >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return Array.BinarySearch(HoleSlots, slot) >= 0;
    }
}
