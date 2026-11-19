using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    public static TheoryData<int> LegacyCurveProducts => new() { 0x029D, 0x029F };

    public static TheoryData<int, PerformancePreset, byte> LegacyCurvePresetCases => new()
    {
        { 0x029D, PerformancePreset.Balanced, 0 },
        { 0x029D, PerformancePreset.Silent, 5 },
        { 0x029D, PerformancePreset.Custom, 4 },
        { 0x029F, PerformancePreset.Balanced, 0 },
        { 0x029F, PerformancePreset.Silent, 5 },
        { 0x029F, PerformancePreset.Performance, 2 },
        { 0x029F, PerformancePreset.Custom, 4 },
    };

    [Theory]
    [MemberData(nameof(LegacyCurvePresetCases))]
    public async Task LegacyStaticCurves_UseExistingFixedTargetsAndCurrentPresetWithoutMetadataOrDefaults(
        int product, PerformancePreset preset, byte mode)
    {
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(Model(product), true);
        Assert.Contains(FanMode.ManualCurve, capabilities.FanModes);
        Assert.Empty(capabilities.FanSpeedRangesByPerformancePreset);
        Assert.Equal(new FanSpeedRange(product == 0x029F ? 2000 : 2200, 5000, 100), capabilities.FanSpeedRange);
        Assert.Equal(capabilities.FanSpeedRange, capabilities.GetFanIntentSpeedRange(PerformancePreset.Custom));
        var session = new Session { Mode = mode, EchoThermalAck = true };
        await using var port = Create(product, session, power: () => PowerSource.ExternalUnknown);
        await port.SetFanModeAsync(FanMode.ManualCurve, default);
        Assert.Equal(mode, session.Mode);
        Assert.Equal((byte)1, session.Manual);
        Assert.Equal((50, 50), ((int)session.Fan1, (int)session.Fan2));
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x02));
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x01));
        Assert.All(session.Commands.Where(command => command.CommandId == 0x02),
            command => Assert.Equal(mode, command.Arguments.Span[2]));
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 0x0D && command.CommandId == 0x07);
        Assert.All(session.Commands.Where(command => command.ClassId == 0x0D && command.CommandId == 0x82),
            command => Assert.Equal(0, command.Arguments.Span[0]));
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x83 or 0x88);
        AssertFreshAdapterBeforeSetters(session);
        session.Commands.Clear();
        Assert.Equal((3300, 3400), await port.SetCurveFanTargetsAsync(preset, 3300, 3400, default));
        Assert.Equal((33, 34), ((int)session.Fan1, (int)session.Fan2));
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x02 or 0x83 or 0x88);
        AssertFreshAdapterBeforeSetters(session);
    }

    [Theory]
    [MemberData(nameof(LegacyCurveProducts))]
    public async Task LegacyStaticCurves_RejectUnadmittedModesAndHyperBoostWithoutChangingPreset(int product)
    {
        foreach (byte mode in new byte[] { 1, 3, 7 })
        {
            var session = new Session { Mode = mode, EchoThermalAck = true };
            await using var port = Create(product, session);
            BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanModeAsync(FanMode.ManualCurve, default));
            Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
            Assert.Equal(mode, session.Mode);
            Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        }
        var wrongIntent = new Session { Manual = 1, EchoThermalAck = true };
        await using var expectedPort = Create(product, wrongIntent);
        await Assert.ThrowsAsync<BladeWriteException>(() => expectedPort.SetCurveFanTargetsAsync(PerformancePreset.Silent, 3500, 3500, default));
        Assert.DoesNotContain(wrongIntent.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [MemberData(nameof(LegacyCurveProducts))]
    public async Task LegacyStaticCurves_RejectDcBeforeQueriesAndRetainExistingFixedDcTransition(int product)
    {
        foreach (PowerSource source in new[] { PowerSource.Battery, PowerSource.UsbC })
        {
            var blocked = new Session { EchoThermalAck = true };
            await using var curvePort = Create(product, blocked, power: () => source);
            await Assert.ThrowsAsync<BladeWriteException>(() => curvePort.SetFanModeAsync(FanMode.ManualCurve, default));
            await Assert.ThrowsAsync<BladeWriteException>(() => curvePort.SetCurveFanTargetsAsync(PerformancePreset.Balanced, 3500, 3500, default));
            Assert.Empty(blocked.Commands);
        }
        var fixedDc = new Session { Adapter = [0, 0x11], EchoThermalAck = true };
        await using var fixedPort = Create(product, fixedDc, power: () => PowerSource.Battery);
        await fixedPort.SetFanTargetsAsync(3300, 3400, default);
        Assert.Equal((byte)6, fixedDc.Mode);
        Assert.Equal((byte)1, fixedDc.Manual);
        Assert.Equal((33, 34), ((int)fixedDc.Fan1, (int)fixedDc.Fan2));
    }

    [Theory]
    [MemberData(nameof(LegacyCurveProducts))]
    public async Task LegacyStaticCurves_MatchingExplicitEntryStillWritesButOwnedMaintenanceCanNoOp(int product)
    {
        var session = new Session { Manual = 1, Fan1 = 50, Fan2 = 50, EchoThermalAck = true };
        await using var port = Create(product, session);
        await port.SetFanModeAsync(FanMode.ManualCurve, default);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x01));
        session.Commands.Clear();
        Assert.Equal((5000, 5000), await port.SetCurveFanTargetsAsync(PerformancePreset.Balanced, 5000, 5000, default));
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [MemberData(nameof(LegacyCurveProducts))]
    public async Task LegacyStaticCurves_PresetDriftBeforeTargetBlocksAndAfterFirstTargetPauses(int product)
    {
        foreach (bool afterFirst in new[] { false, true })
        {
            var session = new Session { Manual = 1, EchoThermalAck = true };
            bool drifted = false;
            session.BeforeSubmission = command =>
            {
                if (!drifted && command.CommandId == 0x82 && session.Commands.Any(item => item.CommandId == 0x81)
                    && session.Commands.Any(item => item.CommandId == 0x01) == afterFirst)
                {
                    session.Mode = 5;
                    drifted = true;
                }
            };
            var policy = new ExperimentalFeaturesPolicy(Model(product));
            await using var port = Create(product, session, policy);
            BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
                port.SetCurveFanTargetsAsync(PerformancePreset.Balanced, 3300, 3400, default));
            Assert.True(drifted);
            Assert.Equal(afterFirst ? WriteSubmissionState.Indeterminate : WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
            Assert.Equal(afterFirst ? 1 : 0, session.Commands.Count(command => command.CommandId == 0x01));
            Assert.Equal(!afterFirst, policy.WritesAllowed);
        }
    }

    public static IEnumerable<object[]> CurveAdapterChangeCases =>
        from product in new[] { 0x029D, 0x029F, 0x02A0, 0x0245 }
        from manual in new[] { false, true }
        from actual in new byte[] { 0x0C, 0, 0xFE }
        select new object[] { product, manual, actual };

    [Theory]
    [MemberData(nameof(CurveAdapterChangeCases))]
    public async Task HostCurves_RequireFreshFullAdapterAtEveryFlagAndTargetSetterEvenWhenWindowsAcStaysConstant(
        int product, bool alreadyManual, byte changedActual)
    {
        var session = new Session
        {
            Manual = alreadyManual ? (byte)1 : (byte)0,
            Fan1 = product == 0x02A0 ? (byte)30 : (byte)45,
            Fan2 = product == 0x02A0 ? (byte)30 : (byte)45,
            EchoThermalAck = true,
        };
        byte firstSet = alreadyManual ? (byte)0x01 : (byte)0x02;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x8C && session.Commands.Any(item => item.CommandId == firstSet))
            {
                session.Adapter = [changedActual, 0x11];
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy, power: () => PowerSource.ExternalUnknown);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanModeAsync(FanMode.ManualCurve, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Commands, command => command.ClassId == 0x0D && command.CommandId < 0x80);
        Assert.False(policy.WritesAllowed);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanModeAsync(FanMode.Automatic, default));
        Assert.Equal(before, session.Commands.Count);
    }
    [Theory]
    [InlineData(0x029D, 1)]
    [InlineData(0x029D, 2)]
    [InlineData(0x029F, 1)]
    [InlineData(0x029F, 2)]
    public async Task LegacyStaticCurves_PinOriginalRawWindowsPowerAtNativeSubmission(int product, int changedAt)
    {
        var session = new Session { Manual = 1, EchoThermalAck = true };
        PowerSource raw = PowerSource.ExternalUnknown;
        int attempts = 0;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x01 && ++attempts == changedAt) { raw = PowerSource.Battery; }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy, power: () => raw);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCurveFanTargetsAsync(PerformancePreset.Balanced, 3300, 3400, default));
        Assert.Equal(changedAt == 1 ? WriteSubmissionState.KnownNotApplied : WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(changedAt - 1, session.Commands.Count(command => command.CommandId == 0x01));
        Assert.Equal(changedAt == 1, policy.WritesAllowed);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)4)]
    public async Task LegacyStaticCurves_RetainExistingMaximumFansOverrideGuard(byte mode)
    {
        var session = new Session { Mode = mode, Flags = 2, EchoThermalAck = true };
        await using var port = Create(0x029F, session);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanModeAsync(FanMode.ManualCurve, default));
        Assert.Equal((byte)2, session.Flags);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

}
