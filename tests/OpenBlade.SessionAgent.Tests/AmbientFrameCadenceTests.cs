using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class AmbientFrameCadenceTests
{
    [Fact]
    public void MovingContentKeepsThirtyFramesPerSecond()
    {
        var cadence = new AmbientFrameCadence();

        Assert.Equal(
            TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30),
            AmbientFrameCadence.ActiveInterval);
        for (int index = 0; index < 100; index++)
        {
            Assert.Equal(AmbientFrameCadence.ActiveInterval, cadence.RecordFrame(changed: true));
        }
    }

    [Fact]
    public void StaticContentSlowsToFiveFramesPerSecondAfterHalfASecond()
    {
        var cadence = new AmbientFrameCadence();

        Assert.Equal(TimeSpan.FromMilliseconds(200), AmbientFrameCadence.StaticInterval);
        for (int index = 1; index < AmbientFrameCadence.StaticFrameThreshold; index++)
        {
            Assert.Equal(AmbientFrameCadence.ActiveInterval, cadence.RecordFrame(changed: false));
        }

        Assert.Equal(AmbientFrameCadence.StaticInterval, cadence.RecordFrame(changed: false));
        Assert.Equal(AmbientFrameCadence.StaticInterval, cadence.RecordFrame(changed: false));
    }

    [Fact]
    public void ChangedFrameImmediatelyRestoresActiveCadence()
    {
        var cadence = new AmbientFrameCadence();

        for (int index = 0; index < AmbientFrameCadence.StaticFrameThreshold; index++)
        {
            _ = cadence.RecordFrame(changed: false);
        }

        Assert.Equal(AmbientFrameCadence.ActiveInterval, cadence.RecordFrame(changed: true));
        Assert.Equal(AmbientFrameCadence.ActiveInterval, cadence.RecordFrame(changed: false));
    }
}
