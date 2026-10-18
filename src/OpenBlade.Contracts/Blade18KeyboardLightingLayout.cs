using System.Security.Cryptography;
using System.Text.Json;

namespace OpenBlade.Contracts;

/// <summary>
/// Source-backed lighting addresses for the exact RZ09-0484. The geometry includes
/// regional keys; input mapping includes only keys consistent across all reviewed layouts.
/// This does not admit a keyboard sequence layout or establish physical validation.
/// </summary>
public static class Blade18KeyboardLightingLayout
{
    public const int Rows = 6;
    public const int Columns = 16;
    public const int SlotCount = Rows * Columns;

    private const string ApprovedFixtureSha256 =
        "446751B434F3B3EE7E55DE49D3BB93FEDEE4D48468BCE85EF19497D41DEF05EC";

    private static readonly (double X, double Y)?[] Positions = new (double, double)?[SlotCount];
    private static readonly Dictionary<(ushort ScanCode, bool Extended), byte> InputSlots = [];

    static Blade18KeyboardLightingLayout()
    {
        using Stream stream = typeof(Blade18KeyboardLightingLayout).Assembly.GetManifestResourceStream(
            "OpenBlade.Contracts.Fixtures.Blade18KeyboardLightingLayout.json")
            ?? throw new InvalidOperationException("The Blade 18 lighting layout is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), ApprovedFixtureSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Blade 18 lighting layout source pin does not match.");
        }

        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1
            || root.GetProperty("vendorId").GetInt32() != 0x1532
            || root.GetProperty("productId").GetInt32() != 0x02A0
            || root.GetProperty("model").GetString() != "RZ09-0484"
            || root.GetProperty("rows").GetInt32() != Rows
            || root.GetProperty("columns").GetInt32() != Columns
            || root.GetProperty("slots").GetArrayLength() != 87
            || root.GetProperty("inputs").GetArrayLength() != 77)
        {
            throw new InvalidOperationException("The Blade 18 lighting layout identity or dimensions are invalid.");
        }

        foreach (JsonElement entry in root.GetProperty("slots").EnumerateArray())
        {
            if (entry.GetArrayLength() != 3)
            {
                throw new InvalidOperationException("The Blade 18 lighting position is malformed.");
            }

            int slot = entry[0].GetInt32();
            double x = entry[1].GetDouble();
            double y = entry[2].GetDouble();
            if ((uint)slot >= SlotCount || Positions[slot].HasValue
                || !double.IsFinite(x) || !double.IsFinite(y)
                || x < 0 || x > 14 || y < 0 || y > 6)
            {
                throw new InvalidOperationException("The Blade 18 lighting position is invalid or duplicated.");
            }

            Positions[slot] = (x, y);
        }

        foreach (JsonElement entry in root.GetProperty("inputs").EnumerateArray())
        {
            if (entry.GetArrayLength() != 3)
            {
                throw new InvalidOperationException("The Blade 18 lighting input is malformed.");
            }

            ushort scanCode = entry[0].GetUInt16();
            int flags = entry[1].GetInt32();
            byte slot = entry[2].GetByte();
            if (scanCode is 0 or > 255 || flags is not (0 or 2) || !IsKeySlot(slot)
                || !InputSlots.TryAdd((scanCode, flags == 2), slot))
            {
                throw new InvalidOperationException("The Blade 18 lighting input is invalid or duplicated.");
            }
        }
    }

    public static bool TryMapScanCode(ushort scanCode, bool extended, out byte slot) =>
        InputSlots.TryGetValue((scanCode, extended), out slot);

    public static bool IsKeySlot(int slot) =>
        (uint)slot < SlotCount && Positions[slot].HasValue;

    /// <summary>
    /// Returns Synapse's logical spatial matrix coordinates, not transport row/column.
    /// The down arrow has position (13, 6), while its transport address is row 5, column 15.
    /// </summary>
    public static bool TryGetKeyPosition(int slot, out double x, out double y)
    {
        if (IsKeySlot(slot) && Positions[slot] is { } position)
        {
            x = position.X;
            y = position.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }
}
