using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    private static readonly int[] EarlyAcOnlyProductIds = [0x0270, 0x0276];

    private static bool IsEarlyAcOnly(int product) => EarlyAcOnlyProductIds.Contains(product);

    public static TheoryData<int> EarlyProducts => new() { 0x0270, 0x0276, 0x028C, 0x028A, 0x028B };
    public static TheoryData<int> EarlyBatteryProducts => new() { 0x028C, 0x028A, 0x028B };

    [Theory]
    [MemberData(nameof(EarlyProducts))]
    public void EarlyCapabilities_FollowExactModelSelectors(int product)
    {
        ExperimentalBladeModel model = Model(product);
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, true);
        PerformancePreset[] expected = IsEarlyAcOnly(product)
            ? [PerformancePreset.Balanced, PerformancePreset.Custom]
            : [PerformancePreset.Balanced, PerformancePreset.Custom, PerformancePreset.Silent, PerformancePreset.BatteryBalanced];
        Assert.Equal(expected.Order(), capabilities.PerformancePresets.Order());
        Assert.Equal(new[] { FanMode.Automatic, FanMode.Fixed, FanMode.ManualCurve }.Order(), capabilities.FanModes.Order());
        Assert.NotNull(capabilities.FanSpeedRange);
        Assert.NotEmpty(capabilities.FanSpeedRangesByPerformancePreset);
        Assert.False(capabilities.SupportsDirectManualFanTransitions);
        Assert.True(capabilities.ReportsPerformanceWithManualFans);
        LightingEffect[] expectedLighting = IsEarlyAcOnly(product)
            ? []
            : product == 0x028A
            ? [LightingEffect.Off, LightingEffect.Breathing, LightingEffect.Spectrum, LightingEffect.Wave]
            : [LightingEffect.Off, LightingEffect.Static, LightingEffect.Breathing, LightingEffect.Spectrum, LightingEffect.Wave];
        Assert.Equal(expectedLighting.Order(), capabilities.LightingEffects.Order());
        LogoLightingMode[] expectedLogo = product == 0x028A
            ? []
            : [LogoLightingMode.Off, LogoLightingMode.Static, LogoLightingMode.Breathing];
        Assert.Equal(expectedLogo.Order(), capabilities.LogoLightingModes.Order());
        Assert.True(capabilities.SupportsIndependentLightingControls);
        Assert.False(capabilities.SupportsBreathingColorSelection);
        Assert.Equal((IsEarlyAcOnly(product) ? [BreathingColorMode.Random] : Enum.GetValues<BreathingColorMode>()).Order(),
            capabilities.SupportedBreathingColorModes!.Order());
        Assert.Equal(!IsEarlyAcOnly(product), capabilities.SupportsWaveDirectionSelection);
        Assert.Empty(capabilities.OneShotLaptopControls);
        Assert.Empty(capabilities.HostManagedLaptopControls);
        Assert.Null(capabilities.HyperBoostAccess);
        Assert.Null(capabilities.ThxAudio);
        Assert.True(capabilities.SupportsKeyboardBrightness);
        Assert.False(capabilities.SupportsUniformLightingFrames);
        Assert.Equal(IsEarlyAcOnly(product) ? Array.Empty<int>() : [50, 55, 60, 65, 70, 75, 80, 100], capabilities.ChargeLimits.Order());
        CustomPerformanceCapabilities custom = Assert.IsType<CustomPerformanceCapabilities>(capabilities.CustomPerformance);
        Assert.Equal(12, custom.AllowedCombinations.Count);
        Assert.False(custom.SupportsMaximumFans);
        Assert.All(custom.AllowedCombinations, pair =>
        {
            Assert.InRange((int)pair.Cpu, 0, 3);
            Assert.InRange((int)pair.Gpu, 0, 2);
        });
        Assert.Equal(IsEarlyAcOnly(product) ? "NotEstablished" : "SynapseSourceBackedWithoutPhysicalValidation",
            Assert.Single(ExperimentalBladeModels.FeatureEvidenceFor(model), feature => feature.Feature == "ChargeLimits50To80Step5AndOff").Basis);
    }

    [Theory]
    [MemberData(nameof(EarlyProducts))]
    public async Task EarlyCustomBoostAndBalanced_UseProfileOneBothZonesAndIndependentReadback(int product)
    {
        var session = new Session { Mode = 0 };
        await using var port = Create(product, session);
        var requested = new CustomPerformanceSettings(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High);
        await port.SetCustomPerformanceAsync(requested, default);
        Assert.Equal(requested, (await port.QueryPerformanceFanStateAsync(default)).CustomPerformance);
        Assert.Equal((byte)4, session.Mode);
        Assert.Equal((byte)3, session.Cpu);
        Assert.Equal((byte)2, session.Gpu);
        await port.SetPerformanceAsync(PerformancePreset.Balanced, default);
        Assert.Equal(PerformancePreset.Balanced, (await port.QueryPerformanceFanStateAsync(default)).Performance);
        Assert.Equal((byte)0, session.Mode);
        Assert.All(session.Commands.Where(command => command.ClassId == 0x0D), command => Assert.Equal(1, command.Arguments.Span[0]));
        Assert.Equal(new byte[] { 1, 2, 1, 2 }, session.Commands.Where(command => command.CommandId == 0x02).Select(command => command.Arguments.Span[1]));
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x8F or 0x0F or 0x81 or 0x01 or 0x83);
        AssertFreshAdapterBeforeSetters(session);
    }

    [Theory]
    [MemberData(nameof(EarlyProducts))]
    public async Task EarlyUnadmittedPresetsFansAndMaximumFans_NeverQueryOrSubmit(int product)
    {
        var session = new Session();
        await using var port = Create(product, session);
        foreach (PerformancePreset preset in new[] { PerformancePreset.Turbo, PerformancePreset.MaximumPower, PerformancePreset.Performance, PerformancePreset.BatterySaver })
        {
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetPerformanceAsync(preset, default));
        }
        if (IsEarlyAcOnly(product))
        {
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetPerformanceAsync(PerformancePreset.Silent, default));
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetPerformanceAsync(PerformancePreset.BatteryBalanced, default));
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.QueryProtectionAsync(default));
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetChargeLimitAsync(80, default));
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetProtectionOffAsync(default));
        }
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetFanTargetsAsync(1000, 1000, default));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetFanModeAsync(FanMode.Maximum, default));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetCustomPerformanceAsync(
            new(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High) { MaximumFans = true }, default));
        Assert.Empty(session.Commands);
    }

    [Theory]
    [MemberData(nameof(EarlyProducts))]
    public async Task EarlyUnsupportedRawMode_DoesNotBecomeTurboOrSilent(int product)
    {
        foreach (byte mode in IsEarlyAcOnly(product) ? new byte[] { 1, 2, 3, 5, 6, 7 } : new byte[] { 1, 2, 3, 7 })
        {
            var session = new Session { Mode = mode };
            await using var port = Create(product, session);
            await Assert.ThrowsAsync<RazerProtocolException>(() => port.QueryPerformanceFanStateAsync(default));
            Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        }
    }

    [Theory]
    [MemberData(nameof(EarlyBatteryProducts))]
    public async Task EarlySilent_UsesExactRawFiveAndRejectsDc(int product)
    {
        var session = new Session();
        await using (var port = Create(product, session))
        {
            await port.SetPerformanceAsync(PerformancePreset.Silent, default);
            Assert.Equal(PerformancePreset.Silent, (await port.QueryPerformanceFanStateAsync(default)).Performance);
            Assert.Equal((byte)5, session.Mode);
            AssertFreshAdapterBeforeSetters(session);
        }
        session.Commands.Clear();
        await using var batteryPort = Create(product, session, power: () => PowerSource.Battery);
        await Assert.ThrowsAsync<BladeWriteException>(() => batteryPort.SetPerformanceAsync(PerformancePreset.Silent, default));
        Assert.Empty(session.Commands);
    }

    [Theory]
    [MemberData(nameof(EarlyProducts))]
    public async Task EarlyManualBaseline_RequiresExplicitHandbackWithoutTargetCommands(int product)
    {
        var session = new Session { Mode = 4, Manual = 1, Cpu = 3, Gpu = 2 };
        await using var port = Create(product, session);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Balanced, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        await port.SetFanModeAsync(FanMode.Automatic, default);
        Assert.Equal((byte)4, session.Mode);
        Assert.Equal((byte)3, session.Cpu);
        Assert.Equal((byte)2, session.Gpu);
        Assert.Equal((byte)0, session.Manual);
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x81 or 0x01 or 0x83 or 0x8F or 0x0F);
        AssertFreshAdapterBeforeSetters(session);
    }

    [Theory]
    [InlineData(0x0270)]
    [InlineData(0x0276)]
    public async Task EarlyBatteryHandback_PreservesRawZeroAndUnknownBatteryState(int product)
    {
        var session = new Session { Mode = 0, Manual = 1, Adapter = [0, 0x11] };
        await using var port = Create(product, session, power: () => PowerSource.Battery);
        await port.SetFanModeAsync(FanMode.Automatic, default);
        ConfirmedDeviceState state = await port.ReadRecoveryStateAsync(default);
        Assert.Equal(PerformancePreset.Balanced, state.Performance);
        Assert.Equal(FanMode.Automatic, state.Fans);
        Assert.Null(state.Battery);
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x92 or 0x12 or 0x8F or 0x0F);
        Assert.All(session.Commands.Where(command => command.CommandId == 0x02), command => Assert.Equal(0, command.Arguments.Span[2]));
    }

    [Theory]
    [MemberData(nameof(EarlyBatteryProducts))]
    public async Task EarlyFirmwareWithoutChargeGetter_RejectsWriteAndStrictRecoveryButKeepsThermalReads(int product)
    {
        // Older firmware, including pre-optimizer RZ09-0427 firmware, must
        // establish a successful fresh charge GET before any limiter SET.
        var session = new Session { RejectBatteryQuery = true };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetChargeLimitAsync(75, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.True(policy.WritesAllowed);
        await Assert.ThrowsAnyAsync<IOException>(() => port.ReadRecoveryStateAsync(default));
        Assert.Equal(PerformancePreset.Balanced, (await port.QueryPerformanceFanStateAsync(default)).Performance);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [MemberData(nameof(EarlyProducts))]
    public async Task EarlyUnconfirmedPreset_PausesWithoutReplay(int product)
    {
        var session = new Session { Mode = 4, IgnoreWrites = true };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Balanced, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x02));
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Balanced, default));
        Assert.Equal(before, session.Commands.Count);
    }

    [Theory]
    [MemberData(nameof(EarlyProducts))]
    public async Task EarlyIdentityLossAndNativePowerChange_BlockBeforeSubmission(int product)
    {
        var session = new Session { Mode = 4 };
        await using (var port = Create(product, session, identity: () => false))
        {
            await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Balanced, default));
            Assert.Empty(session.Commands);
        }
        PowerSource power = PowerSource.PluggedIn;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x02)
            {
                power = PowerSource.UsbC;
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var second = Create(product, session, policy, power: () => power);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => second.SetPerformanceAsync(PerformancePreset.Balanced, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.True(policy.WritesAllowed);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }
}
