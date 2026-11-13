using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    [Fact]
    public void Rz090301_IndependentLightingCoexistsWithReviewedEffectsWithoutFrames()
    {
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(Model(0x0245), true);
        Assert.True(capabilities.SupportsIndependentLightingControls);
        Assert.True(capabilities.SupportsKeyboardBrightness);
        Assert.Equal(new HashSet<LightingEffect> { LightingEffect.Off, LightingEffect.Static, LightingEffect.Spectrum, LightingEffect.Breathing, LightingEffect.Wave }, capabilities.LightingEffects.ToHashSet());
        Assert.Contains(LightingEffect.Static, capabilities.ObservedLightingEffects);
        Assert.False(capabilities.SupportsUniformLightingFrames);
        Assert.Equal(3, capabilities.LogoLightingModes.Count);
        Assert.Null(capabilities.KeyboardLightingLayoutId);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    [InlineData(25, 64)]
    [InlineData(50, 128)]
    [InlineData(75, 192)]
    [InlineData(100, 255)]
    public async Task Rz090301_AbsoluteBrightnessChangesNoEffectOrLogo(int percent, int raw)
    {
        var session = new Session { Brightness = 127, McuLighting = [0, 5, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        await using var port = Create(0x0245, session);
        await port.SetIndependentLightingAsync(new((byte)percent, null), default);
        Assert.Equal(raw, session.Brightness);
        Assert.Single(session.Commands, command => command.ClassId == 3 && command.CommandId == 3);
        Assert.All(session.Commands, command => Assert.True(command.ClassId == 3 && command.CommandId is 3 or 0x83));
        RazerCommandSpec setter = session.Commands.Single(command => command.CommandId == 3);
        Assert.Equal(new byte[] { 0, 5, (byte)raw }, setter.Arguments.ToArray());
        Assert.True(setter.PayloadValidator?.Invoke(ReadOnlyMemory<byte>.Empty));
        Assert.False(setter.PayloadValidator?.Invoke(new byte[] { 99 }));
        Assert.Equal(new IndependentLightingSettings((byte)percent, null),
            await port.ReadIndependentLightingAsync(new(0, null), default));
        Assert.Null(await port.ReadStateAsync(default));
    }

    [Theory]
    [InlineData(LogoLightingMode.Static, 0)]
    [InlineData(LogoLightingMode.Breathing, 2)]
    [InlineData(LogoLightingMode.Off, 0)]
    public async Task Rz090301_LogoUsesProfileZeroAndConfirmsEachWrite(LogoLightingMode requested, int effect)
    {
        var session = new Session { LogoOn = requested == LogoLightingMode.Off ? (byte)1 : (byte)0 };
        await using var port = Create(0x0245, session);
        await port.SetIndependentLightingAsync(new(null, requested), default);
        Assert.Equal(requested, await port.ReadLogoStateAsync(default));
        Assert.All(session.Commands, command =>
        {
            Assert.Equal(3, command.ClassId);
            Assert.Equal(0, command.Arguments.Span[0]);
            Assert.Equal(4, command.Arguments.Span[1]);
            Assert.Contains(command.CommandId, new byte[] { 0, 2, 0x80, 0x82 });
        });
        if (requested != LogoLightingMode.Off)
        {
            int effectSet = session.Commands.FindIndex(command => command.CommandId == 2);
            int stateSet = session.Commands.FindIndex(command => command.CommandId == 0);
            Assert.True(effectSet >= 0 && stateSet > effectSet);
            Assert.Contains(session.Commands.Skip(effectSet + 1).Take(stateSet - effectSet - 1), command => command.CommandId == 0x82);
            Assert.Equal(effect, session.LogoEffect);
        }
        Assert.Equal(128, session.Brightness);
    }

    [Fact]
    public async Task Rz090301_UnknownBrightnessBaselineDoesNotWriteAndNativeStepsRemainUnavailable()
    {
        var session = new Session { RejectLightingQuery = true };
        await using var port = Create(0x0245, session);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetIndependentLightingAsync(new(40, null), default));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.StepBrightnessAsync(KeyboardBrightnessDirection.Increase, default));
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Fact]
    public async Task Rz090301_UnconfirmedBrightnessPausesAndAllRecoveryReadsOnlyIndependentFields()
    {
        var session = new Session { IgnoreWrites = true, McuLighting = [0, 5, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetIndependentLightingAsync(new(75, null), default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetIndependentLightingAsync(new(75, null), default));
        Assert.Equal(before, session.Commands.Count);
        ConfirmedDeviceState recovered = await port.ReadRecoveryStateAsync(default);
        Assert.Null(recovered.Lighting);
        Assert.Equal(new IndependentLightingSettings(50, LogoLightingMode.Static), recovered.IndependentLighting);
        Assert.All(session.Commands.Skip(before), command => Assert.True(command.CommandId >= 0x80));
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 0x0F || command.CommandId == 0x8A);
        Assert.False(policy.WritesAllowed);
    }
    [Fact]
    public async Task Rz090301_ObservationRetainsAvailableBrightnessAndScopedReadDoesNotQueryLogo()
    {
        var session = new Session { RejectLogoQuery = true };
        await using var port = Create(0x0245, session);
        Assert.Equal(new IndependentLightingSettings(50, null), await port.ReadIndependentLightingAsync(default));
        session.Commands.Clear();
        Assert.Equal(new IndependentLightingSettings(50, null),
            await port.ReadIndependentLightingAsync(new(0, null), default));
        await port.SetIndependentLightingAsync(new(70, null), default);
        Assert.All(session.Commands, command => Assert.True(command.ClassId == 3 && command.CommandId is 3 or 0x83));
    }

    [Fact]
    public async Task Rz090301_CombinedRequestStopsAfterConfirmedBrightnessWhenLogoBaselineFails()
    {
        var session = new Session { RejectLogoQuery = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetIndependentLightingAsync(new(75, LogoLightingMode.Breathing), default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(192, session.Brightness);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.CommandId < 0x80);
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0 or 2);
    }

    [Fact]
    public async Task Rz090301_IndependentIdentityLossBeforeSecondFieldStopsWithoutReplay()
    {
        var session = new Session();
        bool identity = true;
        session.BeforeSubmission = command =>
        {
            if (command.ClassId == 3 && command.CommandId == 2)
            {
                identity = false;
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy, identity: () => identity);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetIndependentLightingAsync(new(75, LogoLightingMode.Breathing), default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.CommandId < 0x80);
        Assert.Equal(192, session.Brightness);
        Assert.Equal(0, session.LogoEffect);
    }

    [Fact]
    public async Task Rz090301_IndependentNoOpDoesNotRecordFictitiousSubmission()
    {
        var session = new Session();
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy);
        await port.SetIndependentLightingAsync(new(50, LogoLightingMode.Static), default);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        Assert.All(policy.Snapshot().Observations, observation => Assert.False(observation.Submitted));
    }

    [Fact]
    public async Task Rz090301_RetiredObservationPropagatesBeforeUnrelatedLogoRead()
    {
        var session = new Session { RejectLightingQuery = true, RetireOnQueryFailure = true };
        await using var port = Create(0x0245, session);
        await Assert.ThrowsAnyAsync<IOException>(() => port.ReadIndependentLightingAsync(default));
        Assert.Single(session.Commands);
        Assert.Equal(0x83, session.Commands[0].CommandId);
    }

    [Fact]
    public async Task Rz090301_ThermalOnlyRecoveryAddsNoIndependentLightingQueries()
    {
        var session = new Session { RejectLightingQuery = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        policy.RequireRecovery(ExperimentalRecoveryScope.ThermalOnly);
        await using var port = Create(0x0245, session, policy);
        ConfirmedDeviceState recovered = await port.ReadRecoveryStateAsync(default);
        Assert.Null(recovered.IndependentLighting);
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 3);
        Assert.False(policy.WritesAllowed);
    }
}
