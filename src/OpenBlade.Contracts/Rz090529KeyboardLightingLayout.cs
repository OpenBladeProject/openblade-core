using System.Security.Cryptography;
using System.Text.Json;

namespace OpenBlade.Contracts;

/// <summary>
/// Exact RZ09-0529 keyboard addresses shared by every reviewed vendor layout.
/// Logical positions exclude regional disagreements and the auxiliary region.
/// This source-reviewed map does not establish physical validation.
/// </summary>
public static class Rz090529KeyboardLightingLayout
{
    public const int Rows = 6;
    public const int Columns = 19;
    public const int SlotCount = Rows * Columns;

    private const string ApprovedFixtureSha256 = "71B8324BFA5EECA880701B032F159A4357A09046C718565236DA73DE7A095613";
    private static readonly bool[] KeySlots = new bool[SlotCount];
    private static readonly (double X, double Y)?[] Positions = new (double, double)?[SlotCount];
    private static readonly Dictionary<(ushort ScanCode, bool Extended), byte> Inputs = [];

    static Rz090529KeyboardLightingLayout()
    {
        using Stream stream = typeof(Rz090529KeyboardLightingLayout).Assembly.GetManifestResourceStream(
            "OpenBlade.Contracts.Fixtures.Rz090529KeyboardLightingLayout.json")
            ?? throw new InvalidOperationException("The RZ09-0529 keyboard layout is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != ApprovedFixtureSha256)
        {
            throw new InvalidOperationException("The RZ09-0529 keyboard layout source pin does not match.");
        }

        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1
            || root.GetProperty("model").GetString() != "RZ09-0529"
            || root.GetProperty("vendorId").GetInt32() != 0x1532
            || root.GetProperty("productId").GetInt32() != 0x02C7
            || root.GetProperty("rows").GetInt32() != Rows
            || root.GetProperty("columns").GetInt32() != Columns)
        {
            throw new InvalidOperationException("The RZ09-0529 keyboard layout identity or dimensions are invalid.");
        }

        foreach (JsonElement entry in root.GetProperty("keySlots").EnumerateArray())
        {
            int slot = entry.GetInt32();
            if ((uint)slot >= SlotCount || KeySlots[slot])
            {
                throw new InvalidOperationException("The RZ09-0529 keyboard slot is invalid or duplicated.");
            }
            KeySlots[slot] = true;
        }
        foreach (JsonElement entry in root.GetProperty("positions").EnumerateArray())
        {
            if (entry.GetArrayLength() != 3)
            {
                throw new InvalidOperationException("The RZ09-0529 keyboard position is malformed.");
            }
            int slot = entry[0].GetInt32();
            double x = entry[1].GetDouble();
            double y = entry[2].GetDouble();
            if (!IsKeySlot(slot) || Positions[slot].HasValue
                || !double.IsFinite(x) || !double.IsFinite(y)
                || x < 0 || x > 17 || y < 0 || y > 7)
            {
                throw new InvalidOperationException("The RZ09-0529 keyboard position is invalid or duplicated.");
            }
            Positions[slot] = (x, y);
        }
        foreach (JsonElement entry in root.GetProperty("inputs").EnumerateArray())
        {
            if (entry.GetArrayLength() != 3)
            {
                throw new InvalidOperationException("The RZ09-0529 keyboard input is malformed.");
            }
            ushort scanCode = entry[0].GetUInt16();
            int flags = entry[1].GetInt32();
            byte slot = entry[2].GetByte();
            if (scanCode is 0 or > 255 || flags is not (0 or 2) || !IsKeySlot(slot)
                || !Inputs.TryAdd((scanCode, flags == 2), slot))
            {
                throw new InvalidOperationException("The RZ09-0529 keyboard input is invalid or duplicated.");
            }
        }
        if (KeySlots.Count(value => value) != 98
            || Positions.Count(value => value.HasValue) != 96 || Inputs.Count != 94)
        {
            throw new InvalidOperationException("The RZ09-0529 common keyboard map is incomplete.");
        }
    }

    public static bool IsKeySlot(int slot) => (uint)slot < SlotCount && KeySlots[slot];

    public static bool TryMapScanCode(ushort scanCode, bool extended, out byte slot) =>
        Inputs.TryGetValue((scanCode, extended), out slot);

    /// <summary>
    /// Returns one common input name for an addressable key. Some keypad
    /// positions have multiple input aliases; they share the same lighting slot.
    /// </summary>
    public static bool TryGetRepresentativeScanCode(
        int slot, out ushort scanCode, out bool extended)
    {
        foreach (KeyValuePair<(ushort ScanCode, bool Extended), byte> input in Inputs)
        {
            if (input.Value == slot)
            {
                scanCode = input.Key.ScanCode;
                extended = input.Key.Extended;
                return true;
            }
        }

        scanCode = 0;
        extended = false;
        return false;
    }

    /// <summary>
    /// Returns common logical canvas coordinates, not transport coordinates.
    /// Shared transport slots with conflicting regional positions return false.
    /// </summary>
    public static bool TryGetKeyPosition(int slot, out double x, out double y)
    {
        if ((uint)slot < SlotCount && Positions[slot] is { } position)
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
