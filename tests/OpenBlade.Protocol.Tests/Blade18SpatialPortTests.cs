using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    [Theory]
    [InlineData(LightingEffect.Reactive)]
    [InlineData(LightingEffect.Ripple)]
    public async Task Blade18KeyTriggeredPulse_OnlyChangesMemoryUntilTheNextFrame(LightingEffect effect)
    {
        var clock = new AnimationClock();
        var session = new Session();
        var policy = Enabled();
        await using var port = AnimatedPort(session, clock, policy);
        LightingSettings setting = SpatialSetting(effect);
        await port.SetLightingAsync(setting, CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        Assert.True(Blade18KeyboardLightingLayout.TryMapScanCode(0x10, false, out byte slot));
        int commandsBeforePulse = session.Commands.Count;
        await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(slot), CancellationToken.None);
        Assert.Equal(commandsBeforePulse, session.Commands.Count);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(2);
        Assert.Equal(2, session.AnimationBatches);
        Assert.Contains(session.Commands.Where(command => command.ClassId == 3
            && command.CommandId == 0x0B).TakeLast(6),
            row => row.Arguments.Span[4..].ContainsAnyExcept((byte)0));
        Assert.True(policy.WritesAllowed);
        await port.RelinquishRendererAsync(CancellationToken.None);
        Assert.Equal(3, session.Effect);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.PulseReactiveKeyAsync(new ReactiveKeyPulse(slot), CancellationToken.None));
    }

    [Theory]
    [InlineData(LightingEffect.Reactive)]
    [InlineData(LightingEffect.Ripple)]
    [InlineData(LightingEffect.Tidal)]
    public async Task Blade18SpatialRenderer_RetiresAndRestoresSavedBaseline(LightingEffect effect)
    {
        var clock = new AnimationClock();
        var session = new Session();
        await using var port = AnimatedPort(session, clock);
        await port.SetLightingAsync(SpatialSetting(effect), CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        Assert.Equal(effect.ToString(), port.OwnershipState.ToString());
        await port.RelinquishRendererAsync(CancellationToken.None);
        int batches = session.AnimationBatches;
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(batches, session.AnimationBatches);
        Assert.Equal(3, session.Effect);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    [Fact]
    public async Task Blade18SpatialPulse_RejectsHolesAndOtherModelSlotRangeWithoutTraffic()
    {
        var clock = new AnimationClock();
        var session = new Session();
        await using var port = AnimatedPort(session, clock);
        await port.SetLightingAsync(SpatialSetting(LightingEffect.Reactive), CancellationToken.None);
        byte hole = (byte)Enumerable.Range(0, 96).First(slot =>
            !Blade18KeyboardLightingLayout.IsKeySlot(slot));
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.PulseReactiveKeyAsync(new ReactiveKeyPulse(hole), CancellationToken.None));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.PulseReactiveKeyAsync(new ReactiveKeyPulse(100), CancellationToken.None));
        Assert.Equal(before, session.Commands.Count);
    }

    [Fact]
    public async Task Blade18SpatialFrameFailure_RejectsFurtherPulsesAndDoesNotReplay()
    {
        var clock = new AnimationClock();
        var session = new Session { FailAnimationBatch = 2 };
        var policy = Enabled();
        await using var port = AnimatedPort(session, clock, policy);
        await port.SetLightingAsync(SpatialSetting(LightingEffect.Reactive), CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await WaitUntilAsync(() => port.OwnershipState == LightingRendererOwnershipState.Faulted);
        Assert.False(policy.WritesAllowed);
        int before = session.Commands.Count;
        Assert.True(Blade18KeyboardLightingLayout.TryMapScanCode(0x10, false, out byte slot));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.PulseReactiveKeyAsync(new ReactiveKeyPulse(slot), CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(before, session.Commands.Count);
        Assert.Equal(2, session.AnimationBatches);
    }

    private static LightingSettings SpatialSetting(LightingEffect effect) => effect switch
    {
        LightingEffect.Reactive => LightingSettingsFactory.Reactive(
            new RgbColor(255, 0, 0), 50, ReactiveLightingDuration.Medium),
        LightingEffect.Ripple => LightingSettingsFactory.Ripple(new RgbColor(255, 0, 0), 50),
        LightingEffect.Tidal => LightingSettingsFactory.Tidal(
            new RgbColor(255, 0, 0), new RgbColor(0, 0, 255),
            false, LightingDirection.Leftward, 50),
        _ => throw new ArgumentOutOfRangeException(nameof(effect)),
    };
}
