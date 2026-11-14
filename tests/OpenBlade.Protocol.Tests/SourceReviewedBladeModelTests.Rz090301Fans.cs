using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    [Fact]
    public async Task Rz090301_ConfirmedDynamicRangeReachesCapabilitiesAndDoesNotWidenPausedAccess()
    {
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        var session = new Session { DynamicFanRange = [0, 0, 0, 0, 0, 0, 40, 0, 45, 0, 50, 0] };
        await using var port = Create(0x0245, session, policy);
        Assert.Equal(5300, port.Capabilities.GetFanSpeedRange(PerformancePreset.Balanced)!.MaximumRpm);

        await port.SetFanModeAsync(FanMode.ManualCurve, default);
        FanSpeedRange range = Assert.IsType<FanSpeedRange>(port.Capabilities.GetFanSpeedRange(PerformancePreset.Balanced));
        Assert.Equal(new FanSpeedRange(4000, 5000, 100), range);
        Assert.Equal(range, port.Capabilities.ObservedFanSpeedRangesByPerformancePreset[PerformancePreset.Balanced]);
        Assert.Equal(new FanSpeedRange(3700, 5300, 100), port.Capabilities.FanSpeedRange);
        Assert.Equal(new FanSpeedRange(3700, 5300, 100),
            port.Capabilities.FanSpeedRangesByPerformancePreset[PerformancePreset.Balanced]);
        // The service's missing-temperature curve fallback consumes this maximum,
        // not the original source envelope. Its next target update must succeed.
        await port.SetFanTargetsAsync(range.MaximumRpm, range.MaximumRpm, default);
        Assert.Equal((50, 50), ((int)session.Fan1, (int)session.Fan2));

        await using var reopened = Create(0x0245, new Session(), policy);
        Assert.Equal(range, reopened.Capabilities.GetFanSpeedRange(PerformancePreset.Balanced));
        Assert.Equal(5300, ExperimentalBladePort.CreateCapabilities(Model(0x0245), true).FanSpeedRange!.MaximumRpm);
        policy.RequireRecovery(ExperimentalRecoveryScope.ThermalOnly);
        Assert.Null(port.Capabilities.FanSpeedRange);
        Assert.Empty(port.Capabilities.FanModes);
    }

    [Fact]
    public async Task Rz090301_FixedFansUseFreshDynamicRangeClassOneTargetsAndIndependentReadback()
    {
        var session = new Session { DynamicFanRange = [0, 0, 0, 0, 0, 0, 40, 0, 45, 0, 50, 0] };
        await using var port = Create(0x0245, session);
        await port.SetFanTargetsAsync(4600, 4700, default);
        Assert.Equal((byte)1, session.Manual);
        Assert.Equal((byte)0, session.Mode);
        Assert.Equal((46, 47), ((int)session.Fan1, (int)session.Fan2));
        Assert.All(session.Commands.Where(command => command.CommandId == 0x81),
            command => Assert.Equal(1, command.Arguments.Span[0]));
        Assert.All(session.Commands.Where(command => command.CommandId == 0x83),
            command => Assert.Equal(new byte[12], command.Arguments.ToArray()));
        RazerCommandSpec[] modeSets = session.Commands.Where(command => command.CommandId == 0x02).ToArray();
        Assert.Equal(2, modeSets.Length);
        Assert.All(modeSets, command => Assert.Equal(new byte[] { 1, command.Arguments.Span[1], 0, 1 }, command.Arguments.ToArray()));
        int lastTargetSet = -1;
        for (int index = 0; index < session.Commands.Count; index++)
        {
            if (session.Commands[index].CommandId != 0x01) { continue; }
            Assert.Contains(session.Commands.Skip(lastTargetSet + 1).Take(index - lastTargetSet - 1),
                command => command.CommandId == 0x83);
            lastTargetSet = index;
        }
        AssertFreshAdapterBeforeSetters(session);
        await port.SetFanModeAsync(FanMode.Automatic, default);
        Assert.Equal((byte)0, session.Manual);
        Assert.Equal((46, 47), ((int)session.Fan1, (int)session.Fan2));
    }

    [Theory]
    [InlineData(0, 45, 53)]
    [InlineData(50, 45, 53)]
    [InlineData(37, 55, 53)]
    [InlineData(10, 20, 30)]
    public async Task Rz090301_InvalidOrDisjointDynamicRangeBlocksBeforeWrites(byte minimum, byte normal, byte maximum)
    {
        var session = new Session { DynamicFanRange = [0, 0, 0, 0, 0, 0, minimum, 0, normal, 0, maximum, 0] };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(4500, 4500, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.True(policy.WritesAllowed);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Fact]
    public async Task Rz090301_DynamicIntersectionAndMetadataFailureBlockBeforeWrites()
    {
        foreach (Session session in new Session[]
        {
            new() { DynamicFanRange = [0, 0, 0, 0, 0, 0, 40, 0, 45, 0, 50, 0] },
            new() { RejectDynamicFanRange = true },
            new() { DynamicFanRange = [1, 0, 0, 0, 0, 0, 37, 0, 45, 0, 53, 0] },
            new() { DynamicFanRange = new byte[3] },
        })
        {
            var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
            await using var port = Create(0x0245, session, policy);
            BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(3700, 3700, default));
            Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
            Assert.True(policy.WritesAllowed);
            Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        }
    }

    [Fact]
    public async Task Rz090301_MetadataFailureAfterFirstModeFlagStopsSecondFlagAndTargetsWithoutReplay()
    {
        var session = new Session();
        int metadataQueries = 0;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x83 && ++metadataQueries >= 3)
            {
                session.DynamicFanRange = new byte[12];
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(4500, 4500, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.CommandId == 0x02);
        Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x01);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanModeAsync(FanMode.Automatic, default));
        Assert.Equal(before, session.Commands.Count);
    }

    [Fact]
    public async Task Rz090301_StaleTargetReadbackPausesAndRecoveryRemainsReadOnly()
    {
        var session = new Session { Manual = 1, IgnoreWrites = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(4600, 4600, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.CommandId == 0x01);
        int before = session.Commands.Count;
        ConfirmedDeviceState recovered = await port.ReadRecoveryStateAsync(default);
        Assert.Equal(FanMode.Fixed, recovered.Fans);
        Assert.All(session.Commands.Skip(before), command => Assert.True(command.CommandId >= 0x80));
        Assert.False(policy.WritesAllowed);
    }

    [Fact]
    public async Task Rz090301_MetadataFailsAfterFirstTargetStopsSecondAndReconcilesReadOnly()
    {
        var session = new Session { Manual = 1 };
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x83 && session.Commands.Any(item => item.CommandId == 0x01))
            {
                session.DynamicFanRange = new byte[12];
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanTargetsAsync(4600, 4700, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.CommandId == 0x01);
        Assert.Equal((46, 45), ((int)session.Fan1, (int)session.Fan2));
        session.BeforeSubmission = null;
        session.DynamicFanRange = [0, 0, 0, 0, 0, 0, 37, 0, 45, 0, 53, 0];
        int before = session.Commands.Count;
        Assert.Equal(FanMode.Fixed, (await port.ReadRecoveryStateAsync(default)).Fans);
        Assert.All(session.Commands.Skip(before), command => Assert.True(command.CommandId >= 0x80));
        Assert.False(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(PowerSource.UsbC)]
    [InlineData(PowerSource.ExternalUnknown)]
    public async Task Rz090301_LimitedExternalDoesNotAddDynamicManualTargets(PowerSource source)
    {
        var session = new Session { Adapter = [2, 17] };
        await using var port = Create(0x0245, session, power: () => source);
        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanTargetsAsync(4600, 4700, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        Assert.False(port.Capabilities.SupportsManualFansOnLimitedPower);
    }

    [Theory]
    [InlineData(PowerSource.Battery)]
    [InlineData(PowerSource.UsbC)]
    [InlineData((PowerSource)999)]
    public async Task Rz090301_ManualFansRequireExistingFullPowerAdmission(PowerSource power)
    {
        var session = new Session();
        await using (var port = Create(0x0245, session, power: () => power))
        {
            await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(4500, 4500, default));
            Assert.Empty(session.Commands);
        }
        PowerSource current = PowerSource.PluggedIn;
        var changed = new Session { Manual = 1 };
        changed.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x01) { current = power; }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var queued = Create(0x0245, changed, policy, power: () => current);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => queued.SetFanTargetsAsync(4600, 4600, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.True(policy.WritesAllowed);
        Assert.DoesNotContain(changed.Commands, command => command.CommandId == 0x01);
    }

    [Fact]
    public async Task Rz090301_CustomRemainsAutomaticAndCurveUsesExistingBalancedTargets()
    {
        var custom = new Session { Mode = 4 };
        await using (var port = Create(0x0245, custom))
        {
            await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(4500, 4500, default));
            Assert.DoesNotContain(custom.Commands, command => command.CommandId < 0x80);
            Assert.DoesNotContain(custom.Commands, command => command.CommandId is 0x81 or 0x83);
        }
        var manual = new Session { Manual = 1 };
        await using (var port = Create(0x0245, manual))
        {
            await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(new(CustomPerformanceLevel.High, CustomPerformanceLevel.High), default));
            Assert.DoesNotContain(manual.Commands, command => command.CommandId < 0x80);
        }
        var curve = new Session { DynamicFanRange = [0, 0, 0, 0, 0, 0, 40, 0, 45, 0, 50, 0] };
        await using var curvePort = Create(0x0245, curve);
        await curvePort.SetFanModeAsync(FanMode.ManualCurve, default);
        Assert.Equal((50, 50), ((int)curve.Fan1, (int)curve.Fan2));
        Assert.All(curve.Commands.Where(command => command.CommandId < 0x80),
            command => Assert.True(command.ClassId == 0x0D && command.CommandId is 0x01 or 0x02));
    }

    [Theory]
    [InlineData((byte)4, (byte)1)]
    [InlineData((byte)0, (byte)0)]
    public async Task Rz090301_PresetOrManualDriftBeforeFirstTargetBlocksWithoutWrites(byte mode, byte manual)
    {
        var session = new Session { Manual = 1 };
        bool changed = false;
        session.BeforeSubmission = command =>
        {
            // Drift after the baseline target pair, at the next paired state
            // confirmation. Metadata remains valid and overlaps the request.
            if (!changed && command.ClassId == 0x0D && command.CommandId == 0x82
                && session.Commands.Count(item => item.ClassId == 0x0D && item.CommandId == 0x81) >= 2
                && !session.Commands.Any(item => item.CommandId == 0x01))
            {
                changed = true;
                session.Mode = mode;
                session.Manual = manual;
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanTargetsAsync(4600, 4700, default));
        Assert.True(changed);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.True(policy.WritesAllowed);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        Assert.Equal((45, 45), ((int)session.Fan1, (int)session.Fan2));
    }

    [Theory]
    [InlineData((byte)4, (byte)1)]
    [InlineData((byte)0, (byte)0)]
    public async Task Rz090301_PresetOrManualDriftAfterFirstTargetPausesWithoutSecondOrReplay(byte mode, byte manual)
    {
        var session = new Session { Manual = 1 };
        bool changed = false;
        session.BeforeSubmission = command =>
        {
            if (!changed && command.ClassId == 0x0D && command.CommandId == 0x82
                && session.Commands.Any(item => item.ClassId == 0x0D && item.CommandId == 0x01))
            {
                changed = true;
                session.Mode = mode;
                session.Manual = manual;
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanTargetsAsync(4600, 4700, default));
        Assert.True(changed);
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.ClassId == 0x0D && command.CommandId == 0x01);
        Assert.Equal((46, 45), ((int)session.Fan1, (int)session.Fan2));
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanModeAsync(FanMode.Automatic, default));
        Assert.Equal(before, session.Commands.Count);
    }

    [Theory]
    [InlineData((byte)4, (byte)0)]
    [InlineData((byte)0, (byte)1)]
    public async Task Rz090301_ModeEntryRechecksPresetAndManualBeforeFirstFlagWrite(byte mode, byte manual)
    {
        var session = new Session();
        bool changed = false;
        session.BeforeSubmission = command =>
        {
            if (!changed && command.ClassId == 0x0D && command.CommandId == 0x82
                && session.Commands.Any(item => item.ClassId == 0x0D && item.CommandId == 0x81))
            {
                changed = true;
                session.Mode = mode;
                session.Manual = manual;
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanTargetsAsync(4600, 4700, default));
        Assert.True(changed);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.True(policy.WritesAllowed);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Fact]
    public async Task Rz090301_MetadataDriftBeforeModeEntryBlocksWithoutFlagOrTargetWrite()
    {
        var session = new Session();
        int metadataQueries = 0;
        session.BeforeSubmission = command =>
        {
            if (command.ClassId == 0x0D && command.CommandId == 0x83 && ++metadataQueries == 2)
            {
                session.DynamicFanRange = new byte[12];
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanTargetsAsync(4600, 4700, default));
        Assert.Equal(2, metadataQueries);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.True(policy.WritesAllowed);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        Assert.Equal((byte)0, session.Manual);
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn)]
    [InlineData(PowerSource.Battery)]
    public async Task Rz090301_AutomaticHandbackDoesNotRequireManualRangeOrTargets(PowerSource power)
    {
        var session = new Session
        {
            Manual = 1,
            RejectDynamicFanRange = true,
            Fan1 = 30,
            Fan2 = 70,
            Adapter = power == PowerSource.Battery ? [0, 0x11] : [0x11, 0x11],
        };
        await using var port = Create(0x0245, session, power: () => power);
        await port.SetFanModeAsync(FanMode.Automatic, default);
        Assert.Equal((byte)0, session.Manual);
        Assert.Equal((byte)0, session.Mode);
        Assert.Equal((30, 70), ((int)session.Fan1, (int)session.Fan2));
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x81 or 0x83 or 0x01);
        RazerCommandSpec[] setters = session.Commands.Where(command => command.CommandId < 0x80).ToArray();
        Assert.Equal(2, setters.Length);
        Assert.All(setters, command =>
        {
            Assert.Equal(0x0D, command.ClassId);
            Assert.Equal(0x02, command.CommandId);
            Assert.Equal(new byte[] { 1, command.Arguments.Span[1], 0, 0 }, command.Arguments.ToArray());
        });
    }

    [Fact]
    public async Task Rz090301_BasicPerformanceFlagQueryDoesNotRequireManualTargetMetadata()
    {
        var session = new Session { Manual = 1, RejectDynamicFanRange = true, Fan1 = 30, Fan2 = 70 };
        await using var port = Create(0x0245, session);
        ProductionPerformanceFanState state = await port.QueryPerformanceFanStateAsync(default);
        Assert.Equal(PerformancePreset.Balanced, state.Performance);
        Assert.Equal(FanMode.Fixed, state.Fans);
        Assert.Equal(2, session.Commands.Count);
        Assert.All(session.Commands, command =>
        {
            Assert.Equal(0x0D, command.ClassId);
            Assert.Equal(0x82, command.CommandId);
        });
    }

}
