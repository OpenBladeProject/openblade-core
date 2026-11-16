using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    public static TheoryData<int, byte, int, int> RawFanPowerCases => new()
    {
        { 0x029F, 0, 2000, 5000 },
        { 0x029D, 0, 2200, 5000 },
        { 0x02A0, 5, 3700, 5300 },
        { 0x0245, 0, 3700, 5300 },
        { 0x0268, 1, 3600, 5200 },
        { 0x02C5, 4, 4000, 5300 },
        { 0x02C7, 0, 3400, 5200 },
    };

    private static Session RawFanSession(byte mode, int minimum, int maximum) => new()
    {
        Mode = mode,
        Fan1 = checked((byte)(minimum / 100)),
        Fan2 = checked((byte)(minimum / 100)),
        DynamicFanRange = FanMetadata(minimum, maximum),
        EchoThermalAck = true,
    };

    [Theory]
    [MemberData(nameof(RawFanPowerCases))]
    public async Task RawExternalPower_FreshFullAdapterAdmitsTypedFixedTargetsWithoutPresetReset(
        int product, byte mode, int minimum, int maximum)
    {
        var session = RawFanSession(mode, minimum, maximum);
        await using var port = Create(product, session, power: () => PowerSource.ExternalUnknown);
        await port.SetFanTargetsAsync(maximum, maximum, default);
        Assert.Equal(mode, session.Mode);
        Assert.Equal((byte)1, session.Manual);
        Assert.Equal((maximum / 100, maximum / 100), ((int)session.Fan1, (int)session.Fan2));
        AssertFreshAdapterBeforeSetters(session);
    }

    [Theory]
    [MemberData(nameof(RawFanPowerCases))]
    public async Task RawExternalPower_LimitedAdapterBlocksManualAndPermitsTargetFreeAutomatic(
        int product, byte mode, int minimum, int maximum)
    {
        var session = RawFanSession(mode, minimum, maximum);
        session.Adapter = [0x0C, 0x11];
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy, power: () => PowerSource.ExternalUnknown);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(maximum, maximum, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        Assert.True(policy.WritesAllowed);
        session.Manual = 1;
        session.RejectDynamicFanRange = true;
        session.Commands.Clear();
        await port.SetFanModeAsync(FanMode.Automatic, default);
        Assert.Equal(mode, session.Mode);
        Assert.Equal((byte)0, session.Manual);
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x81 or 0x83 or 0x01);
        AssertFreshAdapterBeforeSetters(session);
    }

    [Theory]
    [MemberData(nameof(RawFanPowerCases))]
    public async Task RawExternalPower_UnknownFailedMalformedOrBatteryAdapterNeverAuthorizesSet(
        int product, byte mode, int minimum, int maximum)
    {
        foreach (byte[]? adapter in new byte[]?[] { [0xFE, 0x11], [0x11], [0, 0x11], null })
        {
            var session = RawFanSession(mode, minimum, maximum);
            session.Adapter = adapter ?? [0x11, 0x11];
            session.RejectAdapterQuery = adapter is null;
            var policy = new ExperimentalFeaturesPolicy(Model(product));
            await using var port = Create(product, session, policy, power: () => PowerSource.ExternalUnknown);
            BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(maximum, maximum, default));
            Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
            Assert.True(policy.WritesAllowed);
            Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        }
    }

    [Theory]
    [MemberData(nameof(RawFanPowerCases))]
    public async Task RawExternalPower_NativeSubmissionRequiresUnchangedOriginalWindowsSource(
        int product, byte mode, int minimum, int maximum)
    {
        var session = RawFanSession(mode, minimum, maximum);
        session.Manual = 1;
        PowerSource raw = PowerSource.ExternalUnknown;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x01) { raw = PowerSource.PluggedIn; }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy, power: () => raw);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(maximum, maximum, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.True(policy.WritesAllowed);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [MemberData(nameof(RawFanPowerCases))]
    public async Task RawExternalPower_LimitedAdapterAfterFirstTargetStopsSecondWithoutReplay(
        int product, byte mode, int minimum, int maximum)
    {
        var session = RawFanSession(mode, minimum, maximum);
        session.Manual = 1;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x8C && session.Commands.Any(item => item.CommandId == 0x01))
            {
                session.Adapter = [0x0C, 0x11];
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy, power: () => PowerSource.ExternalUnknown);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(maximum, maximum, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.CommandId == 0x01);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanModeAsync(FanMode.Automatic, default));
        Assert.Equal(before, session.Commands.Count);
    }

    [Fact]
    public async Task RawExternalPower_StaticSilentCurveUsesFreshFullAdapterAndCurrentPreset()
    {
        var session = RawFanSession(5, 3700, 5300);
        await using var port = Create(0x02A0, session, power: () => PowerSource.ExternalUnknown);
        await port.SetFanModeAsync(FanMode.ManualCurve, default);
        Assert.Equal((byte)5, session.Mode);
        Assert.Equal((53, 53), ((int)session.Fan1, (int)session.Fan2));
        AssertFreshAdapterBeforeSetters(session);
    }
}
