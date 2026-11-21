using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalThermalExpansionTests
{
    // RZ09-0510 SKU letters from the EC/MCU updater: 0510T, 05102 and 05103 run HyperBoost.
    private const string Rz090510Sku = "RZ09-0510TEH3";

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task Rz090510_DecodesModeSevenAsHyperBoostEvenWithoutWriteAdmission(byte fan, bool admitted)
    {
        var session = new Session { Mode = 7, Fan = fan };
        await using var port = HyperBoostPort(session, new Pad(), pid: 0x02B7, sku: admitted ? Rz090510Sku : null);
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
    public async Task HyperBoostSkuRecheck_VendorDisabledCharacterKeepsHyperBoostObservedOnly(int pid, char character)
    {
        string sku = $"RZ09-0510{character}EH3";
        var session = new Session { Mode = pid == 0x02B7 ? (byte)1 : (byte)2 };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = HyperBoostPort(session, new Pad(), policy, pid: pid, sku: sku);
        AssertHyperBoostObservedOnly(port.Capabilities);
        CapabilityUnavailableException failure = await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetHyperBoostAsync(true, CancellationToken.None));
        Assert.Equal(HyperBoostSystemSkuPolicy.DisabledMessage, failure.Message);
        Assert.Empty(session.Commands);
        Assert.True(policy.WritesAllowed);
        Assert.Contains(policy.Snapshot().Observations, item => item.Operation == "HyperBoost"
            && item.Stage == "Guard" && item.ConfirmedValue == $"SkuCharacter9={character}"
            && item.FailureCategory == nameof(HyperBoostSystemSkuAdmission.DisabledByVendorSkuPolicy));
        // Other controls keep their ordinary write admission.
        Assert.NotEmpty(port.Capabilities.PerformancePresets);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("RZ09-0510")]
    public async Task HyperBoostSkuRecheck_UnreadableSkuFailsClosedForHyperBoostOnly(string? sku)
    {
        foreach (int pid in new[] { 0x02B7, 0x029F })
        {
            var session = new Session { Mode = 0 };
            var policy = new ExperimentalFeaturesPolicy(Model(pid));
            await using var port = HyperBoostPort(session, new Pad(), policy, pid: pid, sku: sku);
            AssertHyperBoostObservedOnly(port.Capabilities);
            CapabilityUnavailableException failure = await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
                port.SetHyperBoostAsync(true, PerformancePreset.Balanced, true, CancellationToken.None));
            Assert.Equal(HyperBoostSystemSkuPolicy.UnavailableMessage, failure.Message);
            Assert.Empty(session.Commands);
            Assert.Contains(policy.Snapshot().Observations, item => item.Stage == "Guard"
                && item.ConfirmedValue == "SkuCharacter9=Unavailable"
                && item.FailureCategory == nameof(HyperBoostSystemSkuAdmission.SkuUnavailable));
            Assert.Contains(PerformancePreset.Balanced, port.Capabilities.PerformancePresets);
        }
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

    private static void AssertHyperBoostObservedOnly(DeviceCapabilities capabilities)
    {
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, capabilities.HyperBoostAccess);
        Assert.Null(capabilities.HyperBoostWithoutCoolingPadAccess);
        Assert.Null(capabilities.HyperBoostBaseModes);
    }
}
