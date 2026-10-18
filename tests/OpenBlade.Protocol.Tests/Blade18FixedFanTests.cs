using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalThermalExpansionTests
{
    [Fact]
    public async Task Blade18BalancedBatteryFixedFans_KeepModeZeroAndConfirmTheSharedTarget()
    {
        var session = new Session { AdapterPowerPayload = [0, 0x11] };
        await using var port = Create(0x02A0, session, power: () => PowerSource.Battery);
        await port.SetFanTargetsAsync(3000, 3000, CancellationToken.None);
        Assert.Equal(0, session.Mode);
        Assert.Equal(1, session.Fan);
        Assert.Equal(30, session.FirstTarget);
        Assert.Equal(30, session.SecondTarget);
        Assert.All(session.Writes.Where(command => command.CommandId == 2),
            command => Assert.Equal(new byte[] { 0, 1 }, command.Arguments.ToArray()[2..]));
    }

    [Fact]
    public async Task Blade18FixedFans_RejectWrongTargetRegisterClassBeforeAnyWrite()
    {
        var session = new Session { FanTargetClassOverride = 0 };
        await using var port = Create(0x02A0, session);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanTargetsAsync(3000, 3000, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.Equal([(0x0D, 0x81, FirmwareQueryOutcome.ContentRejected)], session.RetainedQueryFailures);
    }

    [Fact]
    public void Blade18FixedFans_ExposePresetRangesAndBalancedCurveWithoutDirectTransitions()
    {
        DeviceCapabilities caps = ExperimentalBladePort.CreateCapabilities(Model(0x02A0), true);
        Assert.Equal(new FanSpeedRange(2200, 3800, 100), caps.FanSpeedRange);
        Assert.Equal(new FanSpeedRange(2200, 3800, 100),
            caps.GetFanSpeedRange(PerformancePreset.Balanced));
        Assert.Equal(new FanSpeedRange(3700, 5300, 100),
            caps.GetFanSpeedRange(PerformancePreset.Silent));
        Assert.Null(caps.GetFanSpeedRange(PerformancePreset.Performance));
        Assert.True(caps.FanModes.SetEquals(
            [FanMode.Automatic, FanMode.Fixed, FanMode.ManualCurve]));
        Assert.False(caps.SupportsDirectManualFanTransitions);
        Assert.True(caps.ReportsPerformanceWithManualFans);
        Assert.Null(ExperimentalBladePort.CreateCapabilities(Model(0x02A0), false).FanSpeedRange);
    }

    [Fact]
    public async Task Blade18SilentFixedFans_UseModeFiveAndSilentRange()
    {
        var session = new Session
        {
            Mode = 5,
            FirstTarget = 30,
            SecondTarget = 30,
        };
        await using var port = Create(0x02A0, session);

        await port.SetFanControlAsync(
            new FanControlSettings(FanMode.Fixed, 5300, ManualFanCurve.Default),
            CancellationToken.None);

        Assert.Equal(5, session.Mode);
        Assert.Equal(1, session.Fan);
        Assert.Equal(53, session.FirstTarget);
        Assert.Equal(53, session.SecondTarget);
        Assert.All(session.Writes.Where(command => command.CommandId == 2),
            command => Assert.Equal(new byte[] { 5, 1 }, command.Arguments.ToArray()[2..]));
    }

    [Fact]
    public async Task Blade18BalancedFixedFans_ConfirmBothStoredTargetsAndRestoreBaseline()
    {
        var session = new Session { FirstTarget = 38, SecondTarget = 38 };
        await using var port = Create(0x02A0, session);

        await port.SetFanControlAsync(new FanControlSettings(FanMode.Fixed, 3000, ManualFanCurve.Default), CancellationToken.None);
        Assert.Equal(0, session.Mode);
        Assert.Equal(1, session.Fan);
        Assert.Equal(30, session.FirstTarget);
        Assert.Equal(30, session.SecondTarget);
        Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x88);
        Assert.All(session.Commands.Where(command => command.CommandId == 0x81),
            command => Assert.Equal(1, command.Arguments.Span[0]));
        RazerCommandSpec[] writes = session.Writes.ToArray();
        Assert.Equal(new byte[] { 2, 2, 1, 1 }, writes.Select(command => command.CommandId));
        Assert.All(writes.Take(2), command => Assert.Equal(new byte[] { 0, 1 }, command.Arguments.ToArray()[2..]));

        await port.SetFanControlAsync(new FanControlSettings(FanMode.Fixed, 3800, ManualFanCurve.Default), CancellationToken.None);
        await port.SetFanModeAsync(FanMode.Automatic, CancellationToken.None);
        Assert.Equal(0, session.Mode);
        Assert.Equal(0, session.Fan);
        Assert.Equal(38, session.FirstTarget);
        Assert.Equal(38, session.SecondTarget);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(2)]
    [InlineData(7)]
    public async Task Blade18FixedFans_RejectOtherPresetsWithoutChangingThem(byte mode)
    {
        var session = new Session { Mode = mode };
        await using var port = Create(0x02A0, session);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanControlAsync(new FanControlSettings(FanMode.Fixed, 3000, ManualFanCurve.Default), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.Equal(mode, session.Mode);
    }

    [Theory]
    [InlineData(2100, 2100)]
    [InlineData(5400, 5400)]
    public async Task Blade18FixedFans_RejectUnsupportedTargetsBeforeTraffic(int first, int second)
    {
        var session = new Session();
        await using var port = Create(0x02A0, session);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetFanTargetsAsync(first, second, CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Blade18FixedFans_RequireRestorableStoredTargetsBeforeAnyWrite()
    {
        var session = new Session { FirstTarget = 0 };
        await using var port = Create(0x02A0, session);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanControlAsync(new FanControlSettings(FanMode.Fixed, 3000, ManualFanCurve.Default), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.Equal([(0x0D, 0x81, FirmwareQueryOutcome.ContentRejected)], session.RetainedQueryFailures);
    }

    [Fact]
    public async Task Blade18FixedFans_IgnoredFirstTargetPausesBeforeSecondTargetWithoutReplay()
    {
        var session = new Session { Fan = 1, IgnoreFanTargets = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02A0));
        await using var port = Create(0x02A0, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanControlAsync(new FanControlSettings(FanMode.Fixed, 3000, ManualFanCurve.Default), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Writes);
        Assert.False(policy.WritesAllowed);
        await port.QueryPerformanceFanStateAsync(CancellationToken.None);
        Assert.Single(session.Writes);
    }

    [Fact]
    public async Task Blade18FixedFans_PowerChangeAtNativeSubmissionBlocksBeforeWrite()
    {
        PowerSource power = PowerSource.PluggedIn;
        var session = new Session();
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 2) { power = PowerSource.Battery; }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02A0));
        await using var port = Create(0x02A0, session, policy, () => power);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanControlAsync(new FanControlSettings(FanMode.Fixed, 3000, ManualFanCurve.Default), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.True(policy.WritesAllowed);
    }

    [Fact]
    public async Task Blade18PerformanceSetters_DoNotCarryFixedFansIntoAnotherPreset()
    {
        var session = new Session { Fan = 1 };
        await using var port = Create(0x02A0, session);
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None));
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(CustomPerformanceSettings.Default, CancellationToken.None));
        Assert.Empty(session.Writes);
        await port.SetFanModeAsync(FanMode.Automatic, CancellationToken.None);
        await port.SetCustomPerformanceAsync(CustomPerformanceSettings.Default, CancellationToken.None);
        Assert.Equal(4, session.Mode);
        Assert.Equal(0, session.Fan);
        Assert.DoesNotContain(session.Writes, command => command.CommandId == 1);
    }
}
