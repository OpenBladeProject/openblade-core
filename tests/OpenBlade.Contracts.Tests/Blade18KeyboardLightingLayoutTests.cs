using System.Security.Cryptography;
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
            "446751B434F3B3EE7E55DE49D3BB93FEDEE4D48468BCE85EF19497D41DEF05EC",
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    [Theory]
    [InlineData(0x01, false, 1)] // Escape
    [InlineData(0x1e, false, 50)] // A
    [InlineData(0x20, false, 52)] // D
    [InlineData(0x39, false, 86)] // Space
    [InlineData(0x53, true, 15)] // Delete, corrected by every exact-product UI layout
    [InlineData(0x52, true, 14)] // Insert is a separate address
    [InlineData(0x48, true, 93)] // Up
    [InlineData(0x50, true, 95)] // Down is not adjacent to Up in the spatial matrix
    [InlineData(0x4b, true, 92)] // Left
    [InlineData(0x4d, true, 94)] // Right
    public void CommonPhysicalKeysUseExactBlade18Addresses(int scanCode, bool extended, byte expected)
    {
        Assert.True(Blade18KeyboardLightingLayout.TryMapScanCode((ushort)scanCode, extended, out byte slot));
        Assert.Equal(expected, slot);
        Assert.True(Blade18KeyboardLightingLayout.IsKeySlot(slot));
    }

    [Theory]
    [InlineData(0x1c, false)] // Enter differs between ANSI and ISO
    [InlineData(0x2b, false)] // Backslash differs between ANSI and ISO
    [InlineData(0x38, false)] // Japanese left Alt differs
    [InlineData(0x38, true)] // Japanese right Alt differs
    [InlineData(0x1d, true)] // Japanese right Ctrl differs
    [InlineData(0x56, false)] // Regional extra key is not common to all layouts
    [InlineData(0x50, false)] // Numpad 2 must not become Down
    [InlineData(0xffff, false)]
    public void AmbiguousRegionalAndForeignInputsAreNotAssigned(int scanCode, bool extended)
    {
        Assert.False(Blade18KeyboardLightingLayout.TryMapScanCode((ushort)scanCode, extended, out _));
    }

    [Fact]
    public void GeometryIncludesRegionalKeysWithoutInferringTheirInputIdentity()
    {
        Assert.Equal(96, Blade18KeyboardLightingLayout.SlotCount);
        Assert.Equal(87, Enumerable.Range(0, 96).Count(Blade18KeyboardLightingLayout.IsKeySlot));
        Assert.True(Blade18KeyboardLightingLayout.IsKeySlot(47)); // ISO Enter / ANSI backslash
        Assert.True(Blade18KeyboardLightingLayout.IsKeySlot(63)); // ANSI Enter
        Assert.True(Blade18KeyboardLightingLayout.IsKeySlot(66)); // ISO extra key
        Assert.True(Blade18KeyboardLightingLayout.IsKeySlot(85)); // Left Alt
        Assert.True(Blade18KeyboardLightingLayout.TryGetKeyPosition(85, out double altX, out double altY));
        Assert.Equal(3, altX); // Canonical layout 1; Japanese layout 12 places it at x=4
        Assert.Equal(5, altY);
        Assert.True(Blade18KeyboardLightingLayout.TryGetKeyPosition(95, out double x, out double y));
        Assert.Equal(13, x);
        Assert.Equal(6, y);
    }

    [Fact]
    public void CommonInputsAreBoundedAndOnlyTargetReviewedAddresses()
    {
        int count = 0;
        for (ushort scanCode = 0; scanCode < 256; scanCode++)
        {
            foreach (bool extended in new[] { false, true })
            {
                if (Blade18KeyboardLightingLayout.TryMapScanCode(scanCode, extended, out byte slot))
                {
                    count++;
                    Assert.True(Blade18KeyboardLightingLayout.IsKeySlot(slot));
                }
            }
        }

        Assert.Equal(77, count);
        foreach (int slot in new[] { -1, 0, 16, 32, 48, 64, 80, 96, int.MaxValue })
        {
            Assert.False(Blade18KeyboardLightingLayout.IsKeySlot(slot));
            Assert.False(Blade18KeyboardLightingLayout.TryGetKeyPosition(slot, out _, out _));
        }
    }
}
