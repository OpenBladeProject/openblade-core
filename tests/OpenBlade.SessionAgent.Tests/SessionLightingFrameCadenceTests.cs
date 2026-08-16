using OpenBlade.Contracts;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionLightingFrameCadenceTests
{
    [Fact]
    public void MovingContentKeepsThirtyFramesPerSecond()
    {
        var time = new ManualTimeProvider();
        var cadence = new SessionLightingFrameCadence(time);

        Assert.Equal(
            TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30),
            SessionLightingFrameCadence.ActiveInterval);
        for (int index = 0; index < 100; index++)
        {
            AmbientLightingFrame frame = Frame((byte)(index % 2 + 1));
            SessionLightingFrameDecision decision = cadence.Evaluate(frame);
            Assert.Equal(SessionLightingFrameCadence.ActiveInterval, decision.NextInterval);
            cadence.RecordSent(frame);
        }
    }

    [Fact]
    public void SixtyHzModeDoublesActiveCadenceAndPreservesHalfSecondBackoff()
    {
        var time = new ManualTimeProvider();
        var cadence = new SessionLightingFrameCadence(
            time,
            LightingRefreshRate.SixtyHz);
        AmbientLightingFrame frame = Frame(1);
        cadence.RecordSent(frame);
        TimeSpan interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

        for (int index = 1; index < 30; index++)
        {
            Assert.Equal(interval, cadence.Evaluate(frame).NextInterval);
        }

        Assert.Equal(
            SessionLightingFrameCadence.StaticInterval,
            cadence.Evaluate(frame).NextInterval);
    }

    [Fact]
    public async Task PacerUsesAbsoluteDeadlinesAndSkipsMissedFrames()
    {
        var time = new ManualTimeProvider();
        var waits = new List<TimeSpan>();
        var pacer = new SessionLightingFramePacer(
            time,
            (interval, _) =>
            {
                waits.Add(interval);
                time.Advance(interval);
                return Task.CompletedTask;
            });
        TimeSpan interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

        await pacer.WaitAsync(interval, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(10));
        await pacer.WaitAsync(interval, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(30));
        await pacer.WaitAsync(interval, CancellationToken.None);

        Assert.Equal(interval, waits[0]);
        Assert.Equal(interval - TimeSpan.FromMilliseconds(10), waits[1]);
        Assert.InRange(
            waits[2],
            TimeSpan.FromMilliseconds(3.32),
            TimeSpan.FromMilliseconds(3.34));
    }

    [Fact]
    public void StaticContentSlowsToFiveFramesPerSecondAfterHalfASecond()
    {
        var time = new ManualTimeProvider();
        var cadence = new SessionLightingFrameCadence(time);
        AmbientLightingFrame frame = Frame(1);
        cadence.RecordSent(frame);

        Assert.Equal(
            TimeSpan.FromMilliseconds(200),
            SessionLightingFrameCadence.StaticInterval);
        for (int index = 1;
            index < SessionLightingFrameCadence.StaticFrameThreshold;
            index++)
        {
            Assert.Equal(
                SessionLightingFrameCadence.ActiveInterval,
                cadence.Evaluate(frame).NextInterval);
        }

        Assert.Equal(
            SessionLightingFrameCadence.StaticInterval,
            cadence.Evaluate(frame).NextInterval);
        Assert.Equal(
            SessionLightingFrameCadence.StaticInterval,
            cadence.Evaluate(frame).NextInterval);
    }

    [Fact]
    public void ChangedFrameImmediatelyRestoresActiveCadence()
    {
        var time = new ManualTimeProvider();
        var cadence = new SessionLightingFrameCadence(time);
        AmbientLightingFrame first = Frame(1);
        cadence.RecordSent(first);

        for (int index = 0;
            index < SessionLightingFrameCadence.StaticFrameThreshold;
            index++)
        {
            _ = cadence.Evaluate(first);
        }

        AmbientLightingFrame changed = Frame(2);
        Assert.Equal(
            SessionLightingFrameCadence.ActiveInterval,
            cadence.Evaluate(changed).NextInterval);
        cadence.RecordSent(changed);
        Assert.Equal(
            SessionLightingFrameCadence.ActiveInterval,
            cadence.Evaluate(changed).NextInterval);
    }

    [Fact]
    public void StaticFrameIsSentAgainAfterKeepalive()
    {
        var time = new ManualTimeProvider();
        var cadence = new SessionLightingFrameCadence(time);
        AmbientLightingFrame frame = Frame(1);

        Assert.True(cadence.Evaluate(frame).ShouldSend);
        cadence.RecordSent(frame);
        Assert.False(cadence.Evaluate(frame).ShouldSend);

        time.Advance(SessionLightingFrameCadence.KeepaliveInterval);

        Assert.True(cadence.Evaluate(frame).ShouldSend);
    }

    private static AmbientLightingFrame Frame(byte red)
    {
        var slots = new RgbColor[AmbientLightingFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (!AmbientLightingFrame.IsHole(slot))
            {
                slots[slot] = new(red, 0, 0);
            }
        }
        return new(slots);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        private long timestamp;

        public override DateTimeOffset GetUtcNow() => now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        internal void Advance(TimeSpan elapsed)
        {
            now += elapsed;
            timestamp += elapsed.Ticks;
        }
    }
}
