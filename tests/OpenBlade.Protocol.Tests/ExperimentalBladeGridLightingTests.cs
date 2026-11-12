using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    [Theory]
    [InlineData(0x02C5)]
    [InlineData(0x029D)]
    [InlineData(0x029F)]
    public void GridCapabilities_ExposeOwnedEffectsAndKeepUnmappedKeysUnavailable(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, true);
        Assert.True(capabilities.SupportsUniformLightingFrames);
        Assert.True(capabilities.SupportsWaveDirectionSelection);
        Assert.Equal(Enum.GetValues<BreathingColorMode>().Length, capabilities.SupportedBreathingColorModes!.Count);
        foreach (LightingEffect effect in new[] { LightingEffect.Static, LightingEffect.AmbientAwareness,
            LightingEffect.AudioMeter, LightingEffect.Fire, LightingEffect.Wheel,
            LightingEffect.Starlight, LightingEffect.Tidal })
        {
            Assert.Contains(effect, capabilities.LightingEffects);
        }
        if (productId == 0x02C5)
        {
            Assert.Contains(LightingEffect.Reactive, capabilities.LightingEffects);
            Assert.Contains(LightingEffect.Ripple, capabilities.LightingEffects);
            Assert.Contains(LightingEffect.Sequence, capabilities.LightingEffects);
            Assert.Equal(KeyboardLightingLayoutIds.Rz09_0530, capabilities.KeyboardLightingLayoutId);
        }
        else
        {
            Assert.DoesNotContain(LightingEffect.Reactive, capabilities.LightingEffects);
            Assert.DoesNotContain(LightingEffect.Ripple, capabilities.LightingEffects);
            Assert.DoesNotContain(LightingEffect.Sequence, capabilities.LightingEffects);
            Assert.Null(capabilities.KeyboardLightingLayoutId);
        }
        Assert.Contains(LightingEffect.Reactive, capabilities.ObservedLightingEffects);
        Assert.Contains(LightingEffect.Ripple, capabilities.ObservedLightingEffects);
        Assert.DoesNotContain(LightingEffect.Fire, capabilities.ObservedLightingEffects);
        Assert.Empty(ExperimentalBladePort.CreateCapabilities(model, false).LightingEffects);
    }

    [Theory]
    [InlineData(0x02C5)]
    [InlineData(0x029D)]
    public async Task Blade14Static_UsesOwnPaddedRowsAndRestoresExactSavedBrightness(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 77, ExpectedFrameTransactionId = 0xFF };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);
        var color = new RgbColor(0x12, 0x34, 0x56);
        LightingSettings settings = LightingSettingsFactory.Static(color, 50);
        Assert.True(port.RequiresLightingRendererOwnership(settings));
        await port.SetLightingAsync(settings, CancellationToken.None);
        RazerCommandSpec[] rows = session.Commands.Where(IsFrameRow).ToArray();
        AssertPaddedBlade14Rows(rows, color);
        Assert.Single(session.Commands, IsFrameActivation);
        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
        Assert.Null(await port.ReadStateAsync(CancellationToken.None));
        await port.RelinquishRendererAsync(CancellationToken.None);
        Assert.Equal(77, session.Raw);
        Assert.Equal(3, session.Effect);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    [Theory]
    [InlineData(0x02C5, LightingEffect.AmbientAwareness)]
    [InlineData(0x029D, LightingEffect.AudioMeter)]
    public async Task Blade14SampledEffect_StagesBlackWhileDarkAndRestoresZero(int productId, LightingEffect effect)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 0, Effect = 0, ExpectedFrameTransactionId = 0xFF };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);
        LightingSettings settings = effect == LightingEffect.AudioMeter
            ? LightingSettingsFactory.AudioMeter(1, 50)
            : LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50);
        await port.SetLightingAsync(settings, CancellationToken.None);
        int brightness = session.Commands.FindIndex(IsBrightnessSetter);
        Assert.True(brightness > 0);
        AssertPaddedBlade14Rows(session.Commands.Take(brightness).Where(IsFrameRow).ToArray(), default);
        Assert.Contains(session.Commands.Take(brightness), IsFrameActivation);
        int beforeSample = session.Commands.Count;
        var color = new RgbColor(9, 8, 7);
        await port.PushUniformLightingFrameAsync(new UniformLightingFrame(color), CancellationToken.None);
        AssertPaddedBlade14Rows(session.Commands.Skip(beforeSample).Where(IsFrameRow).ToArray(), color);
        Assert.DoesNotContain(session.Commands.Skip(beforeSample), IsBrightnessSetter);
        await port.RelinquishRendererAsync(CancellationToken.None);
        Assert.Equal(0, session.Raw);
        Assert.Equal(0, session.Effect);
    }

    [Theory]
    [InlineData(0x02C5)]
    [InlineData(0x029D)]
    public async Task Blade14IncompleteWave_BlocksHostFramesBeforeAnySetter(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Effect = 4, EffectReplyLength = 3 };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        await port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None);
        Assert.Equal(3, session.Effect);
    }

    [Theory]
    [InlineData(0x02C5)]
    [InlineData(0x029D)]
    [InlineData(0x029F)]
    public async Task GridAnimation_ProductionStream_UsesAcknowledgedRowsAndStopsOnReplacement(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var clock = new AnimationClock();
        var session = new Session
        {
            Raw = 0,
            Effect = 0,
            ExpectedFrameTransactionId = productId == 0x029F ? null : (byte)0xFF,
        };
        await using var port = new ExperimentalBladePort(session, new(), model, new(model),
            () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(50), CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        int brightness = session.Commands.FindIndex(IsBrightnessSetter);
        Assert.Equal(6, session.Commands.Take(brightness).Count(IsFrameRow));
        Assert.Contains(session.Commands.Take(brightness), IsFrameActivation);
        for (int i = 1; i <= 45; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await clock.WaitForTimerCountAsync(i + 1);
        }
        Assert.Equal(46 * 6, session.Commands.Count(IsFrameRow));
        Assert.Single(session.Commands, IsFrameActivation);
        Assert.Equal(0, session.AnimationBatches);
        clock.Advance(TimeSpan.FromSeconds(10));
        await clock.WaitForTimerCountAsync(47);
        Assert.Equal(47 * 6, session.Commands.Count(IsFrameRow));
        await port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None);
        int stopped = session.Commands.Count;
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(stopped, session.Commands.Count);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GridAnimation_PartialFrameFailureOrGuardLossPausesWithoutReplay(bool loseAdmission)
    {
        ExperimentalBladeModel model = ModelFor(0x02C5);
        var clock = new AnimationClock();
        var session = new Session { Raw = 0, Effect = 0 };
        var policy = new ExperimentalFeaturesPolicy(model);
        bool present = true;
        session.BeforeSubmission = command =>
        {
            if (IsFrameRow(command) && command.Arguments.Span[1] == 1)
            {
                if (loseAdmission) { present = false; }
                else { session.FailFrameRow = true; }
            }
        };
        await using var port = new ExperimentalBladePort(session, new(), model, policy,
            () => present, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Fire(50), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.Equal(0, session.Raw);
        Assert.DoesNotContain(session.Commands, IsBrightnessSetter);
        Assert.DoesNotContain(session.Commands, IsFrameActivation);
        int stopped = session.Commands.Count;
        present = true;
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(stopped, session.Commands.Count);
    }

    [Theory]
    [InlineData(0x029D)]
    [InlineData(0x029F)]
    public async Task ObservedKeyEffects_RemainUnavailableBeforeAnyDeviceTraffic(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session();
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetLightingAsync(
            LightingSettingsFactory.Reactive(new RgbColor(1, 2, 3), 50, ReactiveLightingDuration.Short),
            CancellationToken.None));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetLightingAsync(
            LightingSettingsFactory.Ripple(new RgbColor(1, 2, 3), 50), CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    private static void AssertPaddedBlade14Rows(RazerCommandSpec[] rows, RgbColor color)
    {
        Assert.Equal(6, rows.Length);
        for (int row = 0; row < rows.Length; row++)
        {
            byte[] payload = rows[row].Arguments.ToArray();
            Assert.Equal(70, rows[row].ExpectedPayloadLength);
            Assert.Equal(70, payload.Length);
            Assert.Equal(new byte[] { 0xFF, (byte)row, 0, 15 }, payload[..4]);
            for (int offset = 4; offset < 52; offset += 3)
            {
                Assert.Equal(new byte[] { color.Red, color.Green, color.Blue }, payload[offset..(offset + 3)]);
            }
            Assert.All(payload[52..], value => Assert.Equal(0, value));
        }
    }

    private sealed partial class Session
    {
        internal byte? ExpectedFrameTransactionId { get; init; }
    }
}
