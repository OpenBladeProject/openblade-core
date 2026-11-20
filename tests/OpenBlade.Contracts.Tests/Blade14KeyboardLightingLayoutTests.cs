using System.Security.Cryptography;
using System.Text.Json;

namespace OpenBlade.Contracts.Tests;

public sealed class Blade14KeyboardLightingLayoutTests
{
    [Theory]
    [InlineData(0x1E, false, 50)]
    [InlineData(0x20, false, 52)]
    [InlineData(0x4B, true, 92)]
    [InlineData(0x48, true, 93)]
    [InlineData(0x4D, true, 94)]
    [InlineData(0x50, true, 95)]
    public void CommonKeyIdentitiesResolveToExactRgbAddresses(int scan, bool extended, int expected)
    {
        Assert.True(Blade14KeyboardLightingLayout.TryMapScanCode((ushort)scan, extended, out byte slot));
        Assert.Equal(expected, slot);
        Assert.True(Blade14KeyboardLightingLayout.TryGetKeyPosition(slot, out double x, out double y));
        Assert.Equal(expected % 16, x);
        Assert.Equal(expected / 16, y);
    }

    [Theory]
    [InlineData(0x1C, false)] // Enter moves between ANSI and ISO/Japanese.
    [InlineData(0x39, false)] // Space has different descriptor indices.
    [InlineData(0x38, false)] // Left Alt differs on Japanese.
    [InlineData(0x38, true)]
    [InlineData(0x2B, false)]
    [InlineData(0x56, false)]
    [InlineData(0x7D, false)]
    [InlineData(0xFF, false)]
    [InlineData(0x1E, true)]
    public void AmbiguousRegionalAndUnknownInputsRemainUnavailable(int scan, bool extended)
    {
        Assert.False(Blade14KeyboardLightingLayout.TryMapScanCode((ushort)scan, extended, out _));
    }

    [Fact]
    public void EmbeddedFixturePinAndEveryCommonSlotMatchTheThreeReviewedTables()
    {
        using Stream stream = typeof(Blade14KeyboardLightingLayout).Assembly.GetManifestResourceStream(
            "OpenBlade.Contracts.Fixtures.Blade14KeyboardLightingLayout.json")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Assert.Equal("9356BC177DFBCB458E4C9E465D93AD7E32AF94A61ADBDF7C7E2A7319A7D3E623",
            Convert.ToHexString(SHA256.HashData(buffer.ToArray())));
        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        byte[][] variants = document.RootElement.GetProperty("variants").EnumerateArray()
            .Select(variant => variant.GetProperty("hidUsages").EnumerateArray().Select(value => value.GetByte()).ToArray())
            .ToArray();
        Assert.Equal(3, variants.Length);
        int commonCount = 0;
        for (int slot = 0; slot < 96; slot++)
        {
            bool common = variants[0][slot] != 0 && variants.All(map => map[slot] == variants[0][slot]);
            Assert.Equal(common, Blade14KeyboardLightingLayout.IsKeySlot(slot));
            if (common)
            {
                commonCount++;
            }
        }
        Assert.Equal(74, commonCount);
        Assert.False(Blade14KeyboardLightingLayout.IsKeySlot(-1));
        Assert.False(Blade14KeyboardLightingLayout.IsKeySlot(96));
    }

    [Fact]
    public void SequenceUsesTheThreeTableUnionAndKeepsSharedHolesAndContainerTailDark()
    {
        Assert.True(KeyboardLightingLayoutIds.IsKnown(KeyboardLightingLayoutIds.Rz09_0530));
        int key = Enumerable.Range(0, 96).First(slot => Blade14KeyboardLightingLayout.IsKeySlot(slot)
            && AmbientLightingFrame.IsHole(slot));
        var colors = new RgbColor[AmbientLightingFrame.SlotCount];
        colors[key] = new(23, 45, 67);
        // Slots bound by only one or two regional tables remain addressable.
        foreach (int regional in RegionalSlots)
        {
            colors[regional] = new(7, 8, (byte)regional);
        }
        string encoded = KeyboardLightingSequenceCodec.Encode([new(100, colors)], KeyboardLightingLayoutIds.Rz09_0530);
        Assert.Equal(colors, Assert.Single(KeyboardLightingSequenceCodec.Decode(encoded, KeyboardLightingLayoutIds.Rz09_0530).Frames).Slots);
        Assert.Throws<InvalidDataException>(() => KeyboardLightingSequenceCodec.Decode(encoded));
        foreach (int slot in new[] { 0, 16, 32, 48, 62, 64, 77, 80, 90, 96, 97, 98, 99, 100, 101 })
        {
            var rejected = new RgbColor[AmbientLightingFrame.SlotCount];
            rejected[slot] = new(1, 2, 3);
            Assert.Throws<InvalidDataException>(() => KeyboardLightingSequenceCodec.Encode(
                [new(100, rejected)], KeyboardLightingLayoutIds.Rz09_0530));
        }
        Assert.Equal(87, Enumerable.Range(0, 96).Count(Blade14KeyboardLightingLayout.IsAddressableKeySlot));
    }

    // Slots whose usage differs between the three reviewed tables.
    private static readonly int[] RegionalSlots = [30, 46, 47, 61, 63, 66, 78, 84, 85, 86, 87, 88, 89];

    [Fact]
    public void EveryEditionByteSelectsTheFirmwareSwitchTable()
    {
        for (int value = 0; value <= byte.MaxValue; value++)
        {
            string expected = value switch
            {
                0 or 1 or 5 or 8 or 9 or 11 => "map-afe3",
                12 => "map-b63b",
                // 2, 3, 4, 6, 7, 10 and every other byte reach the default branch.
                _ => "map-b313",
            };
            Assert.Equal(expected, Blade14KeyboardLightingLayout.GetMapId((byte)value));
        }
    }

