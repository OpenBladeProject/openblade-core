using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    [Theory]
    [InlineData(LightingEffect.Reactive)]
    [InlineData(LightingEffect.Ripple)]
    public async Task Blade14MappedPulse_ChangesOnlyMemoryThenRendersAndRetires(LightingEffect effect)
    {
        ExperimentalBladeModel model = ModelFor(0x02C5);
        var session = new Session { Raw = 77, ExpectedFrameTransactionId = 0xFF };
        var clock = new AnimationClock();
        await using var port = new ExperimentalBladePort(session, new(), model, new(model),
            () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
        var color = new RgbColor(240, 20, 0);
        await port.SetLightingAsync(effect == LightingEffect.Reactive
            ? LightingSettingsFactory.Reactive(color, 50, ReactiveLightingDuration.Short)
            : LightingSettingsFactory.Ripple(color, 50), CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        Assert.True(Blade14KeyboardLightingLayout.TryMapScanCode(0x1E, false, out byte slot));
        Assert.Equal(50, slot);
        int before = session.Commands.Count;
        await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(slot), CancellationToken.None);
        Assert.Equal(before, session.Commands.Count);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.PulseReactiveKeyAsync(new ReactiveKeyPulse(96), CancellationToken.None));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(2);
        RgbColor[] frame = DecodeGridRows(session.Commands.Where(IsFrameRow).TakeLast(6).ToArray());
        Assert.NotEqual(default, frame[slot]);
        for (int index = 0; index < frame.Length; index++)
        {
            if (!Blade14KeyboardLightingLayout.IsKeySlot(index)
                || effect == LightingEffect.Reactive && index != slot)
            {
                Assert.Equal(default, frame[index]);
            }
        }
        await port.RelinquishRendererAsync(CancellationToken.None);
        Assert.Equal(77, session.Raw);
        Assert.Equal(3, session.Effect);
        int stopped = session.Commands.Count;
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(stopped, session.Commands.Count);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.PulseReactiveKeyAsync(new ReactiveKeyPulse(slot), CancellationToken.None));
    }

    [Fact]
    public async Task Blade14Sequence_UsesOwnSlotsAndStopsAtLastFrameWithoutAQueue()
    {
        ExperimentalBladeModel model = ModelFor(0x02C5);
        var clock = new AnimationClock();
        var session = new Session { ExpectedFrameTransactionId = 0xFF };
        await using var port = new ExperimentalBladePort(session, new(), model, new(model),
            () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
        var first = new RgbColor[AmbientLightingFrame.SlotCount];
        var last = new RgbColor[AmbientLightingFrame.SlotCount];
        first[50] = new(255, 0, 0);
        last[52] = new(0, 0, 255);
        string encoded = KeyboardLightingSequenceCodec.Encode(
            [new(200, first), new(200, last)], KeyboardLightingLayoutIds.Rz09_0530);
        await port.SetLightingAsync(LightingSettingsFactory.Sequence(
            KeyboardLightingLayoutIds.Rz09_0530, encoded, loop: false, brightness: 50), CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        Assert.Equal(first.Take(96), DecodeGridRows(session.Commands.Where(IsFrameRow).ToArray()));
        clock.Advance(TimeSpan.FromMilliseconds(200));
        await WaitUntilAsync(() => session.Commands.Count(IsFrameRow) == 12);
        Assert.Equal(last.Take(96), DecodeGridRows(session.Commands.Where(IsFrameRow).TakeLast(6).ToArray()));
        Assert.Equal(LightingRendererOwnershipState.Sequence, port.OwnershipState);
        int completed = session.Commands.Count;
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(completed, session.Commands.Count);
        await port.RelinquishRendererAsync(CancellationToken.None);
        Assert.Equal(3, session.Effect);
    }

    [Theory]
    [InlineData(LightingEffect.Reactive)]
    [InlineData(LightingEffect.Ripple)]
    public void Blade14PulsePattern_FadesToBlackAndRejectsUnmappedKeys(LightingEffect effect)
    {
        var color = new RgbColor(255, 30, 10);
        var renderer = new ExperimentalGridLightingEffect(effect == LightingEffect.Reactive
            ? LightingSettingsFactory.Reactive(color, 50, ReactiveLightingDuration.Short)
            : LightingSettingsFactory.Ripple(color, 50));
        renderer.Pulse(50, TimeSpan.Zero);
        Assert.Equal(color, renderer.RenderFrame(TimeSpan.Zero)[50]);
        Assert.Throws<CapabilityUnavailableException>(() => renderer.Pulse(96, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.Pulse(50, TimeSpan.FromTicks(-1)));
        Assert.All(renderer.RenderFrame(TimeSpan.FromSeconds(3)), value => Assert.Equal(default, value));
    }

    private static RgbColor[] DecodeGridRows(RazerCommandSpec[] rows)
    {
        Assert.Equal(6, rows.Length);
        var colors = new RgbColor[96];
        for (int row = 0; row < 6; row++)
        {
            ReadOnlySpan<byte> bytes = rows[row].Arguments.Span;
            Assert.Equal(row, bytes[1]);
            for (int col = 0; col < 16; col++)
            {
                int offset = 4 + col * 3;
                colors[row * 16 + col] = new(bytes[offset], bytes[offset + 1], bytes[offset + 2]);
            }
        }
        return colors;
    }
}
