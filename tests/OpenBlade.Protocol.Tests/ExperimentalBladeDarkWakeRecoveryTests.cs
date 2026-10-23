using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    [Theory]
    [InlineData(0x029D)]
    [InlineData(0x02C5)]
    [InlineData(0x0255)]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task DarkWakeFailure_ReadOnlyRecoveryKeepsLightingUnknownUntilNewRequest(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 0, Effect = 0, IgnoreBrightness = true };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);

        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None));
        Assert.Equal(3, session.Effect);
        Assert.Equal(0, session.Raw);
        Assert.False(policy.WritesAllowed);

        int beforeRecovery = session.Commands.Count;
        (LightingSettings? lighting, _) = await port.ReadRecoveryLightingStateAsync(
            CancellationToken.None);

        Assert.Null(lighting);
        Assert.All(session.Commands.Skip(beforeRecovery),
            command => Assert.True(command.CommandId >= 0x80));
        Assert.Equal(0, session.Raw);
        Assert.False(policy.WritesAllowed);
        if (productId is not (0x029F or 0x02A0))
        {
            Assert.Contains(policy.Snapshot().Observations, item =>
                item.FailureCategory == "ConfirmedDarkEffect"
                && item.ConfirmedValue == "Effect=Spectrum;Raw=0;Percent=0");
        }

        // The factory completes this latch only after its fresh-session,
        // authority and service reconciliation gates. No port read clears it.
        policy.CompleteRecovery();
        session.IgnoreBrightness = false;
        session.Commands.Clear();

        await Assert.ThrowsAsync<DeviceStateContentRejectedException>(() =>
            port.ReadStateAsync(CancellationToken.None));

        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None);

        // Even the same reported effect must be explicitly reasserted after
        // an unavailable baseline; a recovery read never replays this setter.
        Assert.Equal(new byte[] { 0x02, 0x03 }, MutationCommandIds(session));
        Assert.Equal(128, session.Raw);
        Assert.Equal(LightingSettingsFactory.SpectrumCycling(50),
            await port.ReadStateAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(0x029D)]
    [InlineData(0x02C5)]
    [InlineData(0x0255)]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task ConfirmedDarkBaseline_ExplicitOffConfirmsDestinationWithoutRaisingBrightness(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 0, Effect = 3 };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        await port.SetLightingAsync(LightingSettingsFactory.Off(), CancellationToken.None);

        Assert.Equal(new byte[] { 0x02 }, MutationCommandIds(session));
        Assert.Equal(0, session.Raw);
        Assert.Equal(0, session.Effect);
        Assert.Equal(LightingSettingsFactory.Off(),
            await port.ReadStateAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blade15ConfirmedDarkBaseline_ExplicitColorReassertsAndConfirmsParameters(bool breathing)
    {
        ExperimentalBladeModel model = ModelFor(0x0255);
        var session = new Session { Raw = 0, Effect = breathing ? (byte)2 : (byte)1 };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        await Assert.ThrowsAsync<DeviceStateContentRejectedException>(() =>
            port.ReadStateAsync(CancellationToken.None));
        session.Commands.Clear();
        await port.SetLightingAsync(Blade15ParameterizedSetting(breathing), CancellationToken.None);

        int setter = session.Commands.FindIndex(IsSingleZoneEffectSetter);
        int effectRead = session.Commands.FindIndex(setter + 1, IsEffectQuery);
        int colorRead = session.Commands.FindIndex(effectRead + 1, IsStandardEffectQuery);
        int brightness = session.Commands.FindIndex(IsBrightnessSetter);
        Assert.True(setter < effectRead && effectRead < colorRead && colorRead < brightness);
        Assert.Equal(128, session.Raw);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blade15RandomBreathing_ConfirmsColorModeBeforeBrightness(bool wrongMode)
    {
        ExperimentalBladeModel model = ModelFor(0x0255);
        var session = new Session { Raw = 0, Effect = 0 };
        if (wrongMode)
        {
            session.BeforeSubmission = command =>
            {
                if (IsStandardEffectQuery(command))
                {
                    session.StandardEffect = [3, 1, 9, 8, 7];
                }
            };
        }
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);
        LightingSettings requested = LightingSettingsFactory.BreathingRandom(50);

        if (wrongMode)
        {
            await Assert.ThrowsAsync<BladeWriteException>(() =>
                port.SetLightingAsync(requested, CancellationToken.None));
            Assert.Equal(0, session.Raw);
            Assert.DoesNotContain(session.Commands, IsBrightnessSetter);
            Assert.False(policy.WritesAllowed);
        }
        else
        {
            await port.SetLightingAsync(requested, CancellationToken.None);
            int effectSet = session.Commands.FindIndex(command =>
                command.ClassId == 0x0F && command.CommandId == 0x02);
            int effectRead = session.Commands.FindIndex(effectSet + 1, IsEffectQuery);
            int parameterRead = session.Commands.FindIndex(effectRead + 1, IsStandardEffectQuery);
            int brightness = session.Commands.FindIndex(IsBrightnessSetter);
            Assert.True(effectSet < effectRead && effectRead < parameterRead && parameterRead < brightness);
            Assert.Equal(requested, await port.ReadStateAsync(CancellationToken.None));
        }
    }
    [Theory]
    [InlineData(0x029D)]
    [InlineData(0x02C5)]
    [InlineData(0x0255)]
    public async Task Recovery_DoesNotAcceptRoundedZeroWithNonzeroRawBrightness(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 1, Effect = 3 };
        var policy = new ExperimentalFeaturesPolicy(model);
        policy.RequireRecovery();
        await using var port = Create(session, policy, model);

        await Assert.ThrowsAsync<DeviceStateContentRejectedException>(() =>
            port.ReadRecoveryLightingStateAsync(CancellationToken.None));

        Assert.False(policy.WritesAllowed);
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
    }

    [Theory]
    [InlineData(0x029D)]
    [InlineData(0x02C5)]
    [InlineData(0x0255)]
    public async Task Recovery_DoesNotAcceptUnknownEffectAtConfirmedZero(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 0, Effect = 0x7F };
        var policy = new ExperimentalFeaturesPolicy(model);
        policy.RequireRecovery();
        await using var port = Create(session, policy, model);

        await Assert.ThrowsAsync<DeviceStateContentRejectedException>(() =>
            port.ReadRecoveryLightingStateAsync(CancellationToken.None));

        Assert.False(policy.WritesAllowed);
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
    }

    [Theory]
    [InlineData(0x029F, 1, 3)]
    [InlineData(0x02A0, 1, 3)]
    [InlineData(0x029F, 0, 0x7F)]
    [InlineData(0x02A0, 0, 0x7F)]
    public async Task HostModelBuiltInRequest_RejectsMalformedDarkBaselineBeforeWriting(
        int productId, byte raw, byte effect)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = raw, Effect = effect };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None));

        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
        Assert.Equal(raw, session.Raw);
    }

    [Theory]
    [InlineData(0x029F, LightingEffect.Static)]
    [InlineData(0x02A0, LightingEffect.Static)]
    [InlineData(0x029F, LightingEffect.AmbientAwareness)]
    [InlineData(0x02A0, LightingEffect.AmbientAwareness)]
    [InlineData(0x02A0, LightingEffect.Breathing)]
    public async Task UnrepresentableDarkHostBaseline_RemainsBlockedBeforeAnyFrame(
        int productId, LightingEffect effect)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 0, Effect = 3 };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);
        LightingSettings requested = effect switch
        {
            LightingEffect.AmbientAwareness => LightingSettingsFactory.AmbientAwareness(
                AmbientAwarenessRegion.Full, 50),
            LightingEffect.Breathing => LightingSettingsFactory.BreathingSingle(new RgbColor(9, 8, 7), 50),
            _ => LightingSettingsFactory.Static(new RgbColor(9, 8, 7), 50),
        };

        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            requested, CancellationToken.None));

        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
        Assert.Equal(0, session.Raw);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }
}