    [Theory]
    [InlineData(0, "map-afe3", 79, 76)]
    [InlineData(12, "map-b63b", 83, 80)]
    [InlineData(2, "map-b313", 80, 77)]
    public void ConfirmedEditionBindsItsTablesCompleteSlotSet(byte edition, string mapId, int keys, int inputs)
    {
        byte[] usages = FixtureUsages(mapId);
        int keyCount = 0;
        for (int slot = 0; slot < 96; slot++)
        {
            Assert.Equal(usages[slot] != 0, Blade14KeyboardLightingLayout.IsKeySlot(edition, slot));
            Assert.Equal(usages[slot] != 0, Blade14KeyboardLightingLayout.TryGetKeyPosition(edition, slot, out _, out _));
            Assert.True(!Blade14KeyboardLightingLayout.IsKeySlot(slot) || usages[slot] != 0);
            keyCount += usages[slot] != 0 ? 1 : 0;
        }
        Assert.Equal(keys, keyCount);
        int bound = 0;
        for (ushort scan = 1; scan < 0x80; scan++)
        {
            foreach (bool extended in new[] { false, true })
            {
                if (Blade14KeyboardLightingLayout.TryMapScanCode(edition, scan, extended, out byte slot))
                {
                    Assert.NotEqual(0, usages[slot]);
                    bound++;
                }
            }
        }
        Assert.Equal(inputs, bound);
    }

    [Theory]
    [InlineData(0x1C, false, 63, 46, 46)] // Enter.
    [InlineData(0x39, false, 86, 86, 87)] // Space.
    [InlineData(0x38, false, 85, 84, 85)] // Left Alt.
    [InlineData(0x38, true, 89, -1, 89)] // Right Alt.
    [InlineData(0x2B, false, 47, 61, 61)] // HID 0x31 or Non-US # 0x32.
    [InlineData(0x56, false, -1, -1, 66)] // Non-US backslash.
    [InlineData(0x7D, false, -1, 30, -1)] // International3.
    [InlineData(0x73, false, -1, 78, -1)] // International1.
    [InlineData(0x7B, false, -1, 85, -1)] // International5.
    [InlineData(0x79, false, -1, 88, -1)] // International4.
    [InlineData(0x70, false, -1, 89, -1)] // International2.
    public void RegionalKeysResolveOnlyThroughTheirSelectedTable(
        int scan, bool extended, int afe3, int b63b, int b313)
    {
        foreach ((byte edition, int expected) in new[] { ((byte)0, afe3), ((byte)12, b63b), ((byte)2, b313) })
        {
            bool mapped = Blade14KeyboardLightingLayout.TryMapScanCode(edition, (ushort)scan, extended, out byte slot);
            Assert.Equal(expected >= 0, mapped);
            if (mapped)
            {
                Assert.Equal(expected, slot);
            }
        }
        Assert.False(Blade14KeyboardLightingLayout.TryMapScanCode(null, (ushort)scan, extended, out _));
    }

    [Fact]
    public void UnconfirmedEditionKeepsTheCommonIntersection()
    {
        for (int slot = 0; slot < 96; slot++)
        {
            Assert.Equal(Blade14KeyboardLightingLayout.IsKeySlot(slot), Blade14KeyboardLightingLayout.IsKeySlot(null, slot));
        }
        foreach (int slot in RegionalSlots)
        {
            Assert.False(Blade14KeyboardLightingLayout.IsKeySlot(null, slot));
            Assert.True(Blade14KeyboardLightingLayout.IsAddressableKeySlot(slot));
        }
        Assert.False(Blade14KeyboardLightingLayout.BindsUsage(null, 0x32));
        Assert.False(Blade14KeyboardLightingLayout.BindsUsage(0, 0x32));
        Assert.True(Blade14KeyboardLightingLayout.BindsUsage(0, 0x31));
        Assert.True(Blade14KeyboardLightingLayout.BindsUsage(12, 0x32));
        Assert.True(Blade14KeyboardLightingLayout.BindsUsage(2, 0x32));
    }

    [Fact]
    public void EmbeddedSelectorGroupsMatchThePinnedEvidence()
    {
        using JsonDocument document = JsonDocument.Parse(FixtureBytes());
        var groups = document.RootElement.GetProperty("variants").EnumerateArray().ToDictionary(
            variant => variant.GetProperty("id").GetString()!,
            variant => (variant.GetProperty("selectorValues").EnumerateArray().Select(value => value.GetInt32()).ToArray(),
                variant.GetProperty("otherByteValues").GetBoolean()));
        Assert.Equal([0, 1, 5, 8, 9, 11], groups["map-afe3"].Item1);
        Assert.False(groups["map-afe3"].Item2);
        Assert.Equal([12], groups["map-b63b"].Item1);
        Assert.False(groups["map-b63b"].Item2);
        Assert.Equal([2, 3, 4, 6, 7, 10], groups["map-b313"].Item1);
        Assert.True(groups["map-b313"].Item2);
    }

    private static byte[] FixtureUsages(string mapId)
    {
        using JsonDocument document = JsonDocument.Parse(FixtureBytes());
        return document.RootElement.GetProperty("variants").EnumerateArray()
            .Single(variant => variant.GetProperty("id").GetString() == mapId)
            .GetProperty("hidUsages").EnumerateArray().Select(value => value.GetByte()).ToArray();
    }

    private static byte[] FixtureBytes()
    {
        using Stream stream = typeof(Blade14KeyboardLightingLayout).Assembly.GetManifestResourceStream(
            "OpenBlade.Contracts.Fixtures.Blade14KeyboardLightingLayout.json")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
