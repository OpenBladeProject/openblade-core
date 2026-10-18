using System.Security.Cryptography;
using System.Text.Json;

namespace OpenBlade.Contracts;

/// <summary>
/// Source-backed regional lighting addresses for the exact RZ09-0484. Known Synapse
/// layout identifiers select their exact maps; unavailable identifiers use only
/// mappings that remain unambiguous across the reviewed layouts. This does not admit
/// a keyboard sequence layout or establish physical validation.
/// </summary>
public static class Blade18KeyboardLightingLayout
{
    public const int Rows = 6;
    public const int Columns = 16;
    public const int SlotCount = Rows * Columns;

    private const string ApprovedFixtureSha256 =
        "B5C03C722EBA0F57C09D211E4C162F35B7E37A68F0B089E43625C526AD43A1D8";

    private static readonly byte[] ExpectedLayoutIds = [1, 3, 4, 6, 7, 8, 9, 12, 16];
    private static readonly Dictionary<byte, LayoutData> Layouts = [];
    private static readonly (double X, double Y)?[] UnionPositions = new (double, double)?[SlotCount];
    private static readonly (double X, double Y)?[] FallbackPositions = new (double, double)?[SlotCount];
    private static readonly Dictionary<(ushort ScanCode, bool Extended), byte> FallbackInputSlots = [];

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
        if (root.GetProperty("schemaVersion").GetInt32() != 2
            || root.GetProperty("vendorId").GetInt32() != 0x1532
            || root.GetProperty("productId").GetInt32() != 0x02A0
            || root.GetProperty("model").GetString() != "RZ09-0484"
            || root.GetProperty("rows").GetInt32() != Rows
            || root.GetProperty("columns").GetInt32() != Columns
            || !MatchesExpectedValues(root.GetProperty("layoutIds"), ExpectedLayoutIds)
            || !MatchesExpectedValues(root.GetProperty("editionIds"), [0, 128])
            || root.GetProperty("sources").GetArrayLength() != 18
            || root.GetProperty("layoutGroups").GetArrayLength() != 3)
        {
            throw new InvalidOperationException("The Blade 18 lighting layout identity or dimensions are invalid.");
        }

        foreach (JsonElement group in root.GetProperty("layoutGroups").EnumerateArray())
        {
            LayoutData layout = ParseLayout(group);
            foreach (JsonElement layoutIdElement in group.GetProperty("layoutIds").EnumerateArray())
            {
                byte layoutId = layoutIdElement.GetByte();
                if (!ExpectedLayoutIds.Contains(layoutId) || !Layouts.TryAdd(layoutId, layout))
                {
                    throw new InvalidOperationException("The Blade 18 lighting layout identifier is invalid or duplicated.");
                }
            }
        }

        if (Layouts.Count != ExpectedLayoutIds.Length
            || ExpectedLayoutIds.Any(layoutId => !Layouts.ContainsKey(layoutId)))
        {
            throw new InvalidOperationException("The Blade 18 lighting layout identifiers are incomplete.");
        }

