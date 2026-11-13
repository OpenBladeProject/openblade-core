using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    public static TheoryData<int> McuLightingProducts => new()
    {
        0x0245, 0x028A, 0x028B, 0x028C, 0x02B7, 0x02B8, 0x02C7,
    };

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public async Task McuLighting_IndependentFieldsRemainEditableWithUnknownCustomPixels(int product)
    {
        var session = new Session { McuLighting = [0, 5, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        await using var port = Create(product, session);
        await port.SetIndependentLightingAsync(new(73, null), default);
        Assert.Equal(new IndependentLightingSettings(73, null), await port.ReadIndependentLightingAsync(new(0, null), default));
        RazerCommandSpec brightness = Assert.Single(session.Commands, command => command.CommandId < 0x80);
        Assert.Equal(new byte[] { 0, 5, 187 }, brightness.Arguments.ToArray());
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 0x0F);
        session.Commands.Clear();
        if (product == 0x028A)
        {
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetIndependentLightingAsync(new(null, LogoLightingMode.Breathing), default));
            Assert.Empty(session.Commands);
        }
        else
        {
            await port.SetIndependentLightingAsync(new(null, LogoLightingMode.Breathing), default);
            Assert.Equal(new IndependentLightingSettings(null, LogoLightingMode.Breathing), await port.ReadIndependentLightingAsync(new(null, LogoLightingMode.Off), default));
            Assert.Equal(2, session.Commands.Count(command => command.CommandId < 0x80));
            Assert.DoesNotContain(session.Commands, command => command.ClassId == 0x0F);
        }
    }

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public async Task McuLighting_IndependentZeroBrightnessKeepsBuiltinUnknownAcrossStatusAndRecovery(int product)
    {
        byte[][] activePayloads =
        [
            [0, 5, 1, 0, 0, 1, 20, 40, 60, 0, 0, 0],
            [0, 5, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            [0, 5, 2, 0, 0, 1, 20, 40, 60, 0, 0, 0],
            [0, 5, 2, 0, 0, 2, 20, 40, 60, 80, 100, 120],
            [0, 5, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            [0, 5, 4, 1, 40, 0, 0, 0, 0, 0, 0, 0],
            [0, 5, 4, 2, 40, 0, 0, 0, 0, 0, 0, 0],
        ];
        foreach (byte[] payload in activePayloads)
        {
            var session = new Session { McuLighting = payload.ToArray() };
            var policy = new ExperimentalFeaturesPolicy(Model(product));
            await using var port = Create(product, session, policy);
            LightingSettings? before = await port.ReadStateAsync(default);
            await port.SetIndependentLightingAsync(new(0, null), default);
            Assert.Equal(0, session.Brightness);
            Assert.Equal(payload, session.McuLighting);
            Assert.Null(await port.ReadStateAsync(default));
            Assert.Null(await port.ReadStateAsync(default));
            Assert.Equal(new IndependentLightingSettings(0, null),
                await port.ReadIndependentLightingAsync(new(0, null), default));
            ConfirmedDeviceState recovered = await port.ReadRecoveryStateAsync(default);
            Assert.Null(recovered.Lighting);
            Assert.Equal((byte)0, recovered.IndependentLighting!.Brightness);
            Assert.True(policy.WritesAllowed);
            RazerCommandSpec setter = Assert.Single(session.Commands, command => command.CommandId < 0x80);
            Assert.Equal((3, 3), ((int)setter.ClassId, (int)setter.CommandId));
            Assert.Equal(new byte[] { 0, 5, 0 }, setter.Arguments.ToArray());

            // Restoring only brightness exposes the same independently read
            // setting when representable, including its original RGB/direction.
            await port.SetIndependentLightingAsync(new(50, null), default);
            Assert.Equal(before, await port.ReadStateAsync(default));
            Assert.Equal(payload, session.McuLighting);
            Assert.DoesNotContain(session.Commands, command => command.ClassId == 0x0F && command.CommandId < 0x80
                || command.ClassId == 3 && command.CommandId == 0x0A);
        }
    }

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public async Task McuLighting_DarkReadStillRejectsMalformedOrRoundedNonzeroState(int product)
    {
        var session = new Session { Brightness = 0, McuLighting = [0, 5, 2, 0, 0, 0, 99, 0, 0, 0, 0, 0] };
        await using var port = Create(product, session);
        await Assert.ThrowsAsync<DeviceStateContentRejectedException>(() => port.ReadStateAsync(default));
        session.Brightness = 1; // Rounds to zero percent but is not confirmed dark.
        session.McuLighting = [0, 5, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        await Assert.ThrowsAsync<DeviceStateContentRejectedException>(() => port.ReadStateAsync(default));
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
    }

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public async Task McuLighting_ZeroBrightnessOffRemainsIndependentlyConfirmed(int product)
    {
        var session = new Session { Brightness = 0, McuLighting = [0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        await using var port = Create(product, session);
        Assert.Equal(LightingSettingsFactory.Off(), await port.ReadStateAsync(default));
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
    }

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public async Task McuLighting_DarkCustomRemainsUnknownAndAllowsDirectBuiltinWake(int product)
    {
        var session = new Session { Brightness = 0, EchoLightingAck = true, McuLighting = [0, 5, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        await using var port = Create(product, session);
        Assert.Null(await port.ReadStateAsync(default));
        LightingSettings requested = LightingSettingsFactory.Wave(LightingDirection.Leftward, 50);
        await port.SetLightingAsync(requested, default);
        Assert.Equal(requested, await port.ReadStateAsync(default));
        Assert.Single(session.Commands, command => command.ClassId == 3 && command.CommandId == 0x0A
            || command.ClassId == 0x0F && command.CommandId == 2);
    }

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public async Task McuLighting_DirectBuiltinReplacesKnownCustomWithoutInventingPixelBaseline(int product)
    {
        var session = new Session { McuCustomOverlay = true, McuLighting = [0, 5, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        await using var port = Create(product, session);
        Assert.Null(await port.ReadStateAsync(default));
        session.Commands.Clear();
        LightingSettings requested = LightingSettingsFactory.Wave(LightingDirection.Leftward, 50) with { ControlsKeyboardEffect = true };
        await port.SetLightingAsync(requested, default);
        Assert.Equal(requested with { ControlsKeyboardEffect = false }, await port.ReadStateAsync(default));
        Assert.False(session.McuCustomOverlay);
        Assert.Single(session.Commands, command => command.CommandId < 0x80);
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 0x0F && command.CommandId == 3);
    }

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public async Task McuLighting_CustomReadbackAfterBuiltinSubmissionCannotConfirmOrReplay(int product)
    {
        var session = new Session { IgnoreWrites = true, McuLighting = [0, 5, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.Wave(LightingDirection.Leftward, 50), default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public async Task McuLighting_ChangedColorCountAndBothWaveDirectionsAreDirect(int product)
    {
        var session = new Session { McuLighting = [0, 5, 2, 0, 0, 1, 20, 40, 60, 0, 0, 0] };
        await using var port = Create(product, session);
        LightingSettings dual = LightingSettingsFactory.BreathingDual(new(11, 22, 33), new(44, 55, 66), 50);
        await port.SetLightingAsync(dual, default);
        Assert.Equal(dual, await port.ReadStateAsync(default));
        Assert.Single(session.Commands, command => command.ClassId == 0x0F && command.CommandId == 2
            || command.ClassId == 3 && command.CommandId == 0x0A);
        foreach (LightingDirection direction in new[] { LightingDirection.Leftward, LightingDirection.Rightward })
        {
            session.Commands.Clear();
            LightingSettings wave = LightingSettingsFactory.Wave(direction, 50);
            await port.SetLightingAsync(wave, default);
            Assert.Equal(wave, await port.ReadStateAsync(default));
            RazerCommandSpec setter = Assert.Single(session.Commands, command => command.ClassId == 0x0F && command.CommandId == 2
                || command.ClassId == 3 && command.CommandId == 0x0A);
            byte wire = direction == LightingDirection.Leftward ? (byte)1 : (byte)2;
            Assert.Equal(wire, setter.Arguments.Span[setter.ClassId == 3 ? 1 : 3]);
        }
    }

    [Theory]
    [InlineData(0x02B7)]
    [InlineData(0x02B8)]
    [InlineData(0x02C7)]
    public async Task McuLighting_ColorDestinationGetsFreshIdentityAdmissionAfterBridge(int product)
    {
        bool identity = true;
        int writes = 0;
        var session = new Session { McuLighting = [0, 5, 2, 0, 0, 1, 20, 40, 60, 0, 0, 0] };
        session.BeforeSubmission = command =>
        {
            if (command.ClassId == 0x0F && command.CommandId == 2 && ++writes == 2)
            {
                identity = false;
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy, identity: () => identity);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.BreathingSingle(new(11, 22, 33), 50), default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public async Task McuLighting_ChangedBreathingColorsAreConfirmedAtTheSameCountEvenWhileDark(int product)
    {
        foreach (byte count in new byte[] { 1, 2 })
        {
            foreach (byte raw in new byte[] { 0, 128 })
            {
                var session = new Session
                {
                    EchoLightingAck = true,
                    Brightness = raw,
                    McuLighting = [0, 5, 2, 0, 0, count, 20, 40, 60,
                        count == 2 ? (byte)80 : (byte)0, count == 2 ? (byte)100 : (byte)0, count == 2 ? (byte)120 : (byte)0],
                };
                await using var port = Create(product, session);
                var primary = new RgbColor(11, 22, 33);
                var secondary = new RgbColor(44, 55, 66);
                LightingSettings requested = count == 1
                    ? LightingSettingsFactory.BreathingSingle(primary, 50)
                    : LightingSettingsFactory.BreathingDual(primary, secondary, 50);
                await port.SetLightingAsync(requested, default);
                Assert.Equal(requested, await port.ReadStateAsync(default));
                var setters = session.Commands.Where(command => command.ClassId == 0x0F && command.CommandId == 2
                    || command.ClassId == 3 && command.CommandId == 0x0A).ToArray();
                bool legacy = product is 0x0245 or 0x028A or 0x028B or 0x028C;
                Assert.Equal(legacy ? 1 : 2, setters.Length);
                byte[] colors = count == 1 ? [11, 22, 33] : [11, 22, 33, 44, 55, 66];
                byte[] expectedArguments = legacy ? [3, count, .. colors] : [0, 5, 2, 0, 0, count, .. colors];
                Assert.Equal(expectedArguments, setters[^1].Arguments.ToArray());
                if (!legacy)
                {
                    Assert.Equal(new byte[] { 0, 5, 2, 0, 0, 0 }, setters[0].Arguments.ToArray());
                    int bridge = session.Commands.IndexOf(setters[0]);
                    int destination = session.Commands.IndexOf(setters[1]);
                    Assert.Contains(session.Commands.Skip(bridge + 1).Take(destination - bridge - 1), command => command.ClassId == 0x0F && command.CommandId == 0x82);
                }
            }
        }
    }

    [Theory]
    [InlineData(0x028B)]
    [InlineData(0x028C)]
    [InlineData(0x02B8)]
    [InlineData(0x02C7)]
    public async Task McuLighting_StaticColorChangesUseOnlyTheExactFirmwarePrerequisite(int product)
    {
        var session = new Session { McuStaticEqualityShortcut = product == 0x02B8, McuLighting = [0, 5, 1, 0, 0, 1, 20, 40, 60, 0, 0, 0] };
        await using var port = Create(product, session);
        LightingSettings requested = LightingSettingsFactory.Static(new(11, 22, 33), 50);
        await port.SetLightingAsync(requested, default);
        Assert.Equal(requested, await port.ReadStateAsync(default));
        Assert.Equal(product == 0x02B8 ? 2 : 1, session.Commands.Count(command => command.ClassId == 0x0F && command.CommandId == 2
            || command.ClassId == 3 && command.CommandId == 0x0A));
        session.Commands.Clear();
        await port.SetLightingAsync(requested, default);
        Assert.Single(session.Commands, command => command.ClassId == 0x0F && command.CommandId == 2
            || command.ClassId == 3 && command.CommandId == 0x0A);
    }

    [Theory]
    [InlineData(0x02B7)]
    [InlineData(0x02B8)]
    [InlineData(0x02C7)]
    public async Task McuLighting_UnconfirmedColorBridgeStopsBeforeDestination(int product)
    {
        var session = new Session { IgnoreWrites = true, McuLighting = [0, 5, 2, 0, 0, 1, 20, 40, 60, 0, 0, 0] };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.BreathingSingle(new(11, 22, 33), 50), default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        RazerCommandSpec bridge = Assert.Single(session.Commands, command => command.CommandId < 0x80);
        Assert.Equal(new byte[] { 0, 5, 2, 0, 0, 0 }, bridge.Arguments.ToArray());
    }

    [Theory]
    [MemberData(nameof(McuLightingProducts))]
    public void McuLighting_AdmitsOnlyIndependentlyReadableBuiltInDestinations(int product)
    {
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(Model(product), true);
        var expected = new HashSet<LightingEffect> { LightingEffect.Off, LightingEffect.Breathing, LightingEffect.Wave };
        if (product is 0x0245 or 0x028A or 0x028B or 0x028C) { expected.Add(LightingEffect.Spectrum); }
        if (product is 0x0245 or 0x028B or 0x028C or 0x02B8 or 0x02C7) { expected.Add(LightingEffect.Static); }
        Assert.Equal(expected.Order(), capabilities.LightingEffects.Order());
        Assert.True(capabilities.SupportsKeyboardBrightness);
        Assert.True(capabilities.SupportsIndependentLightingControls);
        Assert.Equal(product == 0x028A ? 0 : 3, capabilities.LogoLightingModes.Count);
        Assert.False(capabilities.SupportsUniformLightingFrames);
        Assert.True(capabilities.SupportsWaveDirectionSelection);
        Assert.Equal(Enum.GetValues<BreathingColorMode>().Order(), capabilities.SupportedBreathingColorModes!.Order());
        Assert.False(capabilities.SupportsBreathingColorSelection); // Legacy flag; typed modes above are authoritative.
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
            if (baseline[2] == 1 && product is 0x028A or 0x02B7)
            {
                Assert.Null(observed); // Hidden palette means stored RGB is incomplete state.
            }
            else
            {
                Assert.NotNull(observed);
                Assert.Equal(new RgbColor(20, 40, 60), observed.Color);
                Assert.Equal(baseline[2] == 1 ? LightingEffect.Static : LightingEffect.Breathing, observed.Effect);
            }
            session.Commands.Clear();
            await port.SetLightingAsync(LightingSettingsFactory.BreathingRandom(50), default);
            Assert.Equal(BreathingColorMode.Random,
                Assert.IsType<BreathingLightingParameters>((await port.ReadStateAsync(default))!.Parameters).ColorMode);
            RazerCommandSpec setter = Assert.Single(session.Commands, command => command.CommandId < 0x80);
            Assert.Equal(product is 0x0245 or 0x028A or 0x028B or 0x028C ? new byte[] { 3, 3 } : new byte[] { 0, 5, 2, 0, 0, 0 }, setter.Arguments.ToArray());
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
        Assert.Equal(product is 0x0245 or 0x028A or 0x028B or 0x028C ? (3, 0x0A) : (0x0F, 2), ((int)setters[0].ClassId, (int)setters[0].CommandId));
        Assert.Equal(product is 0x0245 or 0x028A or 0x028B or 0x028C ? new byte[] { 1, 2 } : new byte[] { 0, 5, 4, 2 }, setters[0].Arguments.ToArray());
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
        Assert.Equal(new byte[] { 0, 5, 4, 2 }, setter.Arguments.ToArray());
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
