using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    [Theory]
    [InlineData(0x02C5)]
    [InlineData(0x029D)]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task AlternateWave_CanSelectBuiltInWithoutGuessingRestorationDirection(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Effect = 4, Direction = 2, EffectReplyLength = 4 };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);

        Assert.Null(await port.ReadStateAsync(CancellationToken.None));
        Assert.Contains(policy.Snapshot().Observations, observation =>
            observation.FailureCategory == "WaveDirectionNotRestorable");
        await port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None);

        Assert.Equal(LightingEffect.Spectrum, (await port.ReadStateAsync(CancellationToken.None))!.Effect);
        Assert.Single(session.Commands, command => command.CommandId < 0x80);
        Assert.Empty(session.RetainedQueryFailures);
    }

    [Fact]
    public async Task AlternateWave_CannotConfirmAnIgnoredRightwardDestination()
    {
        ExperimentalBladeModel model = ModelFor(0x02C5);
        var session = new Session
        {
            Effect = 4,
            Direction = 2,
            EffectReplyLength = 4,
            IgnoreEffect = true,
        };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Commands, command => command.CommandId < 0x80);
        Assert.False(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task AlternateWave_CannotBecomeAHostFrameRestorationBaseline(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 45, Effect = 4, Direction = 2, EffectReplyLength = 4 };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Static(new RgbColor(9, 8, 7), 50), CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
    }

    [Theory]
    [InlineData(0x02C5, 4, true)]
    [InlineData(0x029D, 4, true)]
    [InlineData(0x029F, 3, false)]
    [InlineData(0x02A0, 3, false)]
    public async Task WaveGetter_RequestsDirectionOnlyWithExactMcuEvidence(
        int productId, int length, bool directionReadable)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 45, Effect = 4, EffectReplyMatchesRequestLength = true };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        LightingSettings? state = await port.ReadStateAsync(CancellationToken.None);

        RazerCommandSpec query = Assert.Single(session.Commands,
            command => command.ClassId == 0x0F && command.CommandId == 0x82);
        Assert.Equal(length, query.Arguments.Length);
        Assert.Equal(length, query.ExpectedPayloadLength);
        Assert.True(FirmwareQueryDiagnostics.CanRecord(query));
        if (directionReadable)
        {
            Assert.Equal(LightingSettingsFactory.Wave(LightingDirection.Rightward, 18), state);
        }
        else
        {
            Assert.Null(state);
        }
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
    }

    [Theory]
    [InlineData(0x02C5)]
    [InlineData(0x029D)]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task CompactWave_ReadKeepsDirectionUnknownAndRecoveryReadOnly(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 45, Effect = 4, EffectReplyLength = 3 };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);

        Assert.Null(await port.ReadStateAsync(CancellationToken.None));
        var recovery = await port.ReadRecoveryLightingStateAsync(CancellationToken.None);

        Assert.Null(recovery.Lighting);
        Assert.Equal(LogoLightingMode.Off, recovery.Logo);
        Assert.Empty(session.RetainedQueryFailures);
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
        Assert.Contains(policy.Snapshot().Observations, observation =>
            observation.FailureCategory == "WaveDirectionReadbackUnavailable"
            && observation.ConfirmedValue == "Effect=Wave;Raw=45;Direction=Unavailable");
    }

    [Theory]
    [InlineData(LightingEffect.Off, LogoLightingMode.Off)]
    [InlineData(LightingEffect.Spectrum, LogoLightingMode.Static)]
    [InlineData(LightingEffect.Breathing, LogoLightingMode.Breathing)]
    [InlineData(LightingEffect.Wave, LogoLightingMode.Static)]
    public async Task Blade14CompactWave_AppliesDirectBuiltInDestinationAndLogo(
        LightingEffect effect, LogoLightingMode logo)
    {
        ExperimentalBladeModel model = ModelFor(0x02C5);
        var session = new Session { Raw = 45, Effect = 4, EffectReplyLength = 3 };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);
        LightingSettings target = effect switch
        {
            LightingEffect.Off => LightingSettingsFactory.Off(),
            LightingEffect.Spectrum => LightingSettingsFactory.SpectrumCycling(50),
            LightingEffect.Breathing => LightingSettingsFactory.BreathingRandom(50),
            LightingEffect.Wave => LightingSettingsFactory.Wave(LightingDirection.Rightward, 50),
            _ => throw new InvalidOperationException(),
        };
        target = target with { LogoMode = logo };

        await port.SetLightingAsync(target, CancellationToken.None);

        RazerCommandSpec setter = Assert.Single(session.Commands,
            command => command.ClassId == 0x0F && command.CommandId == 0x02);
        byte expectedEffect = effect switch
        {
            LightingEffect.Off => 0,
            LightingEffect.Breathing => 2,
            LightingEffect.Spectrum => 3,
            LightingEffect.Wave => 4,
            _ => throw new InvalidOperationException(),
        };
        Assert.Equal(new byte[] { 1, 5, expectedEffect }, setter.Arguments.ToArray()[..3]);
        Assert.Equal(logo, await port.ReadLogoStateAsync(CancellationToken.None));
        LightingSettings? confirmed = await port.ReadStateAsync(CancellationToken.None);
        if (effect == LightingEffect.Wave)
        {
            Assert.Null(confirmed);
            Assert.Equal(new byte[] { 1, 5, 4, 1 }, setter.Arguments.ToArray());
            Assert.Contains(policy.Snapshot().Observations, observation =>
                observation.Stage == "Readback" && observation.Outcome == "Unavailable"
                && observation.ConfirmedValue == "Wave;Raw=128;Direction=Unavailable");
        }
        else
        {
            Assert.Equal(target with { LogoMode = null }, confirmed);
        }
        Assert.Equal(effect == LightingEffect.Off ? 0 : 128, session.Raw);
        Assert.Empty(session.RetainedQueryFailures);
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(0x029D)]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task CompactWave_SharedBackendCanSelectSpectrum(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Effect = 4, EffectReplyLength = 3 };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        await port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None);

        Assert.Equal(LightingEffect.Spectrum, (await port.ReadStateAsync(CancellationToken.None))!.Effect);
        Assert.Single(session.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [InlineData(0x029F, LightingEffect.Static)]
    [InlineData(0x02A0, LightingEffect.Static)]
    [InlineData(0x02A0, LightingEffect.Fire)]
    [InlineData(0x02A0, LightingEffect.AmbientAwareness)]
    public async Task CompactWave_HostFrameRequiresRestorableDirectionBeforeAnyWrite(
        int productId, LightingEffect effect)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session { Raw = 45, Effect = 4, EffectReplyLength = 3 };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);
        LightingSettings target = effect switch
        {
            LightingEffect.Static => LightingSettingsFactory.Static(new RgbColor(9, 8, 7), 50),
            LightingEffect.Fire => LightingSettingsFactory.Fire(50),
            LightingEffect.AmbientAwareness => LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50),
            _ => throw new InvalidOperationException(),
        };

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetLightingAsync(target, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.IsType<CapabilityUnavailableException>(failure.InnerException);
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactWave_UnconfirmedDestinationPausesWithoutReplay(bool failEffect)
    {
        ExperimentalBladeModel model = ModelFor(0x02C5);
        var session = new Session
        {
            Effect = 4,
            EffectReplyLength = 3,
            IgnoreEffect = !failEffect,
            FailEffect = failEffect,
        };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Commands, command => command.CommandId < 0x80);
        Assert.False(policy.WritesAllowed);
        int count = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Off(), CancellationToken.None));
        Assert.Equal(count, session.Commands.Count);
    }
}