        BuildFallbackMaps();
        if (UnionPositions.Count(position => position.HasValue) != 87 || FallbackInputSlots.Count != 84)
        {
            throw new InvalidOperationException("The Blade 18 regional lighting maps are incomplete.");
        }
    }

    public static bool IsKnownLayout(byte layoutId) => Layouts.ContainsKey(layoutId);

    public static bool TryMapScanCode(ushort scanCode, bool extended, out byte slot) =>
        TryMapScanCode(null, scanCode, extended, out slot);

    public static bool TryMapScanCode(byte? layoutId, ushort scanCode, bool extended, out byte slot)
    {
        Dictionary<(ushort ScanCode, bool Extended), byte> inputSlots =
            layoutId.HasValue && Layouts.TryGetValue(layoutId.Value, out LayoutData? layout)
                ? layout.InputSlots
                : FallbackInputSlots;
        return inputSlots.TryGetValue((scanCode, extended), out slot);
    }

    public static bool IsKeySlot(int slot) =>
        (uint)slot < SlotCount && UnionPositions[slot].HasValue;

    /// <summary>
    /// Returns Synapse's logical spatial matrix coordinates, not transport row/column.
    /// The down arrow has position (13, 6), while its transport address is row 5, column 15.
    /// </summary>
    public static bool TryGetKeyPosition(int slot, out double x, out double y) =>
        TryGetKeyPosition(null, slot, out x, out y);

    /// <summary>
    /// Returns the exact layout's Synapse logical coordinates. An unavailable or unknown
    /// layout identifier retains the canonical union geometry used before regional detection.
    /// </summary>
    public static bool TryGetKeyPosition(byte? layoutId, int slot, out double x, out double y)
    {
        (double X, double Y)?[] positions =
            layoutId.HasValue && Layouts.TryGetValue(layoutId.Value, out LayoutData? layout)
                ? layout.Positions
                : FallbackPositions;
        if ((uint)slot < SlotCount && positions[slot] is { } position)
        {
            x = position.X;
            y = position.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    private static LayoutData ParseLayout(JsonElement group)
    {
        var positions = new (double X, double Y)?[SlotCount];
        foreach (JsonElement entry in group.GetProperty("positions").EnumerateArray())
        {
            if (entry.GetArrayLength() != 3)
            {
                throw new InvalidOperationException("The Blade 18 lighting position is malformed.");
            }

            int slot = entry[0].GetInt32();
            double x = entry[1].GetDouble();
            double y = entry[2].GetDouble();
            if ((uint)slot >= SlotCount || positions[slot].HasValue
                || !double.IsFinite(x) || !double.IsFinite(y)
                || x < 0 || x > 14 || y < 0 || y > 6)
            {
                throw new InvalidOperationException("The Blade 18 lighting position is invalid or duplicated.");
            }

            positions[slot] = (x, y);
        }

        var inputSlots = new Dictionary<(ushort ScanCode, bool Extended), byte>();
        foreach (JsonElement entry in group.GetProperty("inputs").EnumerateArray())
        {
            if (entry.GetArrayLength() != 3)
            {
                throw new InvalidOperationException("The Blade 18 lighting input is malformed.");
            }

            ushort scanCode = entry[0].GetUInt16();
            int flags = entry[1].GetInt32();
            byte slot = entry[2].GetByte();
            if (scanCode is 0 or > 255 || flags is not (0 or 2) || (uint)slot >= SlotCount
                || !positions[slot].HasValue
                || !inputSlots.TryAdd((scanCode, flags == 2), slot))
            {
                throw new InvalidOperationException("The Blade 18 lighting input is invalid or duplicated.");
            }
        }

        if (positions.Count(position => position.HasValue) is < 80 or > 84
            || inputSlots.Count is < 82 or > 87)
        {
            throw new InvalidOperationException("The Blade 18 regional lighting map has an unexpected size.");
        }

        return new LayoutData(positions, inputSlots);
    }

    private static void BuildFallbackMaps()
    {
        var inputConflicts = new HashSet<(ushort ScanCode, bool Extended)>();
        foreach (LayoutData layout in Layouts.Values.Distinct())
        {
            for (int slot = 0; slot < SlotCount; slot++)
            {
                if (layout.Positions[slot] is not { } position)
                {
                    continue;
                }

                UnionPositions[slot] ??= position;
                if (!FallbackPositions[slot].HasValue)
                {
                    FallbackPositions[slot] = position;
                }
            }

            foreach (((ushort ScanCode, bool Extended) key, byte slot) in layout.InputSlots)
            {
                if (!FallbackInputSlots.TryAdd(key, slot) && FallbackInputSlots[key] != slot)
                {
                    inputConflicts.Add(key);
                }
            }
        }

        foreach ((ushort ScanCode, bool Extended) key in inputConflicts)
        {
            FallbackInputSlots.Remove(key);
        }
    }

    private static bool MatchesExpectedValues(JsonElement array, ReadOnlySpan<byte> expected)
    {
        if (array.GetArrayLength() != expected.Length)
        {
            return false;
        }

        int index = 0;
        foreach (JsonElement value in array.EnumerateArray())
        {
            if (value.GetByte() != expected[index++])
            {
                return false;
            }
        }

        return true;
    }

    private sealed record LayoutData(
        (double X, double Y)?[] Positions,
        Dictionary<(ushort ScanCode, bool Extended), byte> InputSlots);
}
