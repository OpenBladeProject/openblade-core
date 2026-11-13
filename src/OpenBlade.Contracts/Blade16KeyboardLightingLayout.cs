using System.Security.Cryptography;
using System.Text.Json;

namespace OpenBlade.Contracts;

/// <summary>Exact RZ09-0581 MCU source maps. Canvas addresses remain the original
/// ANSI serialization contract; physical addresses follow the device's layout byte.</summary>
public static class Blade16KeyboardLightingLayout
{
    public const int Rows = 6;
    public const int Columns = 17;
    public const int SlotCount = Rows * Columns;
    private const string FixtureSha256 = "187711408FC0667273CABB19A1CB73B57CA53F98688993BA76E7352A8F2AE0FB";
    private const string ImageSha256 = "90bbe0d8dd1ca31614a2465c7c8821347b2a667606c39bedadbd59b98886344f";
    private static readonly Dictionary<int, byte[]> Tables = [];
    private static readonly byte[][] Layouts = new byte[256][];
    private static readonly bool[] UnionKeys = new bool[SlotCount];
    private static readonly byte[] Canvas;

    public static IReadOnlyList<int> CanvasHoleSlots { get; }
    public static IReadOnlyList<int> UnionHoleSlots { get; }

    static Blade16KeyboardLightingLayout()
    {
        using Stream stream = typeof(Blade16KeyboardLightingLayout).Assembly.GetManifestResourceStream(
            "OpenBlade.Contracts.Fixtures.Blade16KeyboardLightingLayout.json")
            ?? throw new InvalidOperationException("The Blade 16 lighting layout is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        Require(Convert.ToHexString(SHA256.HashData(bytes)) == FixtureSha256, "source pin");
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        Require(root.GetProperty("schemaVersion").GetInt32() == 1
            && root.GetProperty("model").GetString() == "RZ09-0581"
            && root.GetProperty("vendorId").GetInt32() == 0x1532
            && root.GetProperty("productId").GetInt32() == 0x02E0
            && root.GetProperty("rows").GetInt32() == Rows
            && root.GetProperty("columns").GetInt32() == Columns
            && root.GetProperty("recordSize").GetInt32() == 8
            && root.GetProperty("source").GetProperty("imageLength").GetInt32() == 57764
            && root.GetProperty("source").GetProperty("imageSha256").GetString() == ImageSha256,
            "identity");
        var declaredLayouts = new Dictionary<byte, int>();
        foreach (JsonElement table in root.GetProperty("tables").EnumerateArray())
        {
            int offset = table.GetProperty("offset").GetInt32();
            Require(offset is 0xB345 or 0xB915 or 0xBDB5 or 0xC7BD or 0xCCE5 or 0xD1B5,
                "table offset");
            byte[] usages = table.GetProperty("hidUsages").EnumerateArray().Select(x => x.GetByte()).ToArray();
            Require(usages.Length == SlotCount && table.GetProperty("length").GetInt32() == SlotCount * 8
                && Tables.TryAdd(offset, usages), "table dimensions");
            int[] holes = Enumerable.Range(0, SlotCount).Where(i => usages[i] == 0).ToArray();
            Require(table.GetProperty("holes").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(holes)
                && table.GetProperty("nonzeroCount").GetInt32() == SlotCount - holes.Length
                && usages.Where(x => x != 0).Distinct().Count() == SlotCount - holes.Length,
                "table usage identities");
            foreach (JsonElement id in table.GetProperty("layoutIds").EnumerateArray())
            {
                Require(declaredLayouts.TryAdd(id.GetByte(), offset), "duplicate layout");
            }
            for (int slot = 0; slot < SlotCount; slot++)
            {
                UnionKeys[slot] |= usages[slot] != 0;
            }
        }
        Require(Tables.Count == 6 && declaredLayouts.Count == 256, "table coverage");
        JsonElement selector = root.GetProperty("selector");
        Require(selector.GetProperty("storedByteAddress").GetInt32() == 0x10001990
            && selector.GetProperty("query").GetString() == "00/86"
            && selector.GetProperty("queryPayloadIndex").GetInt32() == 0
            && selector.GetProperty("allByteValues").GetArrayLength() == 256, "selector identity");
        var seen = new HashSet<byte>();
        foreach (JsonElement entry in selector.GetProperty("allByteValues").EnumerateArray())
        {
            byte id = entry.GetProperty("layoutId").GetByte();
            (int branch, int table, int auxiliary, int count) = ExpectedSelection(id);
            Require(seen.Add(id) && entry.GetProperty("branchOffset").GetInt32() == branch
                && entry.GetProperty("tableOffset").GetInt32() == table
                && entry.GetProperty("auxiliaryTableOffset").GetInt32() == auxiliary
                && entry.GetProperty("auxiliaryRecordCount").GetInt32() == count
                && declaredLayouts[id] == table, "selector branch");
            Layouts[id] = Tables[table];
        }
        Require(seen.Count == 256, "selector coverage");
        Canvas = Tables[0xB345];
        int[] canvasHoles = Enumerable.Range(0, SlotCount).Where(i => Canvas[i] == 0).ToArray();
        int[] unionHoles = Enumerable.Range(0, SlotCount).Where(i => !UnionKeys[i]).ToArray();
        Require(canvasHoles.SequenceEqual(new[] { 0, 17, 31, 34, 48, 51, 64, 65, 68, 70, 81, 82, 85, 89, 92, 93 })
            && unionHoles.SequenceEqual(new[] { 0, 17, 34, 51, 65, 68, 81, 85, 92 })
            && root.GetProperty("unionHoles").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(unionHoles),
            "hole masks");
        CanvasHoleSlots = Array.AsReadOnly(canvasHoles);
        UnionHoleSlots = Array.AsReadOnly(unionHoles);
    }

    public static bool IsKeySlot(int slot, byte? layoutId = null) =>
        slot >= 0 && slot < SlotCount && (layoutId.HasValue ? Layouts[layoutId.Value][slot] != 0 : UnionKeys[slot]);

    public static byte UsageAt(int slot, byte layoutId) =>
        slot >= 0 && slot < SlotCount ? Layouts[layoutId][slot] : (byte)0;

    // Supplemental identities are transient pulse slots only. Serialized
    // frames keep these cells black under the existing canvas contract.
    public static bool IsCanvasPulseSlot(int slot) =>
        slot >= 0 && slot < SlotCount
        && (Canvas[slot] != 0 || slot is 31 or 48 or 64 or 70 or 82 or 93);

    /// <summary>Maps a stable canvas identity to the device's physical RGB slot.
    /// Supplemental transient slots represent reviewed ISO/Japanese usages.</summary>
    public static bool TryMapCanvasSlot(byte canvasSlot, byte layoutId, out byte physicalSlot)
    {
        physicalSlot = 0;
        if (canvasSlot >= SlotCount)
        {
            return false;
        }
        byte usage = canvasSlot switch
        {
            31 => 0x89, // Yen
            48 => 0x8B, // Non-convert
            64 => 0x88, // Katakana/Hiragana
            70 => 0x64, // OEM102
            82 => 0x87, // Ro
            93 => 0x8A, // Convert
            _ => Canvas[canvasSlot],
        };
        if (usage == 0)
        {
            return false;
        }
        byte[] selected = Layouts[layoutId];
        if (selected[canvasSlot] == usage)
        {
            physicalSlot = canvasSlot;
            return true;
        }
        int slot = Array.IndexOf(selected, usage);
        // ANSI backslash and the ISO key beside Enter share the existing canvas
        // identity. This alias changes only RGB projection, not Raw Input identity.
        if (slot < 0 && usage == 0x31)
        {
            slot = Array.IndexOf(selected, (byte)0x32);
        }
        if (slot < 0)
        {
            return false;
        }
        physicalSlot = checked((byte)slot);
        return true;
    }

    private static (int Branch, int Table, int Auxiliary, int Count) ExpectedSelection(byte id) => id switch
    {
        0 or 1 or 5 or 11 => (0x5CD8, 0xB345, 0xB1CD, 47),
        8 => (0x5D00, 0xBDB5, 0xBC45, 46),
        9 => (0x5CE8, 0xB915, 0xB675, 84),
        7 => (0x5D46, 0xCCE5, 0xCAED, 63),
        12 => (0x5D10, 0xD1B5, 0xD015, 52),
        3 => (0x5D3A, 0xC7BD, 0xC605, 55),
        4 => (0x5D2E, 0xC7BD, 0xC42D, 59),
        14 or 16 => (0x5D22, 0xC7BD, 0xC26D, 56),
        _ => (0x5D56, 0xC7BD, 0xC0E5, 49),
    };

    private static void Require(bool condition, string component)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"The Blade 16 lighting layout {component} is invalid.");
        }
    }
}
