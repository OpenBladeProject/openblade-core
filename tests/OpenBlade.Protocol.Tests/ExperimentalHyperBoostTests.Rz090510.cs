using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalThermalExpansionTests
{
    // RZ09-0510 SKU letters from the EC/MCU updater: 0510T, 05102 and 05103 run HyperBoost.
    private const string Rz090510Sku = "RZ09-0510TEH3";

    // The restriction qualifies a writable HyperBoost only; a disabled table has none to restrict.
    [Fact]
    public void Rz090510_EntryRestrictionNeedsAWritableHyperBoost()
    {
        DeviceCapabilities restricted = ExperimentalBladePort.CreateCapabilities(
            Model(0x02B7), true, HyperBoostSystemSkuAdmission.DisabledByVendorSkuPolicy);
        DeviceCapabilities disabled = ExperimentalBladePort.CreateCapabilities(
            Model(0x02B7), false, HyperBoostSystemSkuAdmission.DisabledByVendorSkuPolicy);

        Assert.Equal(HyperBoostEntryRestriction.VendorSystemSku, restricted.HyperBoostEntryRestriction);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, restricted.HyperBoostAccess);
        Assert.Null(disabled.HyperBoostEntryRestriction);
        Assert.NotEqual(DeviceFeatureAccess.ReadWrite, disabled.HyperBoostAccess);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task Rz090510_DecodesModeSevenAsHyperBoostEvenWithoutWriteAdmission(byte fan, bool admitted)
    {
        var session = new Session { Mode = 7, Fan = fan };
        await using var port = HyperBoostPort(session, new Pad(), pid: 0x02B7, sku: admitted ? Rz090510Sku : "RZ09-0510SEH3");
        ProductionPerformanceFanState state = await port.QueryPerformanceFanStateAsync(CancellationToken.None);
        Assert.True(state.HyperBoostActive);
        Assert.Null(state.Performance);
        Assert.Equal(fan == 0 ? FanMode.Automatic : FanMode.Fixed, state.Fans);
        // Mode 7 has no admitted dynamic fan range on 02B7, so manual targets are not read.
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 0x0D && command.CommandId == 0x81);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task Rz090510_CapabilitiesAdmitTurboBaseAndPadlessPath()
    {
        await using var port = HyperBoostPort(new Session(), new Pad(), pid: 0x02B7, sku: Rz090510Sku);
        DeviceCapabilities capabilities = port.Capabilities;
        Assert.Equal(DeviceFeatureAccess.ReadWrite, capabilities.HyperBoostAccess);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, capabilities.HyperBoostWithoutCoolingPadAccess);
        HyperBoostBaseModeCapabilities bases = Assert.IsType<HyperBoostBaseModeCapabilities>(capabilities.HyperBoostBaseModes);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, bases.AccessFor(PerformancePreset.Balanced));
        Assert.Equal(DeviceFeatureAccess.ReadWrite, bases.AccessFor(PerformancePreset.Silent));
        Assert.Equal(DeviceFeatureAccess.ReadWrite, bases.AccessFor(PerformancePreset.Turbo));
        Assert.Equal(DeviceFeatureAccess.ReadWrite, bases.AccessFor(PerformancePreset.Custom));
        // Product 695 has no Performance (raw 2) mode.
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, bases.AccessFor(PerformancePreset.Performance));
        Assert.Equal(PerformancePreset.Turbo, bases.CoolingPadEntryBaseMode);
        Assert.Equal(new Version(1, 3, 0, 0), ExperimentalBladePort.HyperBoostMinimumMcuFirmware(Model(0x02B7)));
    }

    [Theory]
    [InlineData(0, PerformancePreset.Balanced, true)]
    [InlineData(5, PerformancePreset.Silent, true)]
    [InlineData(1, PerformancePreset.Turbo, true)]
    [InlineData(4, PerformancePreset.Custom, true)]
    [InlineData(0, PerformancePreset.Balanced, false)]
    [InlineData(1, PerformancePreset.Turbo, false)]
    public async Task Rz090510_EntersDirectlyAndRestoresEachBase(byte mode, PerformancePreset preset, bool requiresPad)
    {
        var session = new Session { Mode = mode, Cpu = 1, Gpu = 2, HyperBoostFirmware = [1, 3, 0, 0] };
        var pad = new Pad { State = requiresPad ? CoolingPadConnectionState.PresentExact : CoolingPadConnectionState.Absent };
        await using var port = HyperBoostPort(session, pad, pid: 0x02B7, sku: Rz090510Sku);
        await port.SetHyperBoostAsync(true, preset, requiresPad, CancellationToken.None);
        Assert.Equal(new byte[][] { [1, 1, 7, 0], [1, 2, 7, 0] },
            session.Writes.Select(command => command.Arguments.ToArray()).ToArray());
        Assert.True((await port.QueryPerformanceFanStateAsync(CancellationToken.None)).HyperBoostActive);
        // No accessory-status write emulates a pad on either path.
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 0 && command.CommandId == 0x58);
        session.Commands.Clear();
        await port.SetHyperBoostAsync(false, preset, requiresPad, CancellationToken.None);
        Assert.Equal(new byte[][] { [1, 1, mode, 0], [1, 2, mode, 0] },
            session.Writes.Select(command => command.Arguments.ToArray()).ToArray());
        Assert.Equal(preset, (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).Performance);
        Assert.Equal(1, session.Cpu);
        Assert.Equal(2, session.Gpu);
    }

    [Fact]
    public async Task Rz090510_DefaultPadEntryUsesTurbo()
    {
        var session = new Session { Mode = 1, HyperBoostFirmware = [1, 3, 0, 0] };
        await using var port = HyperBoostPort(session, new Pad(), pid: 0x02B7, sku: Rz090510Sku);
        await port.SetHyperBoostAsync(true, CancellationToken.None);
        Assert.Equal(7, session.Mode);
        await port.SetHyperBoostAsync(false, CancellationToken.None);
        Assert.Equal(1, session.Mode);
    }

    [Fact]
    public async Task Rz090510_PerformanceBaseIsRejectedBeforeTraffic()
    {
        var session = new Session { Mode = 2 };
        await using var port = HyperBoostPort(session, new Pad(), pid: 0x02B7, sku: Rz090510Sku);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Performance, true, CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 9, 9 }, false)]
    [InlineData(new byte[] { 1, 3, 0, 0 }, true)]
    [InlineData(new byte[] { 1, 3, 0, 1 }, true)]
    [InlineData(new byte[] { 2, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 1, 3, 0 }, false)]
    public async Task Rz090510_EntryRequiresMcuFirmwareMinimum(byte[] firmware, bool admitted)
    {
        var session = new Session { Mode = 1, HyperBoostFirmware = firmware };
        await using var port = HyperBoostPort(session, new Pad(), pid: 0x02B7, sku: Rz090510Sku);
        if (admitted)
        {
            await port.SetHyperBoostAsync(true, PerformancePreset.Turbo, true, CancellationToken.None);
            Assert.Equal(7, session.Mode);
            return;
        }
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Turbo, true, CancellationToken.None));
        // A short (unreadable) MCU version reply is rejected by the exchange itself.
        if (firmware.Length == 4)
        {
            Assert.Equal(WriteSubmissionState.KnownNotApplied, Assert.IsType<BladeWriteException>(failure).SubmissionState);
            Assert.Contains("1.3.0.0", failure.Message);
        }
        Assert.Empty(session.Writes);
        Assert.Equal(1, session.Mode);
    }

    [Fact]
    public async Task Rz090510_BatteryExitConfirmsDcBalanced()
    {
        var session = new Session { Mode = 7, AdapterPowerPayload = [0, 17] };
        await using var port = HyperBoostPort(session, new Pad(), pid: 0x02B7, sku: Rz090510Sku,
            power: () => PowerSource.Battery);
        await port.SetHyperBoostAsync(false, PerformancePreset.Balanced, true, CancellationToken.None);
        Assert.Equal(new byte[][] { [1, 1, 6, 0], [1, 2, 6, 0] },
            session.Writes.Select(command => command.Arguments.ToArray()).ToArray());
        Assert.Equal(PerformancePreset.BatteryBalanced,
            (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).Performance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090510_UnconfirmedOrFailedPairPausesWithoutReplay(bool failPair)
    {
        var session = new Session
        {
            Mode = 1,
            HyperBoostFirmware = [1, 3, 0, 0],
            IgnoreHyperBoost = !failPair,
            FailModeWrite = failPair ? 2 : null,
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02B7));
        await using var port = HyperBoostPort(session, new Pad(), policy, pid: 0x02B7, sku: Rz090510Sku);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Turbo, true, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        int writes = session.Writes.Count();
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Turbo, true, CancellationToken.None));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetHyperBoostAsync(false, PerformancePreset.Turbo, true, CancellationToken.None));
        Assert.Equal(writes, session.Writes.Count());
    }

    [Theory]
    [InlineData(0x02B7, 'R')]
    [InlineData(0x02B7, 'S')]
    [InlineData(0x02B7, '5')]
    [InlineData(0x02B7, '6')]
    [InlineData(0x029F, 'R')]
    [InlineData(0x029F, 'S')]
    [InlineData(0x029F, '5')]
    [InlineData(0x029F, '6')]
    public async Task HyperBoostSkuRecheck_VendorDisabledCharacterDeclinesEntryOnly(int pid, char character)
    {
        string sku = $"RZ09-0510{character}EH3";
        var session = new Session { Mode = pid == 0x02B7 ? (byte)1 : (byte)2 };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = HyperBoostPort(session, new Pad(), policy, power: () => PowerSource.PluggedIn,
            pid: pid, sku: sku);
        DeviceCapabilities capabilities = port.Capabilities;
        // Exit and restore access never depend on the SKU; only entry is declined.
        Assert.Equal(DeviceFeatureAccess.ReadWrite, capabilities.HyperBoostAccess);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, capabilities.HyperBoostWithoutCoolingPadAccess);
        Assert.NotNull(capabilities.HyperBoostBaseModes);
        Assert.Equal(HyperBoostEntryRestriction.VendorSystemSku, capabilities.HyperBoostEntryRestriction);
        Assert.False(HyperBoostCapabilityPolicy.IsWritable(capabilities));
        Assert.True(HyperBoostCapabilityPolicy.CanExit(capabilities));
        CapabilityUnavailableException failure = await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetHyperBoostAsync(true, CancellationToken.None));
        Assert.Equal(HyperBoostSystemSkuPolicy.EntryRestrictedMessage, failure.Message);
        Assert.Empty(session.Commands);
        Assert.True(policy.WritesAllowed);
        Assert.Contains(policy.Snapshot().Observations, item => item.Operation == "HyperBoost"
            && item.Stage == "Guard" && item.ConfirmedValue == $"SkuCharacter9={character}"
            && item.FailureCategory == nameof(HyperBoostSystemSkuAdmission.DisabledByVendorSkuPolicy));
        Assert.NotEmpty(capabilities.PerformancePresets);
    }

    // Synapse's charAt(9) of a missing or short SKU is "", so HyperBoost stays offered.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("RZ09-0510")]
    public async Task HyperBoostSkuRecheck_UnreadableSkuAdmitsEntryLikeSynapse(string? sku)
    {
        foreach (int pid in new[] { 0x02B7, 0x029F })
        {
            var session = new Session { Mode = 0 };
            var policy = new ExperimentalFeaturesPolicy(Model(pid));
            await using var port = HyperBoostPort(session, new Pad(), policy, pid: pid, sku: sku);
            Assert.Equal(DeviceFeatureAccess.ReadWrite, port.Capabilities.HyperBoostAccess);
            Assert.Null(port.Capabilities.HyperBoostEntryRestriction);
            Assert.Equal("SkuCharacter9=Unavailable", port.HyperBoostSkuDiagnostic);
            await port.SetHyperBoostAsync(true, PerformancePreset.Balanced, true, CancellationToken.None);
            Assert.Equal(7, session.Mode);
            Assert.DoesNotContain(policy.Snapshot().Observations, item => item.Stage == "Guard");
        }
    }

    // A running HyperBoost session can always be turned off, whatever the SKU reads.
    [Theory]
    [InlineData(0x02B7, "RZ09-0510SEH3", PowerSource.PluggedIn)]
    [InlineData(0x02B7, "RZ09-0510SEH3", PowerSource.Battery)]
    [InlineData(0x02B7, null, PowerSource.PluggedIn)]
    [InlineData(0x02B7, null, PowerSource.Battery)]
    [InlineData(0x02B7, Rz090510Sku, PowerSource.PluggedIn)]
    [InlineData(0x02B7, Rz090510Sku, PowerSource.Battery)]
    [InlineData(0x029F, "RZ09-0483SEH3", PowerSource.PluggedIn)]
    [InlineData(0x029F, "RZ09-0483SEH3", PowerSource.Battery)]
    [InlineData(0x029F, null, PowerSource.PluggedIn)]
    [InlineData(0x029F, null, PowerSource.Battery)]
    [InlineData(0x029D, null, PowerSource.Battery)]
    public async Task HyperBoostSkuRecheck_ExitSucceedsWhateverTheSku(int pid, string? sku, PowerSource source)
    {
        var session = new Session
        {
            Mode = 7,
            AdapterPowerPayload = source == PowerSource.Battery ? [0, 17] : [0x11, 0x11],
        };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = HyperBoostPort(session, new Pad { State = CoolingPadConnectionState.Absent }, policy,
            power: () => source, pid: pid, sku: sku);
        Assert.True((await port.QueryPerformanceFanStateAsync(CancellationToken.None)).HyperBoostActive);
        session.Commands.Clear();

        await port.SetHyperBoostAsync(false, PerformancePreset.Balanced, false, CancellationToken.None);

        byte expected = source == PowerSource.Battery ? (byte)6 : (byte)0;
        Assert.Equal(new byte[][] { [1, 1, expected, 0], [1, 2, expected, 0] },
            session.Writes.Select(command => command.Arguments.ToArray()).ToArray());
        Assert.True(policy.WritesAllowed);
        Assert.DoesNotContain(policy.Snapshot().Observations, item => item.Stage == "Guard");
    }

    // Every battery exit hands off to Balanced (DC raw 6), whatever base the session owns.
    // 029F/029D show raw 6 on battery as Balanced; 02B7 as its DC Balanced preset.
    [Theory]
    [InlineData(0x02B7, PerformancePreset.Turbo)]
    [InlineData(0x02B7, PerformancePreset.Silent)]
    [InlineData(0x02B7, PerformancePreset.Custom)]
    [InlineData(0x02B7, PerformancePreset.Balanced)]
    [InlineData(0x029F, PerformancePreset.Performance)]
    [InlineData(0x029F, PerformancePreset.Silent)]
    [InlineData(0x029F, PerformancePreset.Custom)]
    [InlineData(0x029F, PerformancePreset.Balanced)]
    [InlineData(0x029D, PerformancePreset.Silent)]
    [InlineData(0x029D, PerformancePreset.Custom)]
    [InlineData(0x029D, PerformancePreset.Balanced)]
    public async Task HyperBoostBatteryExit_HandsOffToBalancedFromEveryBase(int pid, PerformancePreset baseMode)
    {
        var session = new Session { Mode = 7, Cpu = 1, Gpu = 2, AdapterPowerPayload = [0, 17] };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = HyperBoostPort(session, new Pad { State = CoolingPadConnectionState.Absent }, policy,
            power: () => PowerSource.Battery, pid: pid, sku: pid == 0x02B7 ? Rz090510Sku : null);

        await port.SetHyperBoostAsync(false, baseMode, false, CancellationToken.None);

        Assert.Equal(new byte[][] { [1, 1, 6, 0], [1, 2, 6, 0] },
            session.Writes.Select(command => command.Arguments.ToArray()).ToArray());
        ProductionPerformanceFanState state = await port.QueryPerformanceFanStateAsync(CancellationToken.None);
        Assert.False(state.HyperBoostActive);
        Assert.Equal(FanMode.Automatic, state.Fans);
        Assert.Equal(pid == 0x02B7 ? PerformancePreset.BatteryBalanced : PerformancePreset.Balanced, state.Performance);
        // The saved Custom levels are not touched.
        Assert.Equal(1, session.Cpu);
        Assert.Equal(2, session.Gpu);
        Assert.True(policy.WritesAllowed);
        Assert.Contains(policy.Snapshot().Observations, item => item.Operation == "HyperBoostRestore"
            && item.Stage == "Readback" && item.RequestedValue == $"{baseMode}/BatteryHandoff=Balanced");
    }

    // Synapse can leave mode 7 with manual fans at 3400-5200 RPM on products 671, 669
    // and 695. Its setter writes [1, selector, mode, 0] from any state, so exit admits
    // that baseline, reads no mode-7 fan targets on any of them, and confirms
    // Automatic fans. 5100 and 5200 RPM are outside the ordinary 029F/029D range.
    [Theory]
    [InlineData(0x02B7, PerformancePreset.Turbo, 1, 52)]
    [InlineData(0x02B7, PerformancePreset.Balanced, 0, 34)]
    [InlineData(0x029F, PerformancePreset.Performance, 2, 41)]
    [InlineData(0x029F, PerformancePreset.Performance, 2, 51)]
    [InlineData(0x029F, PerformancePreset.Balanced, 0, 52)]
    [InlineData(0x029D, PerformancePreset.Balanced, 0, 51)]
    [InlineData(0x029D, PerformancePreset.Silent, 5, 52)]
    public async Task HyperBoostExit_FromManualFansConfirmsAutomatic(int pid, PerformancePreset baseMode, byte raw, byte target)
    {
        var session = new Session { Mode = 7, Fan = 1, FirstTarget = target, SecondTarget = target };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = HyperBoostPort(session, new Pad { State = CoolingPadConnectionState.Absent }, policy,
            power: () => PowerSource.PluggedIn, pid: pid, sku: pid == 0x02B7 ? Rz090510Sku : null);

        await port.SetHyperBoostAsync(false, baseMode, false, CancellationToken.None);

        Assert.Equal(new byte[][] { [1, 1, raw, 0], [1, 2, raw, 0] },
            session.Writes.Select(command => command.Arguments.ToArray()).ToArray());
        Assert.Equal(0, session.Fan);
        Assert.Contains(policy.Snapshot().Observations, item => item.Operation == "HyperBoostRestore"
            && item.Stage == "Baseline" && item.ConfirmedValue == "/Fixed;HyperBoost=True");
        // The baseline read never asks for mode-7 fan targets, which have no admitted range.
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 0x0D && command.CommandId == 0x81);
        Assert.Contains(policy.Snapshot().Observations, item => item.Operation == "FanTargets"
            && item.Outcome == "NotQueried" && item.FailureCategory == "HyperBoostHasNoAdmittedFanRange");
    }

    // The exit readback must show Automatic fans and, on AC, the owned base itself.
    // On battery only the Balanced handoff confirms, never the owned AC base.
    [Theory]
    [InlineData(0x02B7, PerformancePreset.Turbo, false, 1, 1)]
    [InlineData(0x029F, PerformancePreset.Performance, false, 2, 1)]
    [InlineData(0x02B7, PerformancePreset.Turbo, false, 0, 0)]
    [InlineData(0x029F, PerformancePreset.Performance, false, 0, 0)]
    [InlineData(0x029D, PerformancePreset.Silent, false, 0, 0)]
    [InlineData(0x029F, PerformancePreset.Performance, true, 6, 1)]
    [InlineData(0x02B7, PerformancePreset.Turbo, true, 1, 0)]
    [InlineData(0x029F, PerformancePreset.Performance, true, 2, 0)]
    public async Task HyperBoostExit_RejectsAnyOtherReadback(
        int pid, PerformancePreset baseMode, bool battery, byte readbackMode, byte readbackFan)
    {
        var session = new Session { Mode = 7, AdapterPowerPayload = battery ? [0, 17] : [0x11, 0x11] };
        session.BeforeSubmission = command =>
        {
            if (command is { ClassId: 0x0D, CommandId: 0x82 } && session.Writes.Count() == 2)
            {
                session.Mode = readbackMode;
                session.Fan = readbackFan;
            }
        };
        await using var port = HyperBoostPort(session, new Pad { State = CoolingPadConnectionState.Absent },
            power: () => battery ? PowerSource.Battery : PowerSource.PluggedIn,
            pid: pid, sku: pid == 0x02B7 ? Rz090510Sku : null);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(false, baseMode, false, CancellationToken.None));

        Assert.Equal(2, session.Writes.Count());
        Assert.NotEqual(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
    }

    // Product 695 does not clear Maximum Fans before HyperBoost (its device definition
    // omits hasMaxFanSpeedModeEnabled). OpenBlade keeps its guarded cleanup on 02B7.
    [Fact]
    public async Task Rz090510_EntryFromCustomClearsMaximumFansFirst()
    {
        var session = new Session { Mode = 4, Cpu = 1, Gpu = 2, PowerModeControlFlags = 2, HyperBoostFirmware = [1, 3, 0, 0] };
        await using var port = HyperBoostPort(session, new Pad(), power: () => PowerSource.PluggedIn,
            pid: 0x02B7, sku: Rz090510Sku);
        await port.SetHyperBoostAsync(true, PerformancePreset.Custom, true, CancellationToken.None);
        Assert.Equal(0, session.PowerModeControlFlags);
        Assert.Equal(new byte[] { 0x0F, 2, 2 }, session.Writes.Select(command => command.CommandId).ToArray());
        Assert.Equal(new byte[] { 0 }, session.Writes.First().Arguments.ToArray());
        Assert.Equal(7, session.Mode);
    }

    [Theory]
    [InlineData('T')]
    [InlineData('2')]
    [InlineData('3')]
    [InlineData('r')]
    [InlineData('s')]
    [InlineData('4')]
    [InlineData('7')]
    public void HyperBoostSkuRecheck_OtherCharactersAdmit(char character)
    {
        string sku = $"RZ09-0510{character}EH3";
        foreach (ushort pid in new ushort[] { 0x02B7, 0x029F })
        {
            Assert.Equal(HyperBoostSystemSkuAdmission.Admitted, HyperBoostSystemSkuPolicy.Evaluate(pid, sku));
            Assert.Equal($"SkuCharacter9={character}",
                HyperBoostSystemSkuPolicy.DescribeForDiagnostics(HyperBoostSystemSkuAdmission.Admitted, sku));
        }
    }

    [Fact]
    public void HyperBoostSkuRecheck_ReadsOnlyTheTenthCharacter()
    {
        Assert.Equal(HyperBoostSystemSkuAdmission.DisabledByVendorSkuPolicy,
            HyperBoostSystemSkuPolicy.Evaluate(0x02B7, "RZ09-0510R"));
        Assert.Equal(HyperBoostSystemSkuAdmission.Admitted,
            HyperBoostSystemSkuPolicy.Evaluate(0x02B7, "RZ09-0510TRS56"));
        Assert.Equal(HyperBoostSystemSkuAdmission.SkuUnavailable,
            HyperBoostSystemSkuPolicy.Evaluate(0x02B7, "RZ09-051"));
        // Only a positively read excluded character declines entry.
        Assert.False(HyperBoostSystemSkuPolicy.AllowsEntry(HyperBoostSystemSkuAdmission.DisabledByVendorSkuPolicy));
        Assert.True(HyperBoostSystemSkuPolicy.AllowsEntry(HyperBoostSystemSkuAdmission.SkuUnavailable));
        Assert.True(HyperBoostSystemSkuPolicy.AllowsEntry(HyperBoostSystemSkuAdmission.Admitted));
        Assert.True(HyperBoostSystemSkuPolicy.AllowsEntry(HyperBoostSystemSkuAdmission.NotApplicable));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("RZ09-04825EH3")]
    [InlineData("RZ09-0482R")]
    public async Task HyperBoostSkuRecheck_Rz090482StaysUngated(string? sku)
    {
        // Product 669 main never sets needHyperboostRecheck, so its 5/6 predicate never runs.
        Assert.Equal(HyperBoostSystemSkuAdmission.NotApplicable, HyperBoostSystemSkuPolicy.Evaluate(0x029D, sku));
        Assert.False(HyperBoostSystemSkuPolicy.AppliesTo(0x02A0));
        var session = new Session { Mode = 0, HyperBoostFirmware = [1, 1, 1, 3] };
        await using var port = HyperBoostPort(session, new Pad(), pid: 0x029D, sku: sku);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, port.Capabilities.HyperBoostAccess);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, port.Capabilities.HyperBoostWithoutCoolingPadAccess);
        Assert.Null(port.Capabilities.HyperBoostBaseModes!.Turbo);
        await port.SetHyperBoostAsync(true, PerformancePreset.Balanced, true, CancellationToken.None);
        Assert.Equal(7, session.Mode);
    }

    [Fact]
    public void HyperBoostCapabilities_ExistingModelsKeepTheirBases()
    {
        DeviceCapabilities blade16 = ExperimentalBladePort.CreateCapabilities(Model(0x029F), true,
            HyperBoostSystemSkuAdmission.Admitted);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, blade16.HyperBoostBaseModes!.Performance);
        Assert.Null(blade16.HyperBoostBaseModes.Turbo);
        Assert.Null(blade16.HyperBoostBaseModes.CoolingPadEntryBaseMode);
        DeviceCapabilities blade14 = ExperimentalBladePort.CreateCapabilities(Model(0x029D), true,
            HyperBoostSystemSkuAdmission.NotApplicable);
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, blade14.HyperBoostBaseModes!.Performance);
        Assert.Equal(PerformancePreset.Balanced, blade14.HyperBoostBaseModes.CoolingPadEntryBaseMode);
        Assert.Equal(new Version(1, 5, 0, 0), ExperimentalBladePort.HyperBoostMinimumMcuFirmware(Model(0x029F)));
        Assert.Equal(new Version(1, 1, 1, 3), ExperimentalBladePort.HyperBoostMinimumMcuFirmware(Model(0x029D)));
        // Potential capabilities, used for profile validation, describe the model before a SKU is read.
        Assert.Equal(DeviceFeatureAccess.ReadWrite,
            ExperimentalBladePort.CreateCapabilities(Model(0x02B7), true).HyperBoostBaseModes!.Turbo);
    }
}
