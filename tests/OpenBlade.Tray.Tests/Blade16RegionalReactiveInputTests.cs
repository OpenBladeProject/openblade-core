using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class Blade16RegionalReactiveInputTests
{
    [Theory]
    [InlineData(0x56, 70, 3, 70)]
    [InlineData(0x73, 82, 12, 82)]
    [InlineData(0x70, 64, 12, 94)]
    [InlineData(0x7D, 31, 12, 31)]
    [InlineData(0x79, 93, 12, 93)]
    [InlineData(0x7B, 48, 12, 90)]
    public void RegionalKeys_UseStandardScanCodesAndEachModelsSourceMap(
        ushort scan, byte canvas, byte layout, byte physical)
    {
        Assert.True(Rz09_0581ReactiveKeyMap.TryMap(scan, false, out ReactiveKeyPulse first, out _));
        Assert.Equal(canvas, first.MatrixSlot);
        Assert.True(Blade16KeyboardLightingLayout.TryMapCanvasSlot(canvas, layout, out byte firstPhysical));
        Assert.Equal(physical, firstPhysical);
        Assert.True(Rz09_0528ReactiveKeyMap.TryMap(scan, false, out ReactiveKeyPulse second, out _));
        Assert.Equal(canvas, second.MatrixSlot);
        Assert.True(Blade16AmdKeyboardLightingLayout.TryMapCanvasSlot(canvas, layout, out byte secondPhysical));
        Assert.Equal(physical, secondPhysical);
        Assert.False(Rz09_0581ReactiveKeyMap.TryMap(scan, true, out _, out _));
        Assert.False(Rz09_0528ReactiveKeyMap.TryMap(scan, true, out _, out _));
    }
}
