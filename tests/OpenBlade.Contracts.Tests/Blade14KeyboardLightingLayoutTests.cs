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
    public void SequenceUsesItsOwnMaskAndKeepsRegionalSlotsAndContainerTailDark()
    {
        Assert.True(KeyboardLightingLayoutIds.IsKnown(KeyboardLightingLayoutIds.Rz09_0530));
        int key = Enumerable.Range(0, 96).First(slot => Blade14KeyboardLightingLayout.IsKeySlot(slot)
            && AmbientLightingFrame.IsHole(slot));
        var colors = new RgbColor[AmbientLightingFrame.SlotCount];
        colors[key] = new(23, 45, 67);
        string encoded = KeyboardLightingSequenceCodec.Encode([new(100, colors)], KeyboardLightingLayoutIds.Rz09_0530);
        Assert.Equal(colors, Assert.Single(KeyboardLightingSequenceCodec.Decode(encoded, KeyboardLightingLayoutIds.Rz09_0530).Frames).Slots);
        Assert.Throws<InvalidDataException>(() => KeyboardLightingSequenceCodec.Decode(encoded));
        foreach (int slot in new[] { 46, 63, 85, 86, 87, 89, 96, 97, 98, 99, 100, 101 })
        {
            var rejected = new RgbColor[AmbientLightingFrame.SlotCount];
            rejected[slot] = new(1, 2, 3);
            Assert.Throws<InvalidDataException>(() => KeyboardLightingSequenceCodec.Encode(
                [new(100, rejected)], KeyboardLightingLayoutIds.Rz09_0530));
        }
    }
}
