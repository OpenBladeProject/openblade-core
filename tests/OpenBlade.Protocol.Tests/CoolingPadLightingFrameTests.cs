using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CoolingPadLightingFrameTests
{
    [Fact]
    public void Constructor_RequiresExactlyEighteenLogicalSlots()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CoolingPadLightingFrame(new RgbColor[CoolingPadLightingFrame.SlotCount - 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CoolingPadLightingFrame(new RgbColor[CoolingPadLightingFrame.SlotCount + 1]));
    }

    [Fact]
    public void ConstructorAndToArray_DefensivelyCopySlots()
    {
        RgbColor[] source = new RgbColor[CoolingPadLightingFrame.SlotCount];
        source[0] = new RgbColor(0x12, 0x34, 0x56);

        var frame = new CoolingPadLightingFrame(source);
        source[0] = default;
        RgbColor[] copy = frame.ToArray();
        copy[0] = default;

        Assert.Equal(new RgbColor(0x12, 0x34, 0x56), frame[0]);
        Assert.Equal(1, CoolingPadLightingFrame.RowCount);
        Assert.Equal(18, CoolingPadLightingFrame.SlotCount);
    }
}
