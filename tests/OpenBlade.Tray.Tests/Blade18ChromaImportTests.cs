using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class Blade18ChromaImportTests
{
    [Theory]
    [InlineData(1, 63)]
    [InlineData(3, 46)]
    [InlineData(12, 46)]
    public void PerKeyAnimationProjectsThroughItsOwnRegionalMap(byte region, byte enter)
    {
        byte[] content = ChromaFile((3, 14), (2, 2));
        KeyboardLightingPreset preset = RazerChromaFileAdapter.Deserialize(content, "Regional", KeyboardLightingLayoutIds.Rz09_0484, region);
        var parameters = Assert.IsType<SequenceLightingParameters>(preset.Lighting.Parameters);
        KeyboardLightingSequence timeline = KeyboardLightingSequenceCodec.Decode(parameters.TimelineBase64, parameters.LayoutId);
        Assert.Equal(2, timeline.Frames.Count);
        Assert.Equal(new RgbColor(255, 0, 0), timeline.Frames[0].Slots[enter]);
        Assert.Equal(new RgbColor(255, 0, 0), timeline.Frames[1].Slots[34]);
        Assert.True(LightingSettingsValidation.IsWellFormed(preset.Lighting));
        Assert.Throws<InvalidDataException>(() => KeyboardLightingSequenceCodec.Decode(parameters.TimelineBase64));
        Assert.False(LightingSettingsValidation.IsWellFormed(preset.Lighting with
        { Parameters = parameters with { LayoutId = KeyboardLightingLayoutIds.Rz09_0581 } }));
    }

    [Fact]
    public void SinglePerKeyFrameRemainsStaticInsteadOfBecomingAWholeKeyboardColor()
    {
        KeyboardLightingPreset preset = RazerChromaFileAdapter.Deserialize(
            ChromaFile((3, 2)), "A only", KeyboardLightingLayoutIds.Rz09_0484, 1);
        var parameters = Assert.IsType<SequenceLightingParameters>(preset.Lighting.Parameters);
        KeyboardLightingSequence timeline = KeyboardLightingSequenceCodec.Decode(parameters.TimelineBase64, parameters.LayoutId);
        Assert.Single(timeline.Frames);
        Assert.Equal(new RgbColor(255, 0, 0), timeline.Frames[0].Slots[50]);
        Assert.Single(timeline.Frames[0].Slots, color => color != default);
    }

    private static byte[] ChromaFile(params (int Row, int Column)[] frames)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1); writer.Write((byte)1); writer.Write((byte)0); writer.Write((uint)frames.Length);
        foreach (var frame in frames)
        {
            writer.Write(0.1f);
            for (int i = 0; i < 6 * 22; i++)
            {
                writer.Write(i == frame.Row * 22 + frame.Column ? 255u : 0u);
            }
        }
        return stream.ToArray();
    }
}
