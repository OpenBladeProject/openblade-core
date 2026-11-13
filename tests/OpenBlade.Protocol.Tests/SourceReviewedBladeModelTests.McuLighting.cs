using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    public static TheoryData<int> McuLightingProducts => new()
    {
        0x028A, 0x028B, 0x028C, 0x02B7, 0x02B8, 0x02C7,
    };

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public void McuLighting_AdmitsOnlyIndependentlyReadableBuiltInDestinations(int product)
    {
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(Model(product), true);
        var expected = new HashSet<LightingEffect> { LightingEffect.Off, LightingEffect.Breathing, LightingEffect.Wave };
        if (product is 0x028A or 0x028B or 0x028C) { expected.Add(LightingEffect.Spectrum); }
        Assert.Equal(expected.Order(), capabilities.LightingEffects.Order());
        Assert.True(capabilities.SupportsKeyboardBrightness);
        Assert.False(capabilities.SupportsIndependentLightingControls);
        Assert.Equal(product == 0x028A ? 0 : 3, capabilities.LogoLightingModes.Count);
        Assert.False(capabilities.SupportsUniformLightingFrames);
        Assert.False(capabilities.SupportsWaveDirectionSelection);
        Assert.Equal(new[] { BreathingColorMode.Random }, capabilities.SupportedBreathingColorModes);
        Assert.Contains(LightingEffect.Static, capabilities.ObservedLightingEffects);
        Assert.Null(capabilities.KeyboardLightingLayoutId);
    }

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public async Task McuLighting_ReadsAllActualColorFieldsAndSwitchesDirectlyToRandomBreathing(int product)
    {
        byte[][] baselines =
        [
            [0, 5, 1, 0, 0, 1, 20, 40, 60, 0, 0, 0],
            [0, 5, 2, 0, 0, 1, 20, 40, 60, 0, 0, 0],
            [0, 5, 2, 0, 0, 2, 20, 40, 60, 80, 100, 120],
        ];
        foreach (byte[] baseline in baselines)
        {
            var session = new Session { McuLighting = baseline };
            await using var port = Create(product, session);
            LightingSettings? observed = await port.ReadStateAsync(default);
            Assert.NotNull(observed);
            Assert.Equal(new RgbColor(20, 40, 60), observed.Color);
            Assert.Equal(baseline[2] == 1 ? LightingEffect.Static : LightingEffect.Breathing, observed.Effect);
            session.Commands.Clear();
            await port.SetLightingAsync(LightingSettingsFactory.BreathingRandom(50), default);
            Assert.Equal(BreathingColorMode.Random,
                Assert.IsType<BreathingLightingParameters>((await port.ReadStateAsync(default))!.Parameters).ColorMode);
            RazerCommandSpec setter = Assert.Single(session.Commands, command => command.CommandId < 0x80);
            Assert.Equal(product is 0x028A or 0x028B or 0x028C ? new byte[] { 3, 3 } : new byte[] { 0, 5, 2, 0, 0, 0 }, setter.Arguments.ToArray());
            Assert.DoesNotContain(session.Commands, command => command.ClassId == 0x03 && command.CommandId is 0x0B or 0x8A);
            Assert.All(session.Commands.Where(command => command.ClassId == 0x0F && command.CommandId == 0x82),
                command => Assert.Equal(new byte[] { 0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, command.Arguments.ToArray()));
        }
    }

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public async Task McuLighting_AlternativeWaveAndDarkStateAllowDirectDestinations(int product)
    {
        var session = new Session { EchoLightingAck = true, Brightness = 0, SavedBrightness = 200, McuLighting = [0, 5, 4, 2, 40, 0, 0, 0, 0, 0, 0, 0] };
        await using var port = Create(product, session);
        await port.SetLightingAsync(LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), default);
        var setters = session.Commands.Where(command => command.CommandId < 0x80).ToArray();
        Assert.Equal(2, setters.Length);
        Assert.Equal(product is 0x028A or 0x028B or 0x028C ? (3, 0x0A) : (0x0F, 2), ((int)setters[0].ClassId, (int)setters[0].CommandId));
        Assert.Equal(product is 0x028A or 0x028B or 0x028C ? new byte[] { 1, 1 } : new byte[] { 0, 5, 4, 1 }, setters[0].Arguments.ToArray());
        Assert.Equal((3, 3), ((int)setters[1].ClassId, (int)setters[1].CommandId));
        Assert.Equal(LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), await port.ReadStateAsync(default));
    }

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public async Task McuLighting_CorruptParametersStopBeforeAnyWrite(int product)
    {
        var session = new Session { McuLighting = [0, 5, 2, 0, 0, 0, 99, 0, 0, 0, 0, 0] };
        await using var port = Create(product, session);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), default));
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public async Task McuLighting_SuccessAckWithoutChangedStatePausesWithoutAlternateSequence(int product)
    {
        var session = new Session { IgnoreWrites = true, McuLighting = [0, 5, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.CommandId < 0x80);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), default));
        Assert.Equal(before, session.Commands.Count);
        await port.ReadRecoveryStateAsync(default);
        Assert.All(session.Commands.Skip(before), command => Assert.True(command.CommandId >= 0x80));
        Assert.False(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(0x028A)]
    [InlineData(0x028B)]
    [InlineData(0x028C)]
    public async Task McuLighting_LegacyEffectRestoresRequestedActiveBrightnessAfterSavedBankCopy(int product)
    {
        var session = new Session { Brightness = 128, SavedBrightness = 200, EchoLightingAck = true };
        await using var port = Create(product, session);
        await port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50), default);
        var setters = session.Commands.Where(command => command.CommandId < 0x80).ToArray();
        Assert.Equal(2, setters.Length);
        Assert.Equal(new byte[] { 4 }, setters[0].Arguments.ToArray());
        Assert.Equal((3, 0x0A), ((int)setters[0].ClassId, (int)setters[0].CommandId));
        Assert.Equal(new byte[] { 0, 5, 128 }, setters[1].Arguments.ToArray());
        Assert.Equal(128, session.Brightness);
        Assert.Equal(LightingSettingsFactory.SpectrumCycling(50), await port.ReadStateAsync(default));
    }

    [Theory]
    [InlineData(0x02B7)]
    [InlineData(0x02B8)]
    [InlineData(0x02C7)]
    public async Task McuLighting_AlreadyOffClearsHiddenOverlayThroughConfirmedDarkBridge(int product)
    {
        var session = new Session
        {
            EchoLightingAck = true,
            McuLighting = [0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            McuCustomOverlay = true,
        };
        session.BeforeSubmission = command =>
        {
            if (command.ClassId == 0x0F && command.CommandId == 2)
            {
                Assert.Equal(0, session.Brightness);
            }
        };
        await using var port = Create(product, session);
        await port.SetLightingAsync(LightingSettingsFactory.Off(), default);
        Assert.False(session.McuCustomOverlay);
        var setters = session.Commands.Where(command => command.CommandId < 0x80).ToArray();
        Assert.Equal(3, setters.Length);
        Assert.Equal(new byte[] { 0, 5, 0 }, setters[0].Arguments.ToArray());
        Assert.Equal(new byte[] { 0, 5, 2, 0, 0, 0 }, setters[1].Arguments.ToArray());
        Assert.Equal(new byte[] { 0, 5, 0 }, setters[2].Arguments.ToArray());
        Assert.All(setters.Skip(1), command => Assert.Equal(0x0F, command.ClassId));
        int bridge = session.Commands.IndexOf(setters[1]);
        int final = session.Commands.IndexOf(setters[2]);
        Assert.Contains(session.Commands.Skip(bridge + 1).Take(final - bridge - 1),
            command => command.ClassId == 0x0F && command.CommandId == 0x82);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(50), default));
    }

    [Theory]
    [InlineData(0x02B7)]
    [InlineData(0x02B8)]
    [InlineData(0x02C7)]
    public async Task McuLighting_UnconfirmedBridgeStopsBeforeFinalOffWithoutReplay(int product)
    {
        var session = new Session
        {
            Brightness = 0,
            IgnoreWrites = true,
            McuLighting = [0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.Off(), default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        RazerCommandSpec submitted = Assert.Single(session.Commands, command => command.CommandId < 0x80);
        Assert.Equal(new byte[] { 0, 5, 2, 0, 0, 0 }, submitted.Arguments.ToArray());
    }

    [Theory]
    [InlineData(0x02B7)]
    [InlineData(0x02B8)]
    [InlineData(0x02C7)]
    public async Task McuLighting_UnreadableSpectrumParametersStayUnknownAndAllowDirectWave(int product)
    {
        var session = new Session();
        await using var port = Create(product, session);
        Assert.Null(await port.ReadStateAsync(default));
        await port.SetLightingAsync(LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), default);
        Assert.Equal(LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), await port.ReadStateAsync(default));
        RazerCommandSpec setter = Assert.Single(session.Commands, command => command.CommandId < 0x80);
        Assert.Equal(new byte[] { 0, 5, 4, 1 }, setter.Arguments.ToArray());
    }

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public async Task McuLighting_WrongWaveSpeedCannotConfirmARequestedWave(int product)
    {
        var session = new Session();
        session.BeforeSubmission = command =>
        {
            if (command.ClassId == 0x0F && command.CommandId == 0x82
                && session.Commands.Any(item => item.CommandId < 0x80))
            {
                session.McuLighting[4] = 255;
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.CommandId < 0x80);
    }
}
