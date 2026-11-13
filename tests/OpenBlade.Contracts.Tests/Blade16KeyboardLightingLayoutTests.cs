using System.Text.Json;

namespace OpenBlade.Contracts.Tests;

public sealed class Blade16KeyboardLightingLayoutTests
{
    [Theory]
    [InlineData(0, 66, 49)]
    [InlineData(8, 66, 49)]
    [InlineData(9, 66, 49)]
    [InlineData(2, 48, 64)]
    [InlineData(7, 48, 64)]
    [InlineData(12, 48, 64)]
    [InlineData(255, 48, 64)]
    public void CanonicalEnterAndBackslashReachSelectedPhysicalKeys(byte layout, byte enter, byte backslash)
    {
        Assert.True(Blade16KeyboardLightingLayout.TryMapCanvasSlot(66, layout, out byte actualEnter));
        Assert.Equal(enter, actualEnter);
        Assert.Equal(0x28, Blade16KeyboardLightingLayout.UsageAt(actualEnter, layout));
        Assert.True(Blade16KeyboardLightingLayout.TryMapCanvasSlot(49, layout, out byte actualBackslash));
        Assert.Equal(backslash, actualBackslash);
        Assert.Contains(Blade16KeyboardLightingLayout.UsageAt(actualBackslash, layout), new byte[] { 0x31, 0x32 });
    }

    [Fact]
    public void Oem102IsSupplementalAndNeverInventedOnOtherLayouts()
    {
        Assert.Contains(70, Blade16KeyboardLightingLayout.CanvasHoleSlots);
        Assert.True(Blade16KeyboardLightingLayout.TryMapCanvasSlot(70, 2, out byte slot));
        Assert.Equal(70, slot);
        Assert.Equal(0x64, Blade16KeyboardLightingLayout.UsageAt(slot, 2));
        Assert.False(Blade16KeyboardLightingLayout.TryMapCanvasSlot(70, 0, out _));
        Assert.False(Blade16KeyboardLightingLayout.TryMapCanvasSlot(70, 12, out _));
        Assert.False(Blade16KeyboardLightingLayout.TryMapCanvasSlot(48, 2, out _));
    }

    [Fact]
    public void CanvasAndPhysicalMasksRemainDistinctAndReadOnly()
    {
        Assert.Equal(new[] { 0, 17, 31, 34, 48, 51, 64, 65, 68, 70, 81, 82, 85, 89, 92, 93 },
            Blade16KeyboardLightingLayout.CanvasHoleSlots);
        Assert.Equal(new[] { 0, 17, 34, 51, 65, 68, 81, 85, 92 }, Blade16KeyboardLightingLayout.UnionHoleSlots);
        Assert.Equal(93, Enumerable.Range(0, 102).Count(slot => Blade16KeyboardLightingLayout.IsKeySlot(slot)));
        Assert.False(Blade16KeyboardLightingLayout.IsKeySlot(48, 0));
        Assert.True(Blade16KeyboardLightingLayout.IsKeySlot(48, 2));
        Assert.True(Blade16KeyboardLightingLayout.IsKeySlot(31, 12));
        Assert.Throws<NotSupportedException>(() => ((IList<int>)Blade16KeyboardLightingLayout.UnionHoleSlots)[0] = 1);
        foreach (int slot in new[] { -1, 102, 255 })
        {
            Assert.False(Blade16KeyboardLightingLayout.IsKeySlot(slot));
            Assert.Equal(0, Blade16KeyboardLightingLayout.UsageAt(slot, 0));
        }
        Assert.False(Blade16KeyboardLightingLayout.TryMapCanvasSlot(255, 0, out _));
    }

    [Fact]
    public void EverySelectorAndCanvasProjectionMatchesPinnedFirmwareUsageIdentity()
    {
        using Stream stream = typeof(Blade16KeyboardLightingLayout).Assembly.GetManifestResourceStream(
            "OpenBlade.Contracts.Fixtures.Blade16KeyboardLightingLayout.json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        Dictionary<int, byte[]> tables = root.GetProperty("tables").EnumerateArray().ToDictionary(
            table => table.GetProperty("offset").GetInt32(),
            table => table.GetProperty("hidUsages").EnumerateArray().Select(x => x.GetByte()).ToArray());
        byte[] canvas = tables[0xB345];
        foreach (JsonElement selection in root.GetProperty("selector").GetProperty("allByteValues").EnumerateArray())
        {
            byte layout = selection.GetProperty("layoutId").GetByte();
            byte[] target = tables[selection.GetProperty("tableOffset").GetInt32()];
            for (byte slot = 0; slot < 102; slot++)
            {
                Assert.Equal(target[slot], Blade16KeyboardLightingLayout.UsageAt(slot, layout));
                Assert.Equal(target[slot] != 0, Blade16KeyboardLightingLayout.IsKeySlot(slot, layout));
                byte usage = slot switch { 31 => 0x89, 48 => 0x8B, 64 => 0x88, 70 => 0x64, 82 => 0x87, 93 => 0x8A, _ => canvas[slot] };
                if (usage == 0x31 && !target.Contains(usage))
                {
                    usage = 0x32;
                }
                bool expected = usage != 0 && target.Contains(usage);
                Assert.Equal(expected, Blade16KeyboardLightingLayout.TryMapCanvasSlot(slot, layout, out byte physical));
                if (expected)
                {
                    Assert.Equal(usage, target[physical]);
                    if (target[slot] == usage)
                    {
                        Assert.Equal(slot, physical);
                    }
                }
            }
        }
    }
}
