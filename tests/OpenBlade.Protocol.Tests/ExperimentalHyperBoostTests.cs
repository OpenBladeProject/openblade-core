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
                () => true, () => PowerSource.PluggedIn, coolingPadDiscovery: new Pad(), systemSku: AdmittedSku);
            if (model.ProductId is not (0x029F or 0x029D or 0x02B7))
            {
                await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
                    port.SetHyperBoostAsync(true, PerformancePreset.Balanced, false, CancellationToken.None));
                await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
                    port.SetHyperBoostAsync(true, PerformancePreset.Balanced, true, CancellationToken.None));
            }
            Assert.Empty(session.Commands);
            Assert.Equal(model.ProductId is 0x029F or 0x029D or 0x02B7 ? DeviceFeatureAccess.ReadWrite : (DeviceFeatureAccess?)null,
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
        if (source == PowerSource.Battery)
        {
            await Assert.ThrowsAsync<BladeWriteException>(() =>
                port.SetHyperBoostAsync(true, PerformancePreset.Balanced, true, CancellationToken.None));
            Assert.Equal(2, session.Writes.Count());
        }
        else
        {
            await port.SetHyperBoostAsync(true, PerformancePreset.Balanced, true, CancellationToken.None);
            Assert.Equal(7, session.Mode);
            Assert.Equal(4, session.Writes.Count());
        }
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
            () => exact, () => source, coolingPadDiscovery: new Pad(), systemSku: AdmittedSku);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Performance, true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(0, 17)]
    [InlineData(16, 17)]
    public async Task HyperBoost_ContradictoryOrUnknownAdapterCannotEnter(byte actual, byte recommended)
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

    [Theory]
    [InlineData(PowerSource.Battery)]
    [InlineData(PowerSource.PluggedIn)]
    public async Task Blade16_BalancedFanModeFreezesSourceBeforeAdapterPreflight(PowerSource source)
    {
        PowerSource initial = source;
        var session = new Session { Mode = source == PowerSource.Battery ? (byte)6 : (byte)0 };
        session.BeforeSubmission = command =>
        {
            if (command.ClassId == 7 && command.CommandId == 0x8C)
            {
                source = initial == PowerSource.Battery ? PowerSource.PluggedIn : PowerSource.Battery;
                session.AdapterPowerPayload = source == PowerSource.Battery ? [0, 17] : [17, 17];
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029F));
        await using var port = Create(0x029F, session, policy, () => source);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanTargetsAsync(3000, 3000, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.True(policy.WritesAllowed);
    }

    // Synapse writes raw 6 on battery and sets the plugged-in mode on AC attach
    // without reading it first, so raw 6 can remain after external power returns.
    // It reads as DC Balanced there: no AC intent matches it, and a typed AC set
    // leaves it without a prerequisite write.
    [Theory]
    [InlineData(0x029F, PowerSource.PluggedIn)]
    [InlineData(0x029F, PowerSource.UsbC)]
    [InlineData(0x029D, PowerSource.PluggedIn)]
    [InlineData(0x029D, PowerSource.UsbC)]
    public async Task Blade16_RawSixOnExternalPowerIsTransitionalDcBalanced(int pid, PowerSource source)
    {
        var session = new Session { Mode = 6, AdapterPowerPayload = source == PowerSource.UsbC ? [2, 17] : [17, 17] };
        await using var port = Create(pid, session, power: () => source);
        ProductionPerformanceFanState state = await port.QueryPerformanceFanStateAsync(CancellationToken.None);
        Assert.Equal(PerformancePreset.BatteryBalanced, state.Performance);
        Assert.False(state.HyperBoostActive);
        Assert.Empty(session.Writes);
        await port.SetPerformanceAsync(PerformancePreset.Balanced, CancellationToken.None);
        Assert.Equal(new byte[][] { [1, 1, 0, 0], [1, 2, 0, 0] },
            session.Writes.Select(command => command.Arguments.ToArray()).ToArray());
        Assert.Equal(PerformancePreset.Balanced,
            (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).Performance);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x029D)]
    public async Task Blade16_BatteryExitThenReplugLeavesRawSixUntilTypedAcSet(int pid)
    {
        PowerSource source = PowerSource.PluggedIn;
        // Silent is a direct entry base on both products.
        var session = new Session { Mode = 5, HyperBoostFirmware = [1, 5, 0, 0] };
        await using var port = HyperBoostPort(session, new Pad(), power: () => source, pid: pid);
        await port.SetHyperBoostAsync(true, PerformancePreset.Silent, true, CancellationToken.None);
        Assert.Equal(7, session.Mode);

        source = PowerSource.Battery;
        session.AdapterPowerPayload = [0, 17];
        await port.SetHyperBoostAsync(false, PerformancePreset.Silent, true, CancellationToken.None);
        Assert.Equal(6, session.Mode);
        Assert.Equal(PerformancePreset.Balanced,
            (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).Performance);

        source = PowerSource.PluggedIn;
        session.AdapterPowerPayload = [17, 17];
        int writes = session.Writes.Count();
        ProductionPerformanceFanState replugged = await port.QueryPerformanceFanStateAsync(CancellationToken.None);
        Assert.Equal(PerformancePreset.BatteryBalanced, replugged.Performance);
        Assert.False(replugged.HyperBoostActive);
        Assert.Equal(writes, session.Writes.Count());

        await port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None);
        Assert.Equal(5, session.Mode);
        Assert.Equal(PerformancePreset.Silent,
            (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).Performance);
        await port.SetHyperBoostAsync(true, PerformancePreset.Silent, true, CancellationToken.None);
        Assert.Equal(7, session.Mode);
        Assert.True((await port.QueryPerformanceFanStateAsync(CancellationToken.None)).HyperBoostActive);
    }

    // Synapse enters Custom directly from the current mode (671 mw-6737 bytes
    // 1,398,696/1,398,993; 669 bytes 1,558,801/1,559,098; 695 bytes
    // 1,893,039/1,893,371), so a plugged-in Custom profile leaves raw 6 itself.
    [Theory]
    [InlineData(0x029F, 0, "RZ09-0483TEH3")]
    [InlineData(0x029D, 0, "RZ09-0483TEH3")]
    [InlineData(0x02B7, 1, "RZ09-0510TEH3")]
    public async Task ModeSeven_CustomPluggedInProfileLeavesRawSixAfterBatteryExit(int pid, byte acMode, string sku)
    {
        PowerSource source = PowerSource.PluggedIn;
        CustomPerformanceCombination combination = ExperimentalBladePort.CreateCapabilities(Model(pid), true)
            .CustomPerformance!.AllowedCombinations.OrderBy(item => item.Cpu).ThenBy(item => item.Gpu).First();
        var custom = new CustomPerformanceSettings(combination.Cpu, combination.Gpu);
        var session = new Session { Mode = acMode, HyperBoostFirmware = [1, 5, 0, 0] };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = HyperBoostPort(session, new Pad { State = CoolingPadConnectionState.Absent }, policy,
            power: () => source, pid: pid, sku: sku);
        await port.SetCustomPerformanceAsync(custom, CancellationToken.None);
        await port.SetHyperBoostAsync(true, PerformancePreset.Custom, false, CancellationToken.None);
        Assert.Equal(7, session.Mode);

        source = PowerSource.Battery;
        session.AdapterPowerPayload = [0, 17];
        await port.SetHyperBoostAsync(false, PerformancePreset.Custom, false, CancellationToken.None);
        Assert.Equal(6, session.Mode);
        // On battery Custom stays refused from raw 6, as before.
        await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetCustomPerformanceAsync(custom, CancellationToken.None));
        Assert.Equal(6, session.Mode);

        source = PowerSource.PluggedIn;
        session.AdapterPowerPayload = [17, 17];
        Assert.Equal(PerformancePreset.BatteryBalanced,
            (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).Performance);
        int before = session.Writes.Count();
        await port.SetCustomPerformanceAsync(custom, CancellationToken.None);
        Assert.Equal(new byte[][] { [1, 1, 4, 0], [1, 2, 4, 0] },
            session.Writes.Skip(before).Where(command => command.ClassId == 0x0D && command.CommandId == 0x02)
                .Select(command => command.Arguments.ToArray()).ToArray());
        ProductionPerformanceFanState applied = await port.QueryPerformanceFanStateAsync(CancellationToken.None);
        Assert.Equal(PerformancePreset.Custom, applied.Performance);
        Assert.Equal(custom with { MaximumFans = false }, applied.CustomPerformance! with { MaximumFans = false });
        Assert.True(policy.WritesAllowed);

        // HyperBoost re-entry from the applied Custom profile is a direct entry.
        await port.SetHyperBoostAsync(true, PerformancePreset.Custom, false, CancellationToken.None);
        Assert.Equal(7, session.Mode);
    }

    // The DC Balanced Custom baseline is admitted only on the three mode-7 models.
    [Fact]
    public async Task OtherDcBalancedModel_CustomFromRawSixStaysRefusedOnAc()
    {
        var session = new Session { Mode = 6 };
        await using var port = Create(0x02C5, session, power: () => PowerSource.PluggedIn);
        Assert.Equal(PerformancePreset.BatteryBalanced,
            (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).Performance);
        CustomPerformanceCombination combination = ExperimentalBladePort.CreateCapabilities(Model(0x02C5), true)
            .CustomPerformance!.AllowedCombinations.First();
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(
            new(combination.Cpu, combination.Gpu), CancellationToken.None));
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(0x029F, false)]
    [InlineData(0x029F, true)]
    [InlineData(0x029D, false)]
    [InlineData(0x029D, true)]
    public async Task LimitedExternal_CustomAndHyperBoostRoundTripRetainsNativePowerChecks(int pid, bool requiresPad)
    {
        var session = new Session { AdapterPowerPayload = [2, 17], HyperBoostFirmware = [1, 5, 0, 0] };
        var pad = new Pad { State = requiresPad ? CoolingPadConnectionState.PresentExact : CoolingPadConnectionState.Absent };
        await using var port = HyperBoostPort(session, pad, power: () => PowerSource.UsbC, pid: pid);
        await port.SetCustomPerformanceAsync(new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Medium), default);
        Assert.Equal(4, session.Mode);
        await port.SetHyperBoostAsync(true, PerformancePreset.Custom, requiresPad, default);
        Assert.Equal(7, session.Mode);
        await port.SetHyperBoostAsync(false, PerformancePreset.Custom, requiresPad, default);
        Assert.Equal(4, session.Mode);
        Assert.Equal(AdapterPowerState.UsbCPower45W, await port.QueryAdapterPowerStateAsync(default));
        for (int i = 0; i < session.Commands.Count; i++)
        {
            if (session.Commands[i].CommandId < 0x80)
            {
                Assert.True(i > 0);
                Assert.Equal((0x07, 0x8C), (session.Commands[i - 1].ClassId, session.Commands[i - 1].CommandId));
            }
        }
    }

    // SKU character 9 is T: outside the vendor HyperBoost recheck's disabled set.
    private const string AdmittedSku = "RZ09-0483TEH3";

    private static ExperimentalBladePort HyperBoostPort(Session session, Pad pad,
        ExperimentalFeaturesPolicy? policy = null, Func<PowerSource>? power = null, int pid = 0x029F,
        string? sku = AdmittedSku) =>
        new(session, new(), Model(pid), policy ?? new(Model(pid)), () => true,
            power ?? (() => PowerSource.ExternalUnknown), coolingPadDiscovery: pad, systemSku: sku);

    private sealed class Pad : ICoolingPadDiscoverySource
    {
        internal CoolingPadConnectionState State { get; set; } = CoolingPadConnectionState.PresentExact;
        public CoolingPadConnectionState Observe() => State;
    }
    [Theory]
    [InlineData(0x03)]
    [InlineData(0x05)]
    [InlineData(0x02)]
    public async Task HyperBoost_UnclassifiedAdapterCannotClearMaximumFansBeforeEntry(byte status)
    {
        HidSessionResult<ReadOnlyMemory<byte>> adapter = status == 2
            ? new(new byte[] { 0xFF, 0xFF }, null)
            : new(default, HidSessionFailureKind.ProtocolFailure)
            {
                Diagnostic = new(HidSessionFailurePhase.ProtocolValidation,
                    HidSessionFailureKind.ProtocolFailure, 0, 90)
                {
                    ProtocolValidationFailure = HidProtocolValidationFailure.Status,
                },
                QueryResponseStatus = status,
            };
        adapter = adapter with { UnclassifiedAdapterResponseAvailable = true };
        var session = new Session
        {
            Mode = 4,
            Cpu = 1,
            Gpu = 2,
            PowerModeControlFlags = 2,
            AdapterPowerResult = adapter,
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029F));
        await using var port = HyperBoostPort(session, new Pad(), policy,
            power: () => PowerSource.ExternalUnknown);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Custom, true, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.Equal((byte)2, session.PowerModeControlFlags);
        Assert.Equal((byte)4, session.Mode);
        Assert.Equal((byte)1, session.Cpu);
        Assert.Equal((byte)2, session.Gpu);
        Assert.True(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.ClassId == 7 && command.CommandId == 0x8C);
    }

}
