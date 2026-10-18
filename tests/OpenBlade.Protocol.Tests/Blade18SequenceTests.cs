using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    private static LightingSettings SequenceSetting(bool loop = true, bool oneFrame = false)
    {
        var first = new RgbColor[AmbientLightingFrame.SlotCount];
        first[1] = new(255, 0, 0);
        first[34] = new(0, 255, 0);
        var second = new RgbColor[AmbientLightingFrame.SlotCount];
        second[1] = new(0, 0, 255);
        KeyboardLightingSequenceFrame[] frames = oneFrame
            ? [new(100, first)] : [new(100, first), new(200, second)];
        return LightingSettingsFactory.Sequence(KeyboardLightingLayoutIds.Rz09_0484,
            KeyboardLightingSequenceCodec.Encode(frames, KeyboardLightingLayoutIds.Rz09_0484), loop, 50);
    }

    [Fact]
    public void Blade18Sequence_UsesOwnGeometryAndTimelineForBothLoopModes()
    {
        var looping = new Blade18ImportedSequenceEffect((SequenceLightingParameters)SequenceSetting().Parameters!, 1);
        Assert.Equal(96, looping.RenderFrame(TimeSpan.Zero).Slots.Count);
        Assert.Equal(new RgbColor(255, 0, 0), looping.RenderFrame(TimeSpan.Zero).Slots[1]);
        Assert.Equal(new RgbColor(0, 0, 255), looping.RenderFrame(TimeSpan.FromMilliseconds(100)).Slots[1]);
        Assert.Equal(new RgbColor(255, 0, 0), looping.RenderFrame(TimeSpan.FromMilliseconds(300)).Slots[1]);
        var once = new Blade18ImportedSequenceEffect((SequenceLightingParameters)SequenceSetting(false).Parameters!, 1);
        Assert.Null(once.GetNextFrameDelay(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(new RgbColor(0, 0, 255), once.RenderFrame(TimeSpan.FromHours(1)).Slots[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(96)]
    [InlineData(101)]
    public void Blade18Sequence_RejectsTransportHolesAndContainerPadding(int invalidSlot)
    {
        var slots = new RgbColor[AmbientLightingFrame.SlotCount];
        slots[invalidSlot] = new(1, 2, 3);
        Assert.Throws<InvalidDataException>(() => KeyboardLightingSequenceCodec.Encode(
            [new(100, slots)], KeyboardLightingLayoutIds.Rz09_0484));
    }

    [Fact]
    public async Task Blade18PerKeyStatic_WritesOneFrameAndRestoresWithoutAnAnimationLoop()
    {
        var clock = new AnimationClock();
        var session = new Session();
        await using var port = AnimatedPort(session, clock);
        await port.SetLightingAsync(SequenceSetting(oneFrame: true), CancellationToken.None);
        Assert.Equal(LightingRendererOwnershipState.Sequence, port.OwnershipState);
        Assert.Equal(1, session.AnimationBatches);
        Assert.Equal(0, clock.TimerCount);
        RazerCommandSpec[] rows = session.Commands.Where(c => c.ClassId == 3 && c.CommandId == 0x0b).ToArray();
        Assert.Equal(6, rows.Length);
        Assert.Equal(new byte[] { 255, 0, 0 }, rows[0].Arguments.Span.Slice(7, 3).ToArray());
        Assert.Equal(new byte[] { 0, 255, 0 }, rows[2].Arguments.Span.Slice(10, 3).ToArray());
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, session.AnimationBatches);
        await port.RelinquishRendererAsync(CancellationToken.None);
        Assert.Equal(3, session.Effect);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    [Fact]
    public async Task Blade18SequenceOnce_HoldsFinalFrameAndStopsSubmitting()
    {
        var clock = new AnimationClock();
        var session = new Session();
        await using var port = AnimatedPort(session, clock);
        await port.SetLightingAsync(SequenceSetting(loop: false), CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => session.AnimationBatches == 2);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, session.AnimationBatches);
        Assert.Equal(LightingRendererOwnershipState.Sequence, port.OwnershipState);
        await port.RelinquishRendererAsync(CancellationToken.None);
        Assert.Equal(3, session.Effect);
    }

    [Fact]
    public async Task Blade18SequenceOnce_DeliversFinalFrameAfterASlowIntermediateBatch()
    {
        var clock = new AnimationClock();
        var session = new Session
        {
            AfterAnimationBatch = batch =>
        {
            if (batch == 2)
            {
                clock.Advance(TimeSpan.FromMilliseconds(500));
            }
        }
        };
        var red = new RgbColor[AmbientLightingFrame.SlotCount];
        var green = new RgbColor[AmbientLightingFrame.SlotCount];
        var blue = new RgbColor[AmbientLightingFrame.SlotCount];
        red[1] = new(255, 0, 0); green[1] = new(0, 255, 0); blue[1] = new(0, 0, 255);
        LightingSettings setting = LightingSettingsFactory.Sequence(KeyboardLightingLayoutIds.Rz09_0484,
            KeyboardLightingSequenceCodec.Encode([new(50, red), new(50, green), new(50, blue)],
                KeyboardLightingLayoutIds.Rz09_0484), false, 50);
        await using var port = AnimatedPort(session, clock);
        await port.SetLightingAsync(setting, CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(2);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await WaitUntilAsync(() => session.AnimationBatches == 3);
        RazerCommandSpec lastRowZero = session.Commands.Where(c => c.ClassId == 3 && c.CommandId == 0x0b).TakeLast(6).First();
        Assert.Equal(new byte[] { 0, 0, 255 }, lastRowZero.Arguments.Span.Slice(7, 3).ToArray());
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(3, session.AnimationBatches);
    }

    [Fact]
    public async Task Blade18Sequence_PartialFrameFailurePausesWithoutReplay()
    {
        var clock = new AnimationClock();
        var session = new Session { FailAnimationBatch = 2 };
        var policy = Enabled();
        await using var port = AnimatedPort(session, clock, policy);
        await port.SetLightingAsync(SequenceSetting(), CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => port.OwnershipState == LightingRendererOwnershipState.Faulted);
        Assert.False(policy.WritesAllowed);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2, session.AnimationBatches);
    }
}
