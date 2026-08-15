using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ImportedSequenceLightingEffectsTests
{
    [Fact]
    public void Pid02E0Renderer_SelectsFramesAndLoopsAtExactDurations()
    {
        SequenceLightingParameters parameters = Parameters(
            KeyboardLightingLayoutIds.Rz09_0581,
            loop: true);
        var effect = new Pid02E0ImportedSequenceEffect(parameters);

        Assert.Equal(new MatrixRgb(1, 2, 3), effect.RenderFrame(TimeSpan.Zero)[59]);
        Assert.Equal(new MatrixRgb(4, 5, 6), effect.RenderFrame(
            TimeSpan.FromMilliseconds(100))[59]);
        Assert.Equal(new MatrixRgb(1, 2, 3), effect.RenderFrame(
            TimeSpan.FromMilliseconds(300))[59]);
        Assert.Equal(TimeSpan.FromMilliseconds(100), effect.GetNextFrameDelay(TimeSpan.Zero));
    }

    [Fact]
    public void Pid02E0Renderer_HoldsTheFinalNonLoopingFrame()
    {
        var effect = new Pid02E0ImportedSequenceEffect(Parameters(
            KeyboardLightingLayoutIds.Rz09_0581,
            loop: false));

        Assert.Equal(new MatrixRgb(4, 5, 6), effect.RenderFrame(
            TimeSpan.FromSeconds(10))[59]);
        Assert.Null(effect.GetNextFrameDelay(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Rz090528Renderer_UsesTheIndependentTypedTimeline()
    {
        var effect = new Rz090528ImportedSequenceEffect(Parameters(
            KeyboardLightingLayoutIds.Rz09_0528,
            loop: true));

        Assert.True(effect.RequiresContinuousRendering);
        Assert.Equal(new RgbColor(4, 5, 6), effect.RenderFrame(
            TimeSpan.FromMilliseconds(100))[59]);
        Assert.Equal(TimeSpan.FromMilliseconds(200), effect.GetNextFrameDelay(
            TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void Renderers_RejectTheOtherModelsLayout()
    {
        Assert.Throws<ArgumentException>(() => new Pid02E0ImportedSequenceEffect(
            Parameters(KeyboardLightingLayoutIds.Rz09_0528, loop: true)));
        Assert.Throws<ArgumentException>(() => new Rz090528ImportedSequenceEffect(
            Parameters(KeyboardLightingLayoutIds.Rz09_0581, loop: true)));
    }

    private static SequenceLightingParameters Parameters(string layoutId, bool loop)
    {
        RgbColor[] first = Slots(new RgbColor(1, 2, 3));
        RgbColor[] second = Slots(new RgbColor(4, 5, 6));
        string timeline = KeyboardLightingSequenceCodec.Encode(
        [
            new KeyboardLightingSequenceFrame(100, first),
            new KeyboardLightingSequenceFrame(200, second),
        ]);
        return new SequenceLightingParameters(layoutId, timeline, loop);
    }

    private static RgbColor[] Slots(RgbColor color)
    {
        var slots = new RgbColor[AmbientLightingFrame.SlotCount];
        slots[59] = color;
        return slots;
    }
}
