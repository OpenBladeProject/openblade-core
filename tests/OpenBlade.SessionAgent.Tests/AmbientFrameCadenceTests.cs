using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class AmbientFrameCadenceTests
{
    [Fact]
    public void MovingContentKeepsTwentyFramesPerSecond()
    {
        var cadence = new AmbientFrameCadence();

        for (int index = 0; index < 100; index++)
        {
            Assert.Equal(AmbientFrameCadence.ActiveInterval, cadence.RecordFrame(changed: true));
        }
    }

    [Fact]
    public void StaticContentSlowsAfterOneSecondAndResumesImmediatelyOnChange()
    {
        var cadence = new AmbientFrameCadence();

        for (int index = 1; index < AmbientFrameCadence.StaticFrameThreshold; index++)
        {
            Assert.Equal(AmbientFrameCadence.ActiveInterval, cadence.RecordFrame(changed: false));
        }

        Assert.Equal(AmbientFrameCadence.StaticInterval, cadence.RecordFrame(changed: false));
        Assert.Equal(AmbientFrameCadence.StaticInterval, cadence.RecordFrame(changed: false));
        Assert.Equal(AmbientFrameCadence.ActiveInterval, cadence.RecordFrame(changed: true));
        Assert.Equal(AmbientFrameCadence.ActiveInterval, cadence.RecordFrame(changed: false));
    }
}
