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
    public const int RgbByteCount = SlotCount * 3;

    private static readonly int[] LayoutHoleSlotsValue =
        [0, 17, 31, 34, 48, 51, 64, 65, 68, 70, 81, 82, 85, 89, 92, 93];
    private static readonly IReadOnlyList<int> LayoutHoleSlotsReadOnly =
        Array.AsReadOnly(LayoutHoleSlotsValue);
    private static readonly bool[] HoleMask = CreateHoleMask();

    public static IReadOnlyList<int> LayoutHoleSlots => LayoutHoleSlotsReadOnly;

    public bool IsWellFormed()
    {
        if (Slots is not { Count: SlotCount })
        {
            return false;
        }

        foreach (int slot in LayoutHoleSlotsValue)
        {
            if (Slots[slot] != default)
            {
                return false;
            }
        }

        return true;
    }

    public AmbientLightingFrame Copy() => new(Slots.ToArray());

    public byte[] ToRgbBytes()
    {
        if (!IsWellFormed())
        {
            throw new InvalidOperationException("The ambient lighting frame is malformed.");
        }

        byte[] bytes = new byte[RgbByteCount];
        for (int slot = 0; slot < SlotCount; slot++)
        {
            RgbColor color = Slots[slot];
            int offset = slot * 3;
            bytes[offset] = color.Red;
            bytes[offset + 1] = color.Green;
            bytes[offset + 2] = color.Blue;
        }

        return bytes;
    }

    public static AmbientLightingFrame FromRgbBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (!IsWellFormedRgbBytes(bytes))
        {
            throw new ArgumentException(
                $"An ambient lighting frame must contain exactly {RgbByteCount} RGB bytes with black layout holes.",
                nameof(bytes));
        }

        RgbColor[] slots = new RgbColor[SlotCount];
        for (int slot = 0; slot < SlotCount; slot++)
        {
            int offset = slot * 3;
            slots[slot] = new RgbColor(
                bytes[offset],
                bytes[offset + 1],
                bytes[offset + 2]);
        }

        return new AmbientLightingFrame(slots);
    }

    public static bool IsWellFormedRgbBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RgbByteCount)
        {
            return false;
        }

        foreach (int slot in LayoutHoleSlotsValue)
        {
            int offset = slot * 3;
            if (bytes[offset] != 0
                || bytes[offset + 1] != 0
                || bytes[offset + 2] != 0)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsHole(int slot)
    {
        if ((uint)slot >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return HoleMask[slot];
    }

    private static bool[] CreateHoleMask()
    {
        bool[] mask = new bool[SlotCount];
        foreach (int slot in LayoutHoleSlotsValue)
        {
            mask[slot] = true;
        }

        return mask;
    }
}
