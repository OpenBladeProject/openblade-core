using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    // Independent expectations transcribed from the reviewed exact-model
    // middleware/UI intersections, rather than generated from production policy.
    public static TheoryData<int, PerformancePreset, int, int> DynamicFanPresetCases => new()
    {
        { 0x0234, PerformancePreset.Balanced, 3700, 5200 },
        { 0x024C, PerformancePreset.Balanced, 3700, 5200 },
        { 0x0245, PerformancePreset.Balanced, 3700, 5300 },
        { 0x0253, PerformancePreset.Balanced, 3700, 5300 },
        { 0x0246, PerformancePreset.Balanced, 3500, 5500 },
        { 0x0246, PerformancePreset.Gaming, 3900, 5500 },
        { 0x0246, PerformancePreset.Creator, 3900, 5500 },
        { 0x0268, PerformancePreset.Balanced, 3600, 5200 },
        { 0x0268, PerformancePreset.Gaming, 3600, 5200 },
        { 0x0268, PerformancePreset.Creator, 3600, 5200 },
        { 0x0270, PerformancePreset.Balanced, 3300, 5400 },
        { 0x0276, PerformancePreset.Balanced, 3400, 5200 },
        { 0x028C, PerformancePreset.Balanced, 3300, 5400 },
        { 0x028C, PerformancePreset.Silent, 3300, 5400 },
        { 0x028A, PerformancePreset.Balanced, 3400, 5200 },
        { 0x028A, PerformancePreset.Silent, 3400, 5200 },
        { 0x028B, PerformancePreset.Balanced, 2200, 3800 },
        { 0x028B, PerformancePreset.Silent, 2200, 3800 },
        { 0x02B6, PerformancePreset.Balanced, 2000, 5400 },
        { 0x02B6, PerformancePreset.Silent, 2200, 3800 },
        { 0x02B6, PerformancePreset.Turbo, 2000, 5400 },
        { 0x02B7, PerformancePreset.Balanced, 3400, 5200 },
        { 0x02B7, PerformancePreset.Silent, 3400, 3800 },
        { 0x02B7, PerformancePreset.Turbo, 3300, 5400 },
        { 0x02B8, PerformancePreset.Balanced, 3700, 5300 },
        { 0x02B8, PerformancePreset.Silent, 3700, 5300 },
        { 0x02B8, PerformancePreset.Turbo, 3300, 5400 },
        { 0x02C7, PerformancePreset.Balanced, 3400, 5200 },
        { 0x02C7, PerformancePreset.Silent, 3400, 3800 },
        { 0x02C7, PerformancePreset.Turbo, 3300, 5400 },
        { 0x02C7, PerformancePreset.MaximumPower, 3300, 5400 },
        { 0x02C7, PerformancePreset.Custom, 4000, 5300 },
        { 0x02C7, PerformancePreset.BatteryBalanced, 3400, 5200 },
        { 0x0255, PerformancePreset.Balanced, 4300, 5500 },
        { 0x0255, PerformancePreset.Performance, 4900, 5500 },
        { 0x0255, PerformancePreset.Creator, 4900, 5500 },
        { 0x02C5, PerformancePreset.Balanced, 3400, 5200 },
        { 0x02C5, PerformancePreset.Silent, 3400, 3800 },
        { 0x02C5, PerformancePreset.MaximumPower, 3300, 5400 },
        { 0x02C5, PerformancePreset.Custom, 4000, 5300 },
        { 0x02C5, PerformancePreset.BatteryBalanced, 3400, 5200 },
    };

    private static byte FanPresetWireMode(PerformancePreset preset) => preset switch
    {
        PerformancePreset.Balanced => 0,
        PerformancePreset.Gaming or PerformancePreset.Turbo or PerformancePreset.Performance => 1,
        PerformancePreset.Creator or PerformancePreset.MaximumPower => 2,
        PerformancePreset.Custom => 4,
        PerformancePreset.Silent => 5,
        PerformancePreset.BatteryBalanced => 6,
        _ => throw new InvalidOperationException(),
    };

    private static byte[] FanMetadata(int minimum, int maximum) =>
        [0, 0, 0, 0, 0, 0, checked((byte)(minimum / 100)), 0,
            checked((byte)((minimum + (maximum - minimum) / 200 * 100) / 100)), 0, checked((byte)(maximum / 100)), 0];

    [Theory]
    [MemberData(nameof(DynamicFanPresetCases))]
    public async Task DynamicFanPresets_HaveExactBoundsAndRestorableTypedTargets(
        int product, PerformancePreset preset, int minimum, int maximum)
    {
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(Model(product), true);
        Assert.Equal(new FanSpeedRange(minimum, maximum, 100), capabilities.GetFanSpeedRange(preset));
        bool battery = preset == PerformancePreset.BatteryBalanced;
        int saved = minimum + 100;
        var session = new Session
        {
            Mode = FanPresetWireMode(preset),
            Manual = 1,
            Fan1 = checked((byte)(saved / 100)),
            Fan2 = checked((byte)(saved / 100)),
            DynamicFanRange = FanMetadata(minimum, maximum),
            Adapter = battery ? [0, 0x11] : [0x11, 0x11],
        };
        await using var port = Create(product, session, power: () => battery ? PowerSource.Battery : PowerSource.ExternalUnknown);
        await port.SetFanTargetsAsync(maximum, maximum, default);
        Assert.Equal(FanPresetWireMode(preset), session.Mode);
        Assert.Equal((maximum / 100, maximum / 100), ((int)session.Fan1, (int)session.Fan2));
        Assert.Equal((byte)1, session.Manual);
        Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x02);
        Assert.All(session.Commands.Where(command => command.ClassId == 0x0D && command.CommandId == 0x81),
            command => Assert.Equal(1, command.Arguments.Span[0]));
        Assert.Equal(new FanSpeedRange(minimum, maximum, 100), port.Capabilities.GetFanSpeedRange(preset));
        AssertFreshAdapterBeforeSetters(session);
        await port.SetFanTargetsAsync(saved, saved, default);
        Assert.Equal((saved / 100, saved / 100), ((int)session.Fan1, (int)session.Fan2));
        session.Commands.Clear();
        session.RejectDynamicFanRange = true;
        await port.SetFanModeAsync(FanMode.Automatic, default);
        Assert.Equal((byte)0, session.Manual);
        Assert.Equal(FanPresetWireMode(preset), session.Mode);
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x81 or 0x83 or 0x01);
    }

    [Theory]
    [MemberData(nameof(DynamicFanPresetCases))]
    public async Task DynamicFanCurves_UseCurrentAcPresetAndFreshMaximumWithoutPresetReset(
        int product, PerformancePreset preset, int minimum, int maximum)
    {
        var session = new Session
        {
            Mode = FanPresetWireMode(preset),
            Fan1 = checked((byte)(minimum / 100)),
            Fan2 = checked((byte)(minimum / 100)),
            DynamicFanRange = FanMetadata(minimum, maximum - 100),
            Adapter = preset == PerformancePreset.BatteryBalanced ? [0, 0x11] : [0x11, 0x11],
        };
        await using var port = Create(product, session,
            power: () => preset == PerformancePreset.BatteryBalanced ? PowerSource.Battery : PowerSource.ExternalUnknown);
        if (preset == PerformancePreset.BatteryBalanced)
        {
            await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanModeAsync(FanMode.ManualCurve, default));
            Assert.Empty(session.Commands);
            return;
        }
        await port.SetFanModeAsync(FanMode.ManualCurve, default);
        Assert.Equal(FanPresetWireMode(preset), session.Mode);
        Assert.Equal(((maximum - 100) / 100, (maximum - 100) / 100), ((int)session.Fan1, (int)session.Fan2));
        Assert.All(session.Commands.Where(command => command.CommandId == 0x02),
            command => Assert.Equal(FanPresetWireMode(preset), command.Arguments.Span[2]));
        AssertFreshAdapterBeforeSetters(session);
    }

    [Theory]
    [InlineData(0x0246, (byte)1)]
    [InlineData(0x028C, (byte)5)]
    [InlineData(0x02C7, (byte)4)]
    public async Task DynamicFanMetadata_DriftCannotPublishRangeForEitherPreset(int product, byte destination)
    {
        var session = new Session { Manual = 1 };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x83)
            {
                session.Mode = destination;
            }
        };
        await using var port = Create(product, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(4500, 4500, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(policy.ConfirmedFanRanges);
        Assert.True(policy.WritesAllowed);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Fact]
    public async Task DynamicFanMetadata_ObservationsRemainPresetSpecificAcrossReopenAndPause()
    {
        var policy = new ExperimentalFeaturesPolicy(Model(0x0246));
        var session = new Session { Manual = 1, DynamicFanRange = FanMetadata(4000, 5000) };
        await using var port = Create(0x0246, session, policy);
        await port.SetFanTargetsAsync(4600, 4600, default);
        session.Mode = 1;
        session.DynamicFanRange = FanMetadata(3900, 4800);
        await port.SetFanTargetsAsync(4700, 4700, default);
        await using var reopened = Create(0x0246, new Session(), policy);
        Assert.Equal(new FanSpeedRange(4000, 5000, 100), reopened.Capabilities.GetFanSpeedRange(PerformancePreset.Balanced));
        Assert.Equal(new FanSpeedRange(3900, 4800, 100), reopened.Capabilities.GetFanSpeedRange(PerformancePreset.Gaming));
        Assert.Equal(new FanSpeedRange(3900, 5500, 100), reopened.Capabilities.GetFanSpeedRange(PerformancePreset.Creator));
        policy.RequireRecovery(ExperimentalRecoveryScope.ThermalOnly);
        Assert.Empty(port.Capabilities.FanModes);
        Assert.Empty(reopened.Capabilities.FanSpeedRangesByPerformancePreset);
    }

    [Theory]
    [MemberData(nameof(DynamicFanPresetCases))]
    public async Task DynamicFanMetadata_FailureAfterFirstTargetStopsSecondAndPauses(
        int product, PerformancePreset preset, int minimum, int maximum)
    {
        var session = new Session
        {
            Manual = 1,
            Mode = FanPresetWireMode(preset),
            Fan1 = checked((byte)(minimum / 100)),
            Fan2 = checked((byte)(minimum / 100)),
            DynamicFanRange = FanMetadata(minimum, maximum),
            Adapter = preset == PerformancePreset.BatteryBalanced ? [0, 0x11] : [0x11, 0x11],
        };
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x83 && session.Commands.Any(item => item.CommandId == 0x01))
            {
                session.RejectDynamicFanRange = true;
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy,
            power: () => preset == PerformancePreset.BatteryBalanced ? PowerSource.Battery : PowerSource.ExternalUnknown);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanTargetsAsync(maximum, maximum, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Commands, command => command.CommandId == 0x01);
        Assert.False(policy.WritesAllowed);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanModeAsync(FanMode.Automatic, default));
        Assert.Equal(before, session.Commands.Count);
        session.RejectDynamicFanRange = false;
        session.BeforeSubmission = null;
        Assert.Equal(FanMode.Fixed, (await port.ReadRecoveryStateAsync(default)).Fans);
        Assert.All(session.Commands.Skip(before), command => Assert.True(command.CommandId >= 0x80));
    }

    [Theory]
    [InlineData(0x02C5)]
    [InlineData(0x02C7)]
    public async Task DynamicBatteryFixedFans_RejectContradictoryAdapterAndNativePowerChange(int product)
    {
        var session = new Session { Mode = 6, Manual = 1 };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy, power: () => PowerSource.Battery);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(4600, 4600, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        Assert.True(policy.WritesAllowed);
        PowerSource current = PowerSource.Battery;
        var changed = new Session { Mode = 6, Manual = 1, Adapter = [0, 0x11] };
        changed.BeforeSubmission = command => { if (command.CommandId == 0x01) { current = PowerSource.PluggedIn; } };
        await using var queued = Create(product, changed, power: () => current);
        await Assert.ThrowsAsync<BladeWriteException>(() => queued.SetFanTargetsAsync(4600, 4600, default));
        Assert.DoesNotContain(changed.Commands, command => command.CommandId < 0x80);
    }
    [Theory]
    [MemberData(nameof(DynamicFanPresetCases))]
    public async Task DynamicFanMetadata_InvalidBeforeSubmissionLeavesTargetsAndWriteAccessUnchanged(
        int product, PerformancePreset preset, int minimum, int maximum)
    {
        var session = new Session
        {
            Mode = FanPresetWireMode(preset),
            Manual = 1,
            Fan1 = checked((byte)(minimum / 100)),
            Fan2 = checked((byte)(minimum / 100)),
            DynamicFanRange = new byte[12],
        };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy,
            power: () => preset == PerformancePreset.BatteryBalanced ? PowerSource.Battery : PowerSource.ExternalUnknown);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanTargetsAsync(maximum, maximum, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        Assert.True(policy.WritesAllowed);
        Assert.Empty(policy.ConfirmedFanRanges);
        Assert.Equal((minimum / 100, minimum / 100), ((int)session.Fan1, (int)session.Fan2));
    }

    [Theory]
    [InlineData(0x02C5)]
    [InlineData(0x02C7)]
    public async Task DynamicBatteryFixedFans_EntryRequiresFreshBatteryAdapterBeforeEachFlagSetter(int product)
    {
        var session = new Session { Mode = 6, Adapter = [0, 0x11] };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x8C && session.Commands.Any(item => item.CommandId == 0x02))
            {
                session.Adapter = [0x11, 0x11];
            }
        };
        await using var port = Create(product, session, policy, power: () => PowerSource.Battery);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(4600, 4600, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Commands, command => command.CommandId == 0x02);
        Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x01);
        Assert.False(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(0x02C5)]
    [InlineData(0x02C7)]
    public async Task DynamicCustomManual_LevelOnlyEditsPreservePresetAndBothTargets(int product)
    {
        var session = new Session { Mode = 4, Manual = 1 };
        await using var port = Create(product, session);
        await port.SetCustomPerformanceAsync(new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Medium), default);
        Assert.Equal((byte)4, session.Mode);
        Assert.Equal((byte)1, session.Manual);
        Assert.Equal((45, 45), ((int)session.Fan1, (int)session.Fan2));
        Assert.Equal((1, 1), ((int)session.Cpu, (int)session.Gpu));
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x02 or 0x01);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x07));
        AssertFreshAdapterBeforeSetters(session);
    }

    [Fact]
    public async Task SourceReviewedBlade18_SilentCurveEntersCurrentPresetWithoutBalancedReset()
    {
        var session = new Session { Mode = 5 };
        await using var port = Create(0x02A0, session);
        await port.SetFanModeAsync(FanMode.ManualCurve, default);
        Assert.Equal((byte)5, session.Mode);
        Assert.Equal((53, 53), ((int)session.Fan1, (int)session.Fan2));
        Assert.All(session.Commands.Where(command => command.CommandId == 0x02),
            command => Assert.Equal(5, command.Arguments.Span[2]));
        Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x83);
        AssertFreshAdapterBeforeSetters(session);
    }

    [Theory]
    [InlineData(0x02C5)]
    [InlineData(0x02C7)]
    public async Task DynamicBatterySaver_AutomaticHandbackPreservesSelectorWithoutManualMetadata(int product)
    {
        var session = new Session { Mode = 3, Manual = 1, Adapter = [0, 0x11], RejectDynamicFanRange = true };
        await using var port = Create(product, session, power: () => PowerSource.Battery);
        await port.SetFanModeAsync(FanMode.Automatic, default);
        Assert.Equal((byte)3, session.Mode);
        Assert.Equal((byte)0, session.Manual);
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x81 or 0x83 or 0x01);
        Assert.All(session.Commands.Where(command => command.CommandId == 0x02),
            command => Assert.Equal(3, command.Arguments.Span[2]));
        AssertFreshAdapterBeforeSetters(session);
    }

    [Fact]
    public async Task SourceReviewedBlade15_CreatorAutomaticEntersCustomDirectlyWithoutBalancedReset()
    {
        var session = new Session { Mode = 2 };
        await using var port = Create(0x0255, session, power: () => PowerSource.ExternalUnknown);
        await port.SetCustomPerformanceAsync(CustomPerformanceSettings.Default, default);
        Assert.Equal((byte)4, session.Mode);
        Assert.Equal((byte)0, session.Manual);
        Assert.Equal((1, 2), ((int)session.Cpu, (int)session.Gpu));
        Assert.Equal(new byte[] { 4, 4 }, session.Commands.Where(command => command.CommandId == 0x02)
            .Select(command => command.Arguments.Span[2]));
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x81 or 0x83 or 0x01);
        AssertFreshAdapterBeforeSetters(session);
    }

    [Fact]
    public async Task DynamicFanMetadata_RejectionIsRetainedByTheCommandSessionBeforeAnyWrite()
    {
        var session = new Session { Manual = 1, DynamicFanRange = new byte[12] };
        await using var port = Create(0x0245, session);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(4600, 4600, default));
        Assert.Contains(((byte)0x0D, (byte)0x83, FirmwareQueryOutcome.ContentRejected), session.RetainedQueryFailures);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [InlineData((byte)1, 33)]
    [InlineData((byte)0, 53)]
    public async Task DynamicFanUpdates_BoundExchangesWithoutReusingAdmissionAcrossSetters(byte manual, int expectedExchanges)
    {
        var session = new Session { Manual = manual };
        await using var port = Create(0x0245, session, power: () => PowerSource.ExternalUnknown);
        await port.SetFanTargetsAsync(4600, 4700, default);
        Assert.Equal(expectedExchanges, session.Commands.Count);
        int previousSetter = -1;
        for (int index = 0; index < session.Commands.Count; index++)
        {
            RazerCommandSpec command = session.Commands[index];
            if (command.ClassId != 0x0D || command.CommandId is not (0x01 or 0x02)) { continue; }
            RazerCommandSpec[] admission = session.Commands.Skip(previousSetter + 1).Take(index - previousSetter - 1).ToArray();
            Assert.Contains(admission, candidate => candidate.CommandId == 0x83);
            Assert.True(admission.Count(candidate => candidate.CommandId == 0x82) >= 4);
            Assert.Equal(0x8C, session.Commands[index - 1].CommandId);
            previousSetter = index;
        }
        Assert.Equal((46, 47), ((int)session.Fan1, (int)session.Fan2));
    }

    [Fact]
    public async Task DynamicCurveTargets_ProjectFreshNarrowRangeWithoutChangingFixedRequestSemantics()
    {
        var session = new Session { Manual = 1, Fan1 = 53, Fan2 = 53, DynamicFanRange = FanMetadata(4000, 5000) };
        await using var port = Create(0x0245, session, power: () => PowerSource.ExternalUnknown);
        Assert.Equal((5000, 5000), await port.SetCurveFanTargetsAsync(PerformancePreset.Balanced, 5300, 5300, default));
        Assert.Equal((50, 50), ((int)session.Fan1, (int)session.Fan2));
        Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x02);
        session.Commands.Clear();
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(5300, 5300, default));
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        Assert.Equal(new FanSpeedRange(3700, 5300, 100), port.Capabilities.GetFanIntentSpeedRange(PerformancePreset.Balanced));
        Assert.Equal(new FanSpeedRange(4000, 5000, 100), port.Capabilities.GetFanSpeedRange(PerformancePreset.Balanced));
    }

    [Theory]
    [InlineData((byte)0, (byte)0)]
    [InlineData((byte)1, (byte)1)]
    public async Task DynamicCurveTargets_RequireExpectedPresetAndAlreadyConfirmedManualFlag(byte mode, byte manual)
    {
        var session = new Session { Mode = mode, Manual = manual };
        await using var port = Create(0x0246, session);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCurveFanTargetsAsync(PerformancePreset.Balanced, 4500, 4500, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x81 or 0x83 || command.CommandId < 0x80);
    }

    [Fact]
    public async Task DynamicCurveTargets_ReturnPerSetterActualTupleAndRetainOriginalRequestWhenRangeWidens()
    {
        var session = new Session { Manual = 1 };
        int ranges = 0;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x83)
            {
                ranges++;
                session.DynamicFanRange = FanMetadata(3700, ranges == 2 ? 5000 : ranges == 3 ? 5200 : 5300);
            }
        };
        await using var port = Create(0x0245, session);
        Assert.Equal((5000, 5200), await port.SetCurveFanTargetsAsync(PerformancePreset.Balanced, 5300, 5300, default));
        Assert.Equal((50, 52), ((int)session.Fan1, (int)session.Fan2));
        session.BeforeSubmission = null;
        session.DynamicFanRange = FanMetadata(3700, 5300);
        Assert.Equal((5300, 5300), await port.SetCurveFanTargetsAsync(PerformancePreset.Balanced, 5300, 5300, default));
        Assert.Equal((53, 53), ((int)session.Fan1, (int)session.Fan2));
    }

    [Fact]
    public async Task DynamicCurveTargets_SecondRangeExcludingFirstConfirmedTargetStopsWithoutReplay()
    {
        var session = new Session { Manual = 1 };
        int ranges = 0;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x83 && ++ranges == 3)
            {
                session.DynamicFanRange = FanMetadata(3700, 5000);
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCurveFanTargetsAsync(PerformancePreset.Balanced, 5300, 5300, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Commands, command => command.CommandId == 0x01);
        Assert.Equal((53, 45), ((int)session.Fan1, (int)session.Fan2));
        Assert.False(policy.WritesAllowed);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCurveFanTargetsAsync(PerformancePreset.Balanced, 5000, 5000, default));
        Assert.Equal(before, session.Commands.Count);
    }

    [Fact]
    public async Task DynamicFanEntry_FreshMetadataBeforeSecondFlagAcceptsOnlyKnownIndependentZoneStage()
    {
        var session = new Session { IndependentFanModeFlags = true };
        await using var port = Create(0x0245, session);
        await port.SetFanTargetsAsync(4600, 4700, default);
        Assert.Equal((byte)1, session.Manual);
        Assert.Equal((byte)1, session.SecondManual);
        Assert.Equal((46, 47), ((int)session.Fan1, (int)session.Fan2));
        Assert.Equal(53, session.Commands.Count);
        Assert.Equal(5, session.Commands.Count(command => command.CommandId == 0x83));
    }

    [Theory]
    [InlineData(0x0268, (byte)1, 33)]
    [InlineData(0x02B6, (byte)0, 34)]
    [InlineData(0x02C5, (byte)4, 37)]
    [InlineData(0x02C7, (byte)4, 37)]
    public async Task DynamicFanUpdates_ExchangeBudgetIncludesMateriallyDifferentPresetQueries(
        int product, byte mode, int expectedExchanges)
    {
        var session = new Session { Mode = mode, Manual = 1 };
        await using var port = Create(product, session, power: () => PowerSource.ExternalUnknown);
        await port.SetFanTargetsAsync(4600, 4700, default);
        Assert.Equal(expectedExchanges, session.Commands.Count);
        Assert.Equal(3, session.Commands.Count(command => command.CommandId == 0x83));
        AssertFreshAdapterBeforeSetters(session);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 5)]
    public async Task DynamicFanTargets_SecondRangeExcludingFirstTargetStopsFixedAndCurveEntryBeforeSecondSet(
        bool curveEntry, int narrowAt)
    {
        var session = new Session { Manual = curveEntry ? (byte)0 : (byte)1 };
        int ranges = 0;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x83 && ++ranges == narrowAt)
            {
                session.DynamicFanRange = FanMetadata(3700, 4800);
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => curveEntry
            ? port.SetFanModeAsync(FanMode.ManualCurve, default)
            : port.SetFanTargetsAsync(5000, 4700, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Commands, command => command.CommandId == 0x01);
        Assert.Equal(curveEntry ? 53 : 50, session.Fan1);
        Assert.Equal((byte)45, session.Fan2);
        Assert.False(policy.WritesAllowed);
    }

}
