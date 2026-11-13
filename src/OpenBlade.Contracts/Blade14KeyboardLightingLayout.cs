using System.Security.Cryptography;
using System.Text.Json;

namespace OpenBlade.Contracts;

/// <summary>
/// Common HID-to-RGB bindings across the exact RZ09-0530 keyboard MCU's three
/// descriptor tables. Regional disagreements remain unavailable until their
/// runtime selection can be established independently.
/// </summary>
public static class Blade14KeyboardLightingLayout
{
    public const int Rows = 6;
    public const int Columns = 16;
    public const int SlotCount = Rows * Columns;

    private const string ApprovedFixtureSha256 =
        "9356BC177DFBCB458E4C9E465D93AD7E32AF94A61ADBDF7C7E2A7319A7D3E623";
    private const string ApprovedImageSha256 =
        "1ac3f05486d0bca5fbbb1a4a52b874943ad491a00afd298b8d6b857a49d98f11";
    private static readonly HashSet<int> KeySlots = [];
    private static readonly Dictionary<(ushort ScanCode, bool Extended), byte> InputSlots = [];

    static Blade14KeyboardLightingLayout()
    {
        using Stream stream = typeof(Blade14KeyboardLightingLayout).Assembly.GetManifestResourceStream(
            "OpenBlade.Contracts.Fixtures.Blade14KeyboardLightingLayout.json")
            ?? throw new InvalidOperationException("The Blade 14 lighting layout is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != ApprovedFixtureSha256)
        {
            throw new InvalidOperationException("The Blade 14 lighting layout source pin does not match.");
        }
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1
            || root.GetProperty("vendorId").GetInt32() != 0x1532
            || root.GetProperty("productId").GetInt32() != 0x02C5
            || root.GetProperty("model").GetString() != "RZ09-0530"
            || root.GetProperty("sourceImageSha256").GetString() != ApprovedImageSha256
            || root.GetProperty("rows").GetInt32() != Rows
            || root.GetProperty("columns").GetInt32() != Columns
            || root.GetProperty("recordSize").GetInt32() != 8
            || root.GetProperty("coordinateBasis").GetString() != "record-index-row-major")
        {
            throw new InvalidOperationException("The Blade 14 lighting layout identity is invalid.");
        }
        var variants = new List<Dictionary<byte, byte>>();
        foreach (JsonElement variant in root.GetProperty("variants").EnumerateArray())
        {
            JsonElement usages = variant.GetProperty("hidUsages");
            if (usages.GetArrayLength() != SlotCount)
            {
                throw new InvalidOperationException("The Blade 14 lighting layout dimensions are invalid.");
            }
            var map = new Dictionary<byte, byte>();
            for (int slot = 0; slot < SlotCount; slot++)
            {
                byte usage = usages[slot].GetByte();
                if (usage != 0 && !map.TryAdd(usage, (byte)slot))
                {
                    throw new InvalidOperationException("The Blade 14 lighting layout duplicates a HID usage.");
                }
            }
            variants.Add(map);
        }
        if (variants.Count != 3)
        {
            throw new InvalidOperationException("The Blade 14 lighting layout variants are incomplete.");
        }
        foreach ((byte usage, byte slot) in variants[0])
        {
            if (!variants.All(map => map.TryGetValue(usage, out byte other) && other == slot))
            {
                continue;
            }
            KeySlots.Add(slot);
            if (TryGetWindowsScanCode(usage, out ushort scanCode, out bool extended))
            {
                InputSlots.Add((scanCode, extended), slot);
            }
        }
        if (KeySlots.Count != 74 || InputSlots.Count != 71)
        {
            throw new InvalidOperationException("The Blade 14 common lighting bindings are incomplete.");
        }
    }

    public static bool TryMapScanCode(ushort scanCode, bool extended, out byte slot) =>
        InputSlots.TryGetValue((scanCode, extended), out slot);

    public static bool IsKeySlot(int slot) => KeySlots.Contains(slot);

    /// <summary>Returns RGB address-grid coordinates, not physical key geometry.</summary>
    public static bool TryGetKeyPosition(int slot, out double x, out double y)
    {
        if (IsKeySlot(slot))
        {
            x = slot % Columns;
            y = slot / Columns;
            return true;
        }
        x = 0;
        y = 0;
        return false;
    }

    private static bool TryGetWindowsScanCode(byte usage, out ushort scanCode, out bool extended)
    {
        // USB HID Keyboard/Keypad usage identities translated to Windows Set-1
        // make codes. These identity conversions carry no laptop RGB geometry.
        // Unrecognized/vendor usages deliberately have no Raw Input binding.
        ReadOnlySpan<byte> letters =
            [0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26,
                0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C];
        extended = false;
        if (usage is >= 0x04 and <= 0x1D)
        {
            scanCode = letters[usage - 0x04];
            return true;
        }
        if (usage is >= 0x1E and <= 0x27)
        {
            scanCode = (ushort)(usage - 0x1C);
            return true;
        }
        if (usage is >= 0x3A and <= 0x43)
        {
            scanCode = (ushort)(usage + 1);
            return true;
        }
        (int makeCode, bool e0) = usage switch
        {
            0x28 => (0x1C, false),
            0x29 => (0x01, false),
            0x2A => (0x0E, false),
            0x2B => (0x0F, false),
            0x2C => (0x39, false),
            0x2D => (0x0C, false),
            0x2E => (0x0D, false),
            0x2F => (0x1A, false),
            0x30 => (0x1B, false),
            0x31 => (0x2B, false),
            0x33 => (0x27, false),
            0x34 => (0x28, false),
            0x35 => (0x29, false),
            0x36 => (0x33, false),
            0x37 => (0x34, false),
            0x38 => (0x35, false),
            0x39 => (0x3A, false),
            0x44 => (0x57, false),
            0x45 => (0x58, false),
            0x4C => (0x53, true),
            0x4F => (0x4D, true),
            0x50 => (0x4B, true),
            0x51 => (0x50, true),
            0x52 => (0x48, true),
            0xE0 => (0x1D, false),
            0xE1 => (0x2A, false),
            0xE3 => (0x5B, true),
            0xE5 => (0x36, false),
            _ => (0, false),
        };
        scanCode = checked((ushort)makeCode);
        extended = e0;
        return scanCode != 0;
    }
}
