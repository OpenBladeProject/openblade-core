using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    public static TheoryData<int> LegacyProducts => new() { 0x0246, 0x0253, 0x0268 };
    public static TheoryData<int> LegacyBaseProducts => new() { 0x0246, 0x0268 };

    [Theory]
    [MemberData(nameof(LegacyProducts))]
    public void LegacyCapabilities_ExposeOnlyExactThermalFeatures(int product)
    {
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(Model(product), true);
        PerformancePreset[] expected = product == 0x0253
            ? [PerformancePreset.Balanced, PerformancePreset.Custom]
            : [PerformancePreset.Balanced, PerformancePreset.Gaming, PerformancePreset.Creator];
        Assert.Equal(expected.Order(), capabilities.PerformancePresets.Order());
        Assert.Equal(new[] { FanMode.Automatic }, capabilities.FanModes);
        Assert.Null(capabilities.FanSpeedRange);
        Assert.Empty(capabilities.ChargeLimits);
        Assert.Empty(capabilities.LightingEffects);
        Assert.Empty(capabilities.LogoLightingModes);
        Assert.Empty(capabilities.OneShotLaptopControls);
        Assert.Empty(capabilities.HostManagedLaptopControls);
        Assert.Null(capabilities.HyperBoostAccess);
        Assert.Null(capabilities.ThxAudio);
        Assert.False(capabilities.SupportsKeyboardBrightness);
        Assert.False(capabilities.SupportsUniformLightingFrames);
        if (product == 0x0253)
        {
            CustomPerformanceCapabilities custom = Assert.IsType<CustomPerformanceCapabilities>(capabilities.CustomPerformance);
            Assert.Equal(9, custom.AllowedCombinations.Count);
            Assert.False(custom.SupportsMaximumFans);
            Assert.All(custom.AllowedCombinations, pair =>
            {
                Assert.InRange((int)pair.Cpu, 0, 2);
                Assert.InRange((int)pair.Gpu, 0, 2);
            });
        }
        else
        {
            Assert.Null(capabilities.CustomPerformance);
            Assert.Contains(ExperimentalBladeModels.FeatureEvidenceFor(Model(product)),
                item => item.Feature == "ThermalPresetsOnlyBalancedGamingCreator");
        }
    }

    [Theory]
    [MemberData(nameof(LegacyBaseProducts))]
    public async Task LegacyBase_AllPresetTransitionsUseDistinctExactRawModes(int product)
    {
        (PerformancePreset Preset, byte Raw)[] modes =
        [
            (PerformancePreset.Balanced, 0),
            (PerformancePreset.Gaming, 1),
            (PerformancePreset.Creator, 2),
        ];
        foreach (var initial in modes)
        {
            foreach (var destination in modes.Where(mode => mode != initial))
            {
                var session = new Session { Mode = initial.Raw };
                await using var port = Create(product, session);
                await port.SetPerformanceAsync(destination.Preset, default);
                Assert.Equal(destination.Preset, (await port.QueryPerformanceFanStateAsync(default)).Performance);
                RazerCommandSpec[] setters = session.Commands.Where(command => command.CommandId < 0x80).ToArray();
                Assert.Equal(2, setters.Length);
                Assert.Equal(new byte[] { 1, 1, destination.Raw, 0 }, setters[0].Arguments.ToArray());
                Assert.Equal(new byte[] { 1, 2, destination.Raw, 0 }, setters[1].Arguments.ToArray());
                Assert.All(setters, command => Assert.Equal((0x0D, 0x02), (command.ClassId, command.CommandId)));
                // Vendor GPU clock APIs are separate effects and never part of these typed thermal operations.
                Assert.All(session.Commands, command => Assert.True(
                    command.ClassId == 0x0D && command.CommandId is 0x82 or 0x02
                    || command.ClassId == 0x07 && command.CommandId == 0x8C));
                AssertFreshAdapterBeforeSetters(session);
            }
        }
    }

    [Theory]
    [MemberData(nameof(LegacyBaseProducts))]
    public async Task LegacyBase_AutomaticHandbackPreservesEveryRawPreset(int product)
    {
        foreach (byte mode in new byte[] { 0, 1, 2 })
        {
            var session = new Session { Mode = mode, Manual = 1 };
            await using var port = Create(product, session);
            await port.SetFanModeAsync(FanMode.Automatic, default);
            Assert.Equal(mode, session.Mode);
            Assert.Equal((byte)0, session.Manual);
            Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x02));
            Assert.All(session.Commands.Where(command => command.CommandId == 0x02),
                command => Assert.Equal(new byte[] { 1, command.Arguments.Span[1], mode, 0 }, command.Arguments.ToArray()));
            Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x87 or 0x07 or 0x81 or 0x01 or 0x8F or 0x0F);
            AssertFreshAdapterBeforeSetters(session);
        }
    }

    [Theory]
    [MemberData(nameof(LegacyProducts))]
    public async Task LegacyUnsupportedFeatures_RejectBeforeIo(int product)
    {
        var session = new Session();
        await using var port = Create(product, session);
        foreach (PerformancePreset preset in new[] { PerformancePreset.Silent, PerformancePreset.Performance,
            PerformancePreset.Turbo, PerformancePreset.MaximumPower, PerformancePreset.BatteryBalanced, PerformancePreset.BatterySaver })
        {
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetPerformanceAsync(preset, default));
        }
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetCustomPerformanceAsync(
            new(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High), default));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetCustomPerformanceAsync(
            new(CustomPerformanceLevel.High, CustomPerformanceLevel.High) { MaximumFans = true }, default));
        if (product != 0x0253)
        {
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetCustomPerformanceAsync(
                new(CustomPerformanceLevel.High, CustomPerformanceLevel.High), default));
        }
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.QueryProtectionAsync(default));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetChargeLimitAsync(80, default));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetFanTargetsAsync(4000, 4000, default));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetFanModeAsync(FanMode.ManualCurve, default));
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task LegacyAdvanced_CustomHighHighUsesProfileOneAndDoesNotAdmitEcDependentBoost()
    {
        var session = new Session();
        await using var port = Create(0x0253, session);
        var requested = new CustomPerformanceSettings(CustomPerformanceLevel.High, CustomPerformanceLevel.High);
        await port.SetCustomPerformanceAsync(requested, default);
        Assert.Equal(requested, (await port.QueryPerformanceFanStateAsync(default)).CustomPerformance);
        Assert.Equal((byte)4, session.Mode);
        Assert.All(session.Commands.Where(command => command.ClassId == 0x0D), command => Assert.Equal(1, command.Arguments.Span[0]));
        AssertFreshAdapterBeforeSetters(session);
        session.Cpu = 3;
        await Assert.ThrowsAsync<RazerProtocolException>(() => port.QueryPerformanceFanStateAsync(default));
    }

    [Theory]
    [MemberData(nameof(LegacyProducts))]
    public async Task LegacyUnknownModeAndStrictRecovery_DoNotInventState(int product)
    {
        byte[] unsupported = product == 0x0253 ? [1, 2, 3, 5, 6, 7] : [3, 4, 5, 6, 7];
        foreach (byte mode in unsupported)
        {
            var session = new Session { Mode = mode };
            await using var port = Create(product, session);
            await Assert.ThrowsAsync<RazerProtocolException>(() => port.ReadRecoveryStateAsync(default));
            Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        }
        var supported = new Session { Mode = product == 0x0253 ? (byte)4 : (byte)2 };
        await using var supportedPort = Create(product, supported);
        ConfirmedDeviceState recovered = await supportedPort.ReadRecoveryStateAsync(default);
        Assert.Equal(product == 0x0253 ? PerformancePreset.Custom : PerformancePreset.Creator, recovered.Performance);
        Assert.Null(recovered.Battery);
        Assert.Null(recovered.Lighting);
        Assert.Null(recovered.LogoLighting);
        Assert.DoesNotContain(supported.Commands, command => command.CommandId is 0x92 or 0x8F);
    }

    [Theory]
    [MemberData(nameof(LegacyProducts))]
    public async Task LegacyAdapterFailureOrUnknown_NeverUsesVendorPermissiveFallback(int product)
    {
        foreach (byte[] adapter in new byte[][] { [], [0x11], [0xFF, 0xFF], [0x0C, 0x10], [0, 0x11], [0x0C, 0x11] })
        {
            var session = new Session { Adapter = adapter };
            var policy = new ExperimentalFeaturesPolicy(Model(product));
            await using var port = Create(product, session, policy);
            BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => product == 0x0253
                ? port.SetCustomPerformanceAsync(new(CustomPerformanceLevel.High, CustomPerformanceLevel.High), default)
                : port.SetPerformanceAsync(PerformancePreset.Gaming, default));
            Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
            Assert.True(policy.WritesAllowed);
            Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        }
        var failedGetter = new Session { RejectAdapterQuery = true };
        await using var rejectedPort = Create(product, failedGetter);
        await Assert.ThrowsAsync<BladeWriteException>(() => product == 0x0253
            ? rejectedPort.SetCustomPerformanceAsync(new(CustomPerformanceLevel.High, CustomPerformanceLevel.High), default)
            : rejectedPort.SetPerformanceAsync(PerformancePreset.Gaming, default));
        Assert.DoesNotContain(failedGetter.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [MemberData(nameof(LegacyBaseProducts))]
    public async Task LegacyBase_SecondSelectorLosesPower_PausesWithoutReplay(int product)
    {
        var session = new Session();
        PowerSource power = PowerSource.PluggedIn;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x02 && command.Arguments.Span[1] == 2)
            {
                power = PowerSource.Battery;
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy, power: () => power);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Creator, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.CommandId == 0x02);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Creator, default));
        Assert.Equal(before, session.Commands.Count);
    }

    [Theory]
    [MemberData(nameof(LegacyBaseProducts))]
    public async Task LegacyBase_StaleReadbackPausesAndManualBaselineRequiresHandback(int product)
    {
        var session = new Session { Mode = 2, IgnoreWrites = true };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using (var port = Create(product, session, policy))
        {
            BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Gaming, default));
            Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
            Assert.False(policy.WritesAllowed);
        }
        var manual = new Session { Mode = 1, Manual = 1 };
        await using var manualPort = Create(product, manual);
        BladeWriteException manualFailure = await Assert.ThrowsAsync<BladeWriteException>(() => manualPort.SetPerformanceAsync(PerformancePreset.Creator, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, manualFailure.SubmissionState);
        Assert.DoesNotContain(manual.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [MemberData(nameof(LegacyProducts))]
    public async Task LegacyNativeIdentityRevocation_BlocksFirstSetterWithoutPausing(int product)
    {
        var session = new Session { Mode = product == 0x0253 ? (byte)4 : (byte)1 };
        bool identity = true;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x02)
            {
                identity = false;
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy, identity: () => identity);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Balanced, default));
        Assert.False(identity);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.True(policy.WritesAllowed);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [MemberData(nameof(LegacyProducts))]
    public async Task LegacyBatteryAutomaticHandback_RetainsRawBalancedZeroAndUnknownChargePolicy(int product)
    {
        var session = new Session { Mode = 0, Manual = 1, Adapter = [0, 0x0D] };
        await using var port = Create(product, session, power: () => PowerSource.Battery);
        await port.SetFanModeAsync(FanMode.Automatic, default);
        ConfirmedDeviceState state = await port.ReadRecoveryStateAsync(default);
        Assert.Equal(PerformancePreset.Balanced, state.Performance);
        Assert.Equal(FanMode.Automatic, state.Fans);
        Assert.Null(state.Battery);
        Assert.All(session.Commands.Where(command => command.CommandId == 0x02), command => Assert.Equal(0, command.Arguments.Span[2]));
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x92 or 0x12 or 0x8F or 0x0F);
        AssertFreshAdapterBeforeSetters(session);
    }

    [Fact]
    public async Task GamingAndCreator_DoNotBroadenPreviouslyAdmittedModels()
    {
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All.Where(model => model.ProductId is not (0x0246 or 0x0268)))
        {
            var session = new Session();
            await using var port = Create(model.ProductId, session);
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetPerformanceAsync(PerformancePreset.Gaming, default));
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetPerformanceAsync(PerformancePreset.Creator, default));
            Assert.Empty(session.Commands);
        }
    }
}
