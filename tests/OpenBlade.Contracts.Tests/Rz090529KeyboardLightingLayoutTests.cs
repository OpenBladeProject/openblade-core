using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class Rz090529KeyboardLightingLayoutTests
{
    [Fact]
    public void SanitizedFixtureAndExactVendorSourceRemainPinned()
    {
        using Stream stream = typeof(Rz090529KeyboardLightingLayout).Assembly.GetManifestResourceStream(
            "OpenBlade.Contracts.Fixtures.Rz090529KeyboardLightingLayout.json")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        Assert.Equal("71B8324BFA5EECA880701B032F159A4357A09046C718565236DA73DE7A095613",
            Convert.ToHexString(SHA256.HashData(bytes)));
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        Assert.Equal("RZ09-0529", root.GetProperty("model").GetString());
        Assert.Equal(0x1532, root.GetProperty("vendorId").GetInt32());
        Assert.Equal(0x02C7, root.GetProperty("productId").GetInt32());
        Assert.Equal("https://apps.razer.com/synapse/lighting-engine/manifest/DeviceManifest_711_0.json",
            root.GetProperty("sourceUrl").GetString());
        Assert.Equal("FDD5D817BF28F152B3DF498AC6E908291993BBCC6939BE4D86F94AD74F52A9F1",
            root.GetProperty("sourceSha256").GetString());
        Assert.Equal(new[] { 1, 3, 4, 6, 7, 8, 9, 12, 16 },
            root.GetProperty("layoutIds").EnumerateArray().Select(value => value.GetInt32()));
    }

    [Theory]
    [InlineData(0x01, false, 1)]
    [InlineData(0x1e, false, 59)]
    [InlineData(0x20, false, 61)]
    [InlineData(0x1c, true, 113)]
    [InlineData(0x50, true, 110)]
    [InlineData(0x39, false, 101)]
    public void CommonScanCodesUseExactDeviceSlots(int scanCode, bool extended, byte expected)
    {
        Assert.True(Rz090529KeyboardLightingLayout.TryMapScanCode((ushort)scanCode, extended, out byte slot));
        Assert.Equal(expected, slot);
        Assert.True(Rz090529KeyboardLightingLayout.IsKeySlot(slot));
    }

    [Theory]
    [InlineData(0x1c, false)]
    [InlineData(0x2b, false)]
    [InlineData(0x38, false)]
    [InlineData(0x38, true)]
    [InlineData(0x56, false)]
    [InlineData(0x70, false)]
    [InlineData(0xd2, true)]
    [InlineData(0, false)]
    [InlineData(0xffff, false)]
    public void RegionalConflictsNonKeyboardIdentifiersAndForeignInputsAreExcluded(int scanCode, bool extended)
    {
        Assert.False(Rz090529KeyboardLightingLayout.TryMapScanCode((ushort)scanCode, extended, out byte slot));
        Assert.Equal(0, slot);
    }

    [Fact]
    public void CommonTransportAndSpatialMapsRemainDistinctAndBounded()
    {
        Assert.Equal(6, Rz090529KeyboardLightingLayout.Rows);
        Assert.Equal(19, Rz090529KeyboardLightingLayout.Columns);
        Assert.Equal(114, Rz090529KeyboardLightingLayout.SlotCount);
        Assert.Equal(98, Enumerable.Range(0, 114).Count(Rz090529KeyboardLightingLayout.IsKeySlot));
        Assert.Equal(96, Enumerable.Range(0, 114).Count(slot =>
            Rz090529KeyboardLightingLayout.TryGetKeyPosition(slot, out _, out _)));
        int inputCount = 0;
        for (ushort scan = 0; scan < 256; scan++)
        {
            foreach (bool extended in new[] { false, true })
            {
                if (Rz090529KeyboardLightingLayout.TryMapScanCode(scan, extended, out byte slot))
                {
                    inputCount++;
                    Assert.InRange(slot, (byte)0, (byte)113);
                    Assert.True(Rz090529KeyboardLightingLayout.IsKeySlot(slot));
                }
            }
        }
        Assert.Equal(94, inputCount);
        foreach (int slot in Enumerable.Range(0, 114))
        {
            if (Rz090529KeyboardLightingLayout.TryGetKeyPosition(slot, out double x, out double y))
            {
                Assert.True(Rz090529KeyboardLightingLayout.IsKeySlot(slot));
                Assert.InRange(x, 0, 17);
                Assert.InRange(y, 0, 7);
            }
        }
    }

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(59, 1, 4)]
    [InlineData(110, 13, 7)]
    public void PositionsUseLogicalCanvasRatherThanTransportCoordinates(int slot, double expectedX, double expectedY)
    {
        Assert.True(Rz090529KeyboardLightingLayout.TryGetKeyPosition(slot, out double x, out double y));
        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    public void ConflictingRegionalPositionsNeverBecomeGuessedPulseOrigins(int slot)
    {
        Assert.True(Rz090529KeyboardLightingLayout.IsKeySlot(slot));
        Assert.False(Rz090529KeyboardLightingLayout.TryGetKeyPosition(slot, out double x, out double y));
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(99)]
    [InlineData(114)]
    [InlineData(132)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void HolesMalformedBoundsAndAuxiliaryRowsHaveNoKeyPosition(int slot)
    {
        Assert.False(Rz090529KeyboardLightingLayout.IsKeySlot(slot));
        Assert.False(Rz090529KeyboardLightingLayout.TryGetKeyPosition(slot, out double x, out double y));
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }
}
