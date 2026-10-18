using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class Blade18KeyboardLightingLayoutTests
{
    [Fact]
    public void ReviewedSourceFixtureRemainsPinned()
    {
        using Stream stream = typeof(Blade18KeyboardLightingLayout).Assembly.GetManifestResourceStream(
            "OpenBlade.Contracts.Fixtures.Blade18KeyboardLightingLayout.json")!;
        Assert.Equal(
            "B5C03C722EBA0F57C09D211E4C162F35B7E37A68F0B089E43625C526AD43A1D8",
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    [Fact]
    public void KnownLayoutsAreExactlyTheReviewedProduct672Regions()
    {
        byte[] expected = [1, 3, 4, 6, 7, 8, 9, 12, 16];
        Assert.Equal(expected, Enumerable.Range(0, 256)
            .Where(value => Blade18KeyboardLightingLayout.IsKnownLayout((byte)value))
            .Select(value => (byte)value));
    }

    [Theory]
    [InlineData(1, 63, 47, 85, 89, false)]
    [InlineData(3, 46, 61, 85, 89, true)]
    [InlineData(4, 46, 61, 85, 89, true)]
    [InlineData(6, 46, 61, 85, 89, true)]
    [InlineData(7, 46, 61, 85, 89, true)]
    [InlineData(8, 63, 47, 85, 89, false)]
    [InlineData(9, 63, 47, 85, 89, false)]
    [InlineData(12, 46, 61, 84, 90, false)]
    [InlineData(16, 63, 47, 85, 89, false)]
    public void ExactLayoutsUseTheirOwnRegionalMappings(
        byte layoutId,
        byte enterSlot,
        byte backslashSlot,
        byte leftAltSlot,
        byte rightAltSlot,
        bool hasIsoExtraKey)
    {
        AssertMap(layoutId, 0x1c, false, enterSlot);
        AssertMap(layoutId, 0x2b, false, backslashSlot);
        AssertMap(layoutId, 0x38, false, leftAltSlot);
        AssertMap(layoutId, 0x38, true, rightAltSlot);
        Assert.Equal(
            hasIsoExtraKey,
            Blade18KeyboardLightingLayout.TryMapScanCode(layoutId, 0x56, false, out byte isoSlot));
        if (hasIsoExtraKey)
        {
            Assert.Equal(66, isoSlot);
        }
    }

    [Theory]
    [InlineData(0x70, 89)]
    [InlineData(0x73, 78)]
    [InlineData(0x79, 88)]
    [InlineData(0x7b, 85)]
    [InlineData(0x7d, 30)]
    public void JapaneseLayoutMapsItsAdditionalScanCodes(int scanCode, byte expectedSlot)
    {
        AssertMap(12, (ushort)scanCode, false, expectedSlot);
        Assert.False(Blade18KeyboardLightingLayout.TryMapScanCode(1, (ushort)scanCode, false, out _));
    }

    [Theory]
    [InlineData(0x01, false, 1)]
    [InlineData(0x1d, true, 91)]
    [InlineData(0x56, false, 66)]
    [InlineData(0x70, false, 89)]
    [InlineData(0x73, false, 78)]
    [InlineData(0x79, false, 88)]
    [InlineData(0x7b, false, 85)]
    [InlineData(0x7d, false, 30)]
    public void UnknownLayoutFallbackIncludesInputsWithOneUnambiguousTarget(
        int scanCode,
        bool extended,
        byte expectedSlot)
    {
        AssertMap(null, (ushort)scanCode, extended, expectedSlot);
        AssertMap(2, (ushort)scanCode, extended, expectedSlot);
    }

    [Theory]
    [InlineData(0x1c, false)]
    [InlineData(0x2b, false)]
    [InlineData(0x38, false)]
    [InlineData(0x38, true)]
    [InlineData(0x50, false)]
    [InlineData(0xffff, false)]
    public void UnknownLayoutFallbackRejectsConflictingAndForeignInputs(int scanCode, bool extended)
    {
        Assert.False(Blade18KeyboardLightingLayout.TryMapScanCode(
            null, (ushort)scanCode, extended, out _));
        Assert.False(Blade18KeyboardLightingLayout.TryMapScanCode(
            2, (ushort)scanCode, extended, out _));
    }

    [Fact]
    public void RegionalGeometryUsesTheSelectedLayout()
    {
        Assert.True(Blade18KeyboardLightingLayout.TryGetKeyPosition(1, 85, out double ansiX, out double ansiY));
        Assert.Equal(3, ansiX);
        Assert.Equal(5, ansiY);

        Assert.True(Blade18KeyboardLightingLayout.TryGetKeyPosition(12, 85, out double japaneseX, out double japaneseY));
        Assert.Equal(4, japaneseX);
        Assert.Equal(5, japaneseY);
        Assert.True(Blade18KeyboardLightingLayout.TryGetKeyPosition(12, 84, out double japaneseAltX, out double japaneseAltY));
        Assert.Equal(3, japaneseAltX);
        Assert.Equal(5, japaneseAltY);
        Assert.False(Blade18KeyboardLightingLayout.TryGetKeyPosition(1, 84, out _, out _));

        Assert.True(Blade18KeyboardLightingLayout.TryGetKeyPosition(85, out double canonicalX, out double canonicalY));
        Assert.Equal(3, canonicalX);
        Assert.Equal(5, canonicalY);
        Assert.True(Blade18KeyboardLightingLayout.TryGetKeyPosition(2, 85, out canonicalX, out canonicalY));
        Assert.Equal(3, canonicalX);
        Assert.Equal(5, canonicalY);
        Assert.True(Blade18KeyboardLightingLayout.TryGetKeyPosition(84, out double fallbackX, out double fallbackY));
        Assert.Equal(3, fallbackX);
        Assert.Equal(5, fallbackY);
    }

    [Theory]
    [InlineData(1, 80, 82)]
    [InlineData(3, 81, 83)]
    [InlineData(4, 81, 83)]
    [InlineData(6, 81, 83)]
    [InlineData(7, 81, 83)]
    [InlineData(8, 80, 82)]
    [InlineData(9, 80, 82)]
    [InlineData(12, 84, 87)]
    [InlineData(16, 80, 82)]
    public void ExactLayoutMapsAreBoundedAndTargetDeclaredPositions(
        byte layoutId,
        int expectedPositionCount,
        int expectedInputCount)
    {
        int positionCount = Enumerable.Range(0, Blade18KeyboardLightingLayout.SlotCount)
            .Count(slot => Blade18KeyboardLightingLayout.TryGetKeyPosition(
                layoutId, slot, out _, out _));
        Assert.Equal(expectedPositionCount, positionCount);

        int inputCount = 0;
        for (ushort scanCode = 0; scanCode < 256; scanCode++)
        {
            foreach (bool extended in new[] { false, true })
            {
                if (Blade18KeyboardLightingLayout.TryMapScanCode(
                    layoutId, scanCode, extended, out byte slot))
                {
                    inputCount++;
                    Assert.True(Blade18KeyboardLightingLayout.IsKeySlot(slot));
                    Assert.True(Blade18KeyboardLightingLayout.TryGetKeyPosition(
                        layoutId, slot, out _, out _));
                }
            }
        }

        Assert.Equal(expectedInputCount, inputCount);
    }

    [Fact]
    public void FallbackAndUnionRemainBounded()
    {
        Assert.Equal(87, Enumerable.Range(0, Blade18KeyboardLightingLayout.SlotCount)
            .Count(Blade18KeyboardLightingLayout.IsKeySlot));

        int fallbackInputCount = 0;
        for (ushort scanCode = 0; scanCode < 256; scanCode++)
        {
            foreach (bool extended in new[] { false, true })
            {
                if (Blade18KeyboardLightingLayout.TryMapScanCode(
                    scanCode, extended, out byte slot))
                {
                    fallbackInputCount++;
                    Assert.True(Blade18KeyboardLightingLayout.IsKeySlot(slot));
                }
            }
        }

        Assert.Equal(84, fallbackInputCount);
        foreach (int slot in new[] { -1, 0, 16, 32, 48, 64, 80, 96, int.MaxValue })
        {
            Assert.False(Blade18KeyboardLightingLayout.IsKeySlot(slot));
            Assert.False(Blade18KeyboardLightingLayout.TryGetKeyPosition(slot, out _, out _));
            Assert.False(Blade18KeyboardLightingLayout.TryGetKeyPosition(12, slot, out _, out _));
        }
    }

    [Fact]
    public void DeviceCapabilitiesJsonIncludesLayoutAndValidationRejectsUnknownValue()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            Blade18KeyboardLayoutId = 12,
        };

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(capabilities));

        Assert.Equal(
            (byte)12,
            document.RootElement.GetProperty(nameof(DeviceCapabilities.Blade18KeyboardLayoutId)).GetByte());
        Assert.True(IsWellFormed(capabilities));
        Assert.False(IsWellFormed(capabilities with { Blade18KeyboardLayoutId = 2 }));
    }

    private static void AssertMap(
        byte? layoutId,
        ushort scanCode,
        bool extended,
        byte expectedSlot)
    {
        Assert.True(Blade18KeyboardLightingLayout.TryMapScanCode(
            layoutId, scanCode, extended, out byte slot));
        Assert.Equal(expectedSlot, slot);
        Assert.True(Blade18KeyboardLightingLayout.IsKeySlot(slot));
    }

    private static bool IsWellFormed(DeviceCapabilities capabilities) =>
        (bool)typeof(DeviceCapabilities)
            .GetProperty("IsWellFormed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(capabilities)!;
}
