using OpenBlade.Contracts;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ServiceAudioMeterLightingLoopTests
{
    [Fact]
    public void RenderFrame_MapsPeakAcrossColumnsAndPreservesHoles()
    {
        AmbientLightingFrame frame =
            WindowsAudioMeterLightingSampler.RenderFrame(0.5f, colorBoost: 1);

        Assert.True(frame.IsWellFormed());
        Assert.NotEqual(default, frame.Slots[1]);
        Assert.NotEqual(default, frame.Slots[8]);
        Assert.Equal(default, frame.Slots[9]);
        foreach (int hole in AmbientLightingFrame.LayoutHoleSlots)
        {
            Assert.Equal(default, frame.Slots[hole]);
        }
    }

    [Fact]
    public void RenderFrame_ColorBoostIncreasesLitColumns()
    {
        AmbientLightingFrame normal =
            WindowsAudioMeterLightingSampler.RenderFrame(0.1f, colorBoost: 1);
        AmbientLightingFrame boosted =
            WindowsAudioMeterLightingSampler.RenderFrame(0.1f, colorBoost: 5);

        Assert.True(
            boosted.Slots.Count(color => color != default)
            > normal.Slots.Count(color => color != default));
    }

    [Theory]
    [InlineData(-0.1f, 1)]
    [InlineData(1.1f, 1)]
    [InlineData(0.5f, 0)]
    [InlineData(0.5f, 11)]
    public void RenderFrame_RejectsInvalidInputs(float peak, byte boost)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowsAudioMeterLightingSampler.RenderFrame(peak, boost));
    }
}
