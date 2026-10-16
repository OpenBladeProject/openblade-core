using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalThermalExpansionTests
{
    [Theory]
    [InlineData(0, PerformancePreset.Balanced, 0)]
    [InlineData(5, PerformancePreset.Silent, 0)]
    [InlineData(2, PerformancePreset.Performance, 1)]
    [InlineData(4, PerformancePreset.Custom, 0)]
    public async Task HyperBoost_EntersDirectlyAndRestoresEachAdmittedBase(byte mode, PerformancePreset preset, byte fan)
    {
        var session = new Session { Mode = mode, Fan = fan, Cpu = 1, Gpu = 2 };
        var pad = new Pad();
        await using var port = HyperBoostPort(session, pad);
        await port.SetHyperBoostAsync(true, preset, true, CancellationToken.None);
        Assert.Equal(new byte[][] { [1, 1, 7, 0], [1, 2, 7, 0] },
            session.Writes.Select(command => command.Arguments.ToArray()).ToArray());
        Assert.True((await port.QueryPerformanceFanStateAsync(CancellationToken.None)).HyperBoostActive);
        session.Commands.Clear();
        // Exit remains possible after pad removal; only entry depends on the pad.
        pad.State = CoolingPadConnectionState.Absent;
        await port.SetHyperBoostAsync(false, preset, true, CancellationToken.None);
        Assert.Equal(new byte[][] { [1, 1, mode, 0], [1, 2, mode, 0] },
            session.Writes.Select(command => command.Arguments.ToArray()).ToArray());
        Assert.Equal(preset, (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).Performance);
        Assert.Equal(1, session.Cpu);
        Assert.Equal(2, session.Gpu);
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.Absent)]
    [InlineData(CoolingPadConnectionState.Ambiguous)]
    [InlineData(CoolingPadConnectionState.ObservationFailed)]
    public async Task HyperBoost_RejectsUnavailablePadWithoutWrites(CoolingPadConnectionState state)
    {
        var session = new Session { Mode = 2 };
        await using var port = HyperBoostPort(session, new Pad { State = state });
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Performance, true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task HyperBoost_PadLostAtNativeSubmissionStopsAndPausesAfterPossibleWrite(int lostAt)
    {
        var session = new Session { Mode = 2 };
        var pad = new Pad();
        var policy = new ExperimentalFeaturesPolicy(Model(0x029F));
        int attempt = 0;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 2 && ++attempt == lostAt) { pad.State = CoolingPadConnectionState.Absent; }
        };
        await using var port = HyperBoostPort(session, pad, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Performance, true, CancellationToken.None));
        Assert.Equal(lostAt == 1 ? WriteSubmissionState.KnownNotApplied : WriteSubmissionState.Indeterminate,
            failure.SubmissionState);
        Assert.Equal(lostAt - 1, session.Writes.Count());
        Assert.Equal(lostAt == 1, policy.WritesAllowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HyperBoost_UnconfirmedOrFailedPairPausesWithoutReplay(bool failPair)
    {
        var session = new Session { Mode = 2, IgnoreHyperBoost = !failPair, FailModeWrite = failPair ? 2 : null };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029F));
        await using var port = HyperBoostPort(session, new Pad(), policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Performance, true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        int writes = session.Writes.Count();
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Performance, true, CancellationToken.None));
        Assert.Equal(writes, session.Writes.Count());
    }

    [Fact]
    public async Task HyperBoost_RejectsOtherModelsBeforeTraffic()
    {
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All)
        {
            var session = new Session();
            await using var port = new ExperimentalBladePort(session, new(), model, new(model),
                () => true, () => PowerSource.PluggedIn, coolingPadDiscovery: new Pad());
            if (model.ProductId != 0x029F)
            {
                await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
                    port.SetHyperBoostAsync(true, PerformancePreset.Balanced, false, CancellationToken.None));
                await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
                    port.SetHyperBoostAsync(true, PerformancePreset.Balanced, true, CancellationToken.None));
            }
            Assert.Empty(session.Commands);
            Assert.Equal(model.ProductId == 0x029F ? DeviceFeatureAccess.ReadWrite : (DeviceFeatureAccess?)null,
                port.Capabilities.HyperBoostWithoutCoolingPadAccess);
        }
    }

    [Theory]
    [InlineData(PowerSource.Battery, 0, 17)]
    [InlineData(PowerSource.UsbC, 7, 17)]
    public async Task HyperBoost_LowPowerExitUsesBalancedAutomaticDirectly(PowerSource source, byte actual, byte recommended)
    {
        var session = new Session { Mode = 7, AdapterPowerPayload = [actual, recommended] };
        await using var port = HyperBoostPort(session, new Pad(), power: () => source);
        await port.SetHyperBoostAsync(false, PerformancePreset.Balanced, true, CancellationToken.None);
        Assert.Equal(source == PowerSource.Battery ? 6 : 0, session.Mode);
        Assert.Equal(0, session.Fan);
        Assert.Equal(2, session.Writes.Count());
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Balanced, true, CancellationToken.None));
        Assert.Equal(2, session.Writes.Count());
    }

    [Fact]
    public async Task HyperBoost_MaximumFansCleanupPreservesOtherPowerOptions()
    {
        var session = new Session { Mode = 4, Cpu = 1, Gpu = 2, PowerModeControlFlags = 3 };
        await using var port = HyperBoostPort(session, new Pad());
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Custom, true, CancellationToken.None));
        Assert.Empty(session.Writes);
        Assert.Equal(3, session.PowerModeControlFlags);
        session.PowerModeControlFlags = 2;
        await port.SetHyperBoostAsync(true, PerformancePreset.Custom, true, CancellationToken.None);
        Assert.Equal(0, session.PowerModeControlFlags);
        Assert.Equal(new byte[] { 0x0F, 2, 2 }, session.Writes.Select(command => command.CommandId).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HyperBoost_PowerOrIdentityLostAtSubmissionBlocksTheFirstWrite(bool identityLoss)
    {
        var session = new Session { Mode = 2 };
        bool exact = true;
        PowerSource source = PowerSource.PluggedIn;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId != 2) { return; }
            if (identityLoss) { exact = false; } else { source = PowerSource.Battery; }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029F));
        await using var port = new ExperimentalBladePort(session, new(), Model(0x029F), policy,
            () => exact, () => source, coolingPadDiscovery: new Pad());
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Performance, true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(7, 17)]
    [InlineData(16, 17)]
    public async Task HyperBoost_InsufficientOrUnknownAdapterCannotEnter(byte actual, byte recommended)
    {
        var session = new Session { Mode = 2, AdapterPowerPayload = [actual, recommended] };
        await using var port = HyperBoostPort(session, new Pad());
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Performance, true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(0, 0)]
    public async Task HyperBoost_OlderMcuCannotSubmitEntry(byte major, byte minor)
    {
        var session = new Session { Mode = 2, HyperBoostFirmware = [major, minor, 0, 0] };
        await using var port = HyperBoostPort(session, new Pad());
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Performance, true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Contains("1.5.0.0", failure.Message);
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(0, PerformancePreset.Balanced, 0)]
    [InlineData(5, PerformancePreset.Silent, 0)]
    [InlineData(2, PerformancePreset.Performance, 1)]
    [InlineData(4, PerformancePreset.Custom, 0)]
    public async Task PadlessHyperBoost_UsesSamePairAndRestoresBase(byte mode, PerformancePreset preset, byte fan)
    {
        var session = new Session { Mode = mode, Fan = fan, Cpu = 1, Gpu = 2 };
        await using var port = HyperBoostPort(session, new Pad { State = CoolingPadConnectionState.Absent });
        await port.SetHyperBoostAsync(true, preset, false, CancellationToken.None);
        Assert.Equal(new byte[][] { [1, 1, 7, 0], [1, 2, 7, 0] },
            session.Writes.Select(command => command.Arguments.ToArray()).ToArray());
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 0 && command.CommandId == 0x58);
        await port.SetHyperBoostAsync(false, preset, false, CancellationToken.None);
        Assert.Equal(preset, (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).Performance);
        Assert.Equal(4, session.Writes.Count());
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.PresentExact)]
    [InlineData(CoolingPadConnectionState.Ambiguous)]
    [InlineData(CoolingPadConnectionState.ObservationFailed)]
    public async Task PadlessHyperBoost_RejectsUnconfirmedAbsence(CoolingPadConnectionState state)
    {
        var session = new Session { Mode = 2 };
        await using var port = HyperBoostPort(session, new Pad { State = state });
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Performance, false, CancellationToken.None));
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task PadlessHyperBoost_PadConnectedBetweenCommandsStopsWithoutSwitchingPath()
    {
        var session = new Session { Mode = 2 };
        var pad = new Pad { State = CoolingPadConnectionState.Absent };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029F));
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 2 && command.Arguments.Span[1] == 2)
            {
                pad.State = CoolingPadConnectionState.PresentExact;
            }
        };
        await using var port = HyperBoostPort(session, pad, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Performance, false, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Writes);
        Assert.False(policy.WritesAllowed);
    }

    private static ExperimentalBladePort HyperBoostPort(Session session, Pad pad,
        ExperimentalFeaturesPolicy? policy = null, Func<PowerSource>? power = null) =>
        new(session, new(), Model(0x029F), policy ?? new(Model(0x029F)), () => true,
            power ?? (() => PowerSource.PluggedIn), coolingPadDiscovery: pad);

    private sealed class Pad : ICoolingPadDiscoverySource
    {
        internal CoolingPadConnectionState State { get; set; } = CoolingPadConnectionState.PresentExact;
        public CoolingPadConnectionState Observe() => State;
    }
}
