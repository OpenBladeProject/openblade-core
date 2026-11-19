using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    [Theory]
    [InlineData(0x02C5, LightingEffect.Static)]
    [InlineData(0x029D, LightingEffect.Static)]
    [InlineData(0x029F, LightingEffect.Static)]
    [InlineData(0x02C5, LightingEffect.Fire)]
    [InlineData(0x029D, LightingEffect.Fire)]
    [InlineData(0x029F, LightingEffect.Fire)]
    [InlineData(0x02C5, LightingEffect.AudioMeter)]
    [InlineData(0x029D, LightingEffect.AudioMeter)]
    [InlineData(0x029F, LightingEffect.AudioMeter)]
    public async Task DeveloperFrames_ConsumeUniqueRunTransactions(int productId, LightingEffect effect)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var context = new ValidationRunContext();
        var session = new Session();
        var clock = new AnimationClock();
        await using var port = new ExperimentalBladePort(session, context, model, new(model),
            () => true, () => PowerSource.PluggedIn, recordQueries: false, lightingTimeProvider: clock);
        await port.SetLightingAsync(FrameBudgetSetting(effect), CancellationToken.None);
        if (effect == LightingEffect.AudioMeter)
        {
            await port.PushUniformLightingFrameAsync(new(new RgbColor(1, 2, 3)), CancellationToken.None);
        }
        Assert.Equal(9, session.Commands.Count);
        Assert.Equal(255 - session.Commands.Count, context.RemainingTransactions);
        Assert.Equal(Enumerable.Range(1, session.Commands.Count).Select(id => (byte)id), session.ExchangeTransactions);
    }

    [Theory]
    [InlineData(LightingEffect.Static)]
    [InlineData(LightingEffect.Fire)]
    [InlineData(LightingEffect.AudioMeter)]
    public async Task DeveloperFrame_ExhaustionAfterRowsPausesWithoutActivationOrReplay(LightingEffect effect)
    {
        ExperimentalBladeModel model = ModelFor(0x02C5);
        var context = new ValidationRunContext();
        for (int id = 1; id <= 250; id++) { Assert.True(context.TryAllocateTransaction(out _)); }
        var session = new Session();
        var clock = new AnimationClock();
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = new ExperimentalBladePort(session, context, model, policy,
            () => true, () => PowerSource.PluggedIn, recordQueries: false, lightingTimeProvider: clock);
        async Task ApplyAsync()
        {
            await port.SetLightingAsync(FrameBudgetSetting(effect), CancellationToken.None);
            if (effect == LightingEffect.AudioMeter)
            {
                await port.PushUniformLightingFrameAsync(new(new RgbColor(1, 2, 3)), CancellationToken.None);
            }
        }
        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(ApplyAsync);
        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(0, context.RemainingTransactions);
        Assert.Equal(3, session.Commands.Count(IsFrameRow));
        Assert.DoesNotContain(session.Commands, IsFrameActivation);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.False(policy.WritesAllowed);
        int stopped = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None));
        Assert.Equal(stopped, session.Commands.Count);
    }

    [Fact]
    public async Task Blade16Grid_UsesExistingRollingAllocatorAcrossProductionFrames()
    {
        ExperimentalBladeModel model = ModelFor(0x029F);
        var context = new ValidationRunContext();
        var session = new Session();
        var clock = new AnimationClock();
        await using var port = new ExperimentalBladePort(session, context, model, new(model),
            () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(50), CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        for (int frame = 1; frame <= 45; frame++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await clock.WaitForTimerCountAsync(frame + 1);
        }
        Assert.True(session.ExchangeTransactions.Count > 255);
        Assert.Equal(Enumerable.Range(0, session.ExchangeTransactions.Count).Select(i => (byte)(i % 255 + 1)),
            session.ExchangeTransactions);
        Assert.Equal(255, context.RemainingTransactions);
    }

    [Theory]
    [InlineData(0x029D)]
    [InlineData(0x029F)]
    public async Task UnsupportedKeyEffect_DoesNotPreemptAnActiveGridRenderer(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session();
        var clock = new AnimationClock();
        await using var port = new ExperimentalBladePort(session, new(), model, new(model),
            () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(50), CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetLightingAsync(
            LightingSettingsFactory.Reactive(new(1, 2, 3), 50, ReactiveLightingDuration.Short), CancellationToken.None));
        Assert.Equal(before, session.Commands.Count);
        Assert.Equal(LightingRendererOwnershipState.Fire, port.OwnershipState);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(2);
        Assert.Equal(12, session.Commands.Count(IsFrameRow));
    }

    private static LightingSettings FrameBudgetSetting(LightingEffect effect) => effect switch
    {
        LightingEffect.Static => LightingSettingsFactory.Static(new(1, 2, 3), 50),
        LightingEffect.Fire => LightingSettingsFactory.Fire(50),
        _ => LightingSettingsFactory.AudioMeter(1, 50),
    };

    private sealed partial class Session
    {
        internal List<byte> ExchangeTransactions { get; } = [];
    }
}
