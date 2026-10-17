using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalThermalExpansionTests
{
    [Theory]
    [InlineData(false, 0, PerformancePreset.Balanced, 0)]
    [InlineData(true, 0, PerformancePreset.Balanced, 0)]
    [InlineData(false, 5, PerformancePreset.Silent, 1)]
    [InlineData(true, 5, PerformancePreset.Silent, 1)]
    [InlineData(false, 4, PerformancePreset.Custom, 0)]
    [InlineData(true, 4, PerformancePreset.Custom, 0)]
    public async Task Blade14HyperBoost_UsesOwnFirmwareAndBasesWithoutStaging(bool padPresent,
        byte mode, PerformancePreset baseline, byte fan)
    {
        var session = new Session { Mode = mode, Fan = fan, Cpu = 1, Gpu = 2, HyperBoostFirmware = [1, 1, 1, 3] };
        var pad = new Pad { State = padPresent ? CoolingPadConnectionState.PresentExact : CoolingPadConnectionState.Absent };
        await using var port = HyperBoostPort(session, pad, pid: 0x029D);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, port.Capabilities.HyperBoostAccess);
        Assert.DoesNotContain(PerformancePreset.Performance, port.Capabilities.PerformancePresets);
        Assert.Equal(PerformancePreset.Balanced, HyperBoostCapabilityPolicy.EntryBaseMode(port.Capabilities, padPresent));
        await port.SetHyperBoostAsync(true, baseline, padPresent, CancellationToken.None);
        Assert.Equal(new byte[][] { [1, 1, 7, 0], [1, 2, 7, 0] },
            session.Writes.Select(command => command.Arguments.ToArray()).ToArray());
        Assert.True((await port.QueryPerformanceFanStateAsync(CancellationToken.None)).HyperBoostActive);
        pad.State = CoolingPadConnectionState.Absent;
        await port.SetHyperBoostAsync(false, baseline, padPresent, CancellationToken.None);
        Assert.Equal(baseline, (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).Performance);
        Assert.Equal(4, session.Writes.Count());
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 7 && command.CommandId == 0x0F
            || command.ClassId == 0 && command.CommandId == 0x58);
    }

    [Fact]
    public async Task Blade14HyperBoost_DefaultOverloadDoesNotInventPerformanceMode()
    {
        var session = new Session { HyperBoostFirmware = [1, 1, 1, 3] };
        await using var port = HyperBoostPort(session, new Pad { State = CoolingPadConnectionState.Absent }, pid: 0x029D);
        await port.SetHyperBoostAsync(true, CancellationToken.None);
        await port.SetHyperBoostAsync(false, CancellationToken.None);
        Assert.Equal(0, session.Mode);
        Assert.DoesNotContain(session.Writes, command => command.Arguments.Span[2] == 2);
    }

    [Fact]
    public async Task Blade14HyperBoost_RejectsUnadmittedPerformanceBaseBeforeTraffic()
    {
        var session = new Session();
        await using var port = HyperBoostPort(session, new Pad(), pid: 0x029D);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Performance, true, CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Blade14HyperBoost_RejectsMcuImmediatelyBelowItsOwnMinimum()
    {
        var session = new Session { HyperBoostFirmware = [1, 1, 1, 2] };
        await using var port = HyperBoostPort(session, new Pad(), pid: 0x029D);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Balanced, true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Contains("1.1.1.3", failure.Message);
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    public async Task Blade14HyperBoost_ActiveUnrestorableMaximumFansBlocksWithoutClearing(byte flags)
    {
        var session = new Session { Mode = 4, Cpu = 1, Gpu = 2, PowerModeControlFlags = flags };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029D));
        await using var port = HyperBoostPort(session, new Pad(), policy, pid: 0x029D);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Custom, true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Contains("Maximum Fans", failure.Message);
        Assert.Equal(flags, session.PowerModeControlFlags);
        Assert.Empty(session.Writes);
        Assert.True(policy.WritesAllowed);
    }

    [Fact]
    public async Task Blade14HyperBoost_KeepsOtherPowerFlagsWithoutWholeByteWrites()
    {
        var session = new Session { PowerModeControlFlags = 1 };
        await using var port = HyperBoostPort(session, new Pad(), pid: 0x029D);
        await port.SetHyperBoostAsync(true, PerformancePreset.Balanced, true, CancellationToken.None);
        Assert.Equal(1, session.PowerModeControlFlags);
        Assert.Equal(2, session.Writes.Count());
        Assert.All(session.Writes, command => Assert.Equal(0x0D, command.ClassId));
    }

    [Fact]
    public async Task Blade14HyperBoost_AmbiguousSecondSelectorPausesWithoutReplay()
    {
        var session = new Session { FailModeWrite = 2 };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029D));
        await using var port = HyperBoostPort(session, new Pad { State = CoolingPadConnectionState.Absent }, policy, pid: 0x029D);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Balanced, false, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(2, session.Writes.Count());
        Assert.False(policy.WritesAllowed);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Balanced, false, CancellationToken.None));
        Assert.Equal(2, session.Writes.Count());
    }

    [Theory]
    [InlineData(PowerSource.Battery, 0, 17, 6)]
    [InlineData(PowerSource.UsbC, 7, 17, 0)]
    public async Task Blade14HyperBoost_LowPowerExitUsesOwnBalancedMapping(PowerSource source,
        byte actual, byte recommended, byte mode)
    {
        var session = new Session { Mode = 7, AdapterPowerPayload = [actual, recommended] };
        await using var port = HyperBoostPort(session, new Pad(), power: () => source, pid: 0x029D);
        await port.SetHyperBoostAsync(false, PerformancePreset.Balanced, true, CancellationToken.None);
        Assert.Equal(mode, session.Mode);
        Assert.Equal(FanMode.Automatic, (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).Fans);
        Assert.Equal(2, session.Writes.Count());
    }
}
