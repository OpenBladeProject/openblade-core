using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProductionBatteryPortTests
{
    [Fact]
    public async Task AdapterPowerQuery_WithoutCodeControlledEvidenceReturnsUnknownWithoutIo()
    {
        var session = new ScriptedSession(Success([0x07, 0x11]));
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        Assert.Equal(
            AdapterPowerState.Unknown,
            await port.QueryAdapterPowerStateAsync(default));
        Assert.Empty(session.Commands);
    }

    [Theory]
    [InlineData("0011", AdapterPowerState.Battery)]
    [InlineData("0211", AdapterPowerState.UsbCPower45W)]
    [InlineData("0311", AdapterPowerState.UsbCPower60W)]
    [InlineData("0411", AdapterPowerState.UsbCPower65W)]
    [InlineData("0511", AdapterPowerState.UsbCPower80W)]
    [InlineData("0611", AdapterPowerState.UsbCPower90W)]
    [InlineData("0711", AdapterPowerState.UsbCPower100W)]
    [InlineData("1111", AdapterPowerState.FullPower280W)]
    [InlineData("1211", AdapterPowerState.FullPower330W)]
    [InlineData("FFFF", AdapterPowerState.Unknown)]
    public async Task AdapterPowerQuery_IsSerializedAndReadOnly(
        string responseHex,
        AdapterPowerState expected)
    {
        var session = new ScriptedSession(
            Success(Convert.FromHexString(responseHex)));
        await using var port = CreatePort(
            session,
            DeviceCommandCatalog.Empty,
            AcceptedAdapterPowerReadEvidence());

        Assert.Equal(expected, await port.QueryAdapterPowerStateAsync(default));

        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal((0x07, 0x8C), (command.ClassId, command.CommandId));
        Assert.Equal([0x00, 0x00], command.Arguments.ToArray());
        Assert.Equal(2, command.ExpectedPayloadLength);
    }

    [Theory]
    [InlineData("0011", AdapterPowerState.Unknown)]
    [InlineData("0211", AdapterPowerState.Unknown)]
    [InlineData("0311", AdapterPowerState.Unknown)]
    [InlineData("0411", AdapterPowerState.Unknown)]
    [InlineData("0511", AdapterPowerState.Unknown)]
    [InlineData("0611", AdapterPowerState.Unknown)]
    [InlineData("0711", AdapterPowerState.Unknown)]
    [InlineData("1111", AdapterPowerState.FullPower280W)]
    [InlineData("1211", AdapterPowerState.Unknown)]
    [InlineData("FFFF", AdapterPowerState.Unknown)]
    public async Task AdapterPowerQuery_FiltersValuesOutsideDeviceAdmission(
        string responseHex,
        AdapterPowerState expected)
    {
        var session = new ScriptedSession(
            Success(Convert.FromHexString(responseHex)));
        await using var port = CreatePort(
            session,
            DeviceCommandCatalog.Empty,
            AcceptedAdapterPowerReadEvidence(),
            admittedAdapterPowerStates:
                new HashSet<AdapterPowerState> { AdapterPowerState.FullPower280W });

        Assert.Equal(expected, await port.QueryAdapterPowerStateAsync(default));
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task McuVersionQuery_WithoutCodeControlledEvidenceReturnsNullWithoutIo()
    {
        var session = new ScriptedSession(Success([0x01, 0x05, 0x00, 0x00]));
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        Assert.Null(await port.QueryMcuVersionAsync(default));
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task McuVersionQuery_IsSerializedAndReadOnly()
    {
        var session = new ScriptedSession(Success([0x01, 0x05, 0x00, 0x00]));
        await using var port = CreatePort(
            session,
            DeviceCommandCatalog.Empty,
            AcceptedMcuVersionReadEvidence());

        Assert.True(port.SupportsMcuVersionQuery);
        Assert.Equal("1.5.0.0", await port.QueryMcuVersionAsync(default));

        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal((0x00, 0x87), (command.ClassId, command.CommandId));
        Assert.Equal([0x00, 0x00, 0x00, 0x00], command.Arguments.ToArray());
        Assert.Equal(4, command.ExpectedPayloadLength);
    }

    [Fact]
    public async Task Rz090581CapabilityDiagnostics_WithoutEvidenceReturnsNullWithoutIo()
    {
        var session = new ScriptedSession(Success([0x01]));
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        Assert.Null(await port.QueryRz090581CapabilityDiagnosticsAsync(default));
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Rz090581CapabilityDiagnostics_QueryIsSerializedAndReadOnly()
    {
        byte[] fans = new byte[80];
        fans[0] = 2;
        fans[1] = 1;
        fans[2] = 2;
        var session = new ScriptedSession(
            Success([0x01]),
            Success([0x00]),
            Success(fans));
        await using var port = CreatePort(
            session,
            DeviceCommandCatalog.Empty,
            AcceptedRz090581CapabilityDiagnosticsReadEvidence());

        Assert.True(port.SupportsRz090581CapabilityDiagnosticsQuery);
        Rz090581CapabilityDiagnostics? diagnostics =
            await port.QueryRz090581CapabilityDiagnosticsAsync(default);

        Assert.NotNull(diagnostics);
        Assert.Equal(0x01, diagnostics.SkuHardwareConfigurationRaw);
        Assert.Equal(0x00, diagnostics.MiniLedPanelResolutionRaw);
        Assert.Equal([0x01, 0x02], diagnostics.ThermalFanIds);
        Assert.Collection(
            session.Commands,
            command => AssertQuery(command, 0x8F, [0x00], 1),
            command => AssertQuery(command, 0x8E, [0x00], 1),
            command => AssertQuery(command, 0x80, new byte[80], 80));
    }

    [Fact]
    public async Task StartupAnimationQuery_WithoutCodeControlledEvidenceReturnsNullWithoutIo()
    {
        var session = new ScriptedSession(Success([0x00]));
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        Assert.Null(await port.QueryStartupAnimationAsync(default));
        Assert.Empty(session.Commands);
    }

    [Theory]
    [InlineData("00", true)]
    [InlineData("01", false)]
    public async Task StartupAnimationQuery_IsSerializedAndReadOnly(
        string responseHex,
        bool expected)
    {
        var session = new ScriptedSession(
            Success(Convert.FromHexString(responseHex)));
        await using var port = CreatePort(
            session,
            DeviceCommandCatalog.Empty,
            AcceptedStartupAnimationReadEvidence());

        Assert.True(port.SupportsStartupAnimationQuery);
        Assert.Equal(expected, await port.QueryStartupAnimationAsync(default));

        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal((0x0F, 0x98), (command.ClassId, command.CommandId));
        Assert.Equal([0x00], command.Arguments.ToArray());
        Assert.Equal(1, command.ExpectedPayloadLength);
    }

    [Fact]
    public async Task FunctionKeyPrimaryQuery_WithoutCodeControlledEvidenceReturnsNullWithoutIo()
    {
        var session = new ScriptedSession(Success([0x01, 0x00]));
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        Assert.Null(await port.QueryFunctionKeyPrimaryAsync(default));
        Assert.Empty(session.Commands);
    }

    [Theory]
    [InlineData("0100", FunctionKeyPrimaryMode.FunctionKeys)]
    [InlineData("0101", FunctionKeyPrimaryMode.MultimediaKeys)]
    public async Task FunctionKeyPrimaryQuery_IsSerializedAndReadOnly(
        string responseHex,
        FunctionKeyPrimaryMode expected)
    {
        var session = new ScriptedSession(
            Success(Convert.FromHexString(responseHex)));
        await using var port = CreatePort(
            session,
            DeviceCommandCatalog.Empty,
            AcceptedFunctionKeyPrimaryReadEvidence());

        Assert.True(port.SupportsFunctionKeyPrimaryQuery);
        Assert.Equal(expected, await port.QueryFunctionKeyPrimaryAsync(default));

        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal((0x02, 0x86), (command.ClassId, command.CommandId));
        Assert.Equal([0x01, 0x00], command.Arguments.ToArray());
        Assert.Equal(2, command.ExpectedPayloadLength);
    }

    [Fact]
    public async Task GamingModeQuery_WithoutCodeControlledEvidenceReturnsNullWithoutIo()
    {
        var session = new ScriptedSession(Success([0x00, 0x00, 0x00, 0x00]));
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        Assert.Null(await port.QueryGamingModeAsync(default));
        Assert.Empty(session.Commands);
    }

    [Theory]
    [InlineData("00000000", false)]
    [InlineData("01000000", true)]
    public async Task GamingModeQuery_IsSerializedAndReadOnly(
        string responseHex,
        bool expected)
    {
        var session = new ScriptedSession(
            Success(Convert.FromHexString(responseHex)));
        await using var port = CreatePort(
            session,
            DeviceCommandCatalog.Empty,
            AcceptedGamingModeReadEvidence());

        Assert.True(port.SupportsGamingModeQuery);
        Assert.Equal(expected, await port.QueryGamingModeAsync(default));

        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal((0x00, 0x88), (command.ClassId, command.CommandId));
        Assert.Equal([0x00, 0x00, 0x00, 0x00], command.Arguments.ToArray());
        Assert.Equal(4, command.ExpectedPayloadLength);
    }

    [Fact]
    public async Task EmptyCatalog_RemainsProtectionReadableAndRejectsEverySetter()
    {
        var session = new ScriptedSession(
            Success([BatteryValidationProtocol.Encode(BatteryValidationSetting.Limit80)]));
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        Assert.Empty(port.Capabilities.ChargeLimits);
        Assert.False(port.Capabilities.SupportsBatteryProtectionOff);
        Assert.False(port.Capabilities.SupportsTemporaryFullCharge);
        Assert.Equal(new ConfirmedBatteryState(true, 80, null), await port.QueryProtectionAsync(default));
        Assert.Null(await port.QueryTemporaryFullChargeAsync(default));

        CapabilityUnavailableException limit =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => port.SetChargeLimitAsync(80, default));
        CapabilityUnavailableException protection =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => port.SetProtectionOffAsync(default));
        CapabilityUnavailableException temporary =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => port.SetTemporaryFullChargeAsync(true, default));
        CapabilityUnavailableException performance =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => port.SetPerformanceAsync(PerformancePreset.Balanced, default));
        CapabilityUnavailableException fans =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => port.SetFanModeAsync(FanMode.Automatic, default));

        Assert.All(
            [limit, protection, temporary, performance, fans],
            error => Assert.Contains("admitted", error.Message, StringComparison.OrdinalIgnoreCase));
        Assert.Single(session.Commands);
        Assert.Equal((0x07, 0x92), (session.Commands[0].ClassId, session.Commands[0].CommandId));
    }

    [Theory]
    [InlineData(
        PerformancePreset.Balanced,
        ProductionBatteryPort.PerformanceBalancedFirstAction,
        ProductionBatteryPort.PerformanceBalancedSecondAction,
        0x00)]
    [InlineData(
        PerformancePreset.Silent,
        ProductionBatteryPort.PerformanceSilentFirstAction,
        ProductionBatteryPort.PerformanceSilentSecondAction,
        0x05)]
    [InlineData(
        PerformancePreset.Performance,
        ProductionBatteryPort.PerformanceBoostFirstAction,
        ProductionBatteryPort.PerformanceBoostSecondAction,
        0x02)]
    [InlineData(
        PerformancePreset.BatterySaver,
        ProductionBatteryPort.PerformanceBatterySaverFirstAction,
        ProductionBatteryPort.PerformanceBatterySaverSecondAction,
        0x03)]
    [InlineData(
        PerformancePreset.BatteryBalanced,
        ProductionBatteryPort.PerformanceBatteryBalancedFirstAction,
        ProductionBatteryPort.PerformanceBatteryBalancedSecondAction,
        0x06)]
    public void CompletePerformancePairAloneAdmitsOnlyThatPreset(
        PerformancePreset preset,
        string firstAction,
        string secondAction,
        byte mode)
    {
        DeviceCapabilities capabilities = ProductionBatteryPort.CreateCapabilities(
            Catalog(
                Fixture(firstAction, 0x0D, 0x02, [0x01, 0x01, mode, 0x00]),
                Fixture(secondAction, 0x0D, 0x02, [0x01, 0x02, mode, 0x00])),
            NoTemporaryReadEvidence());

        Assert.True(capabilities.PerformancePresets.SetEquals([preset]));
        Assert.True(capabilities.FanModes.SetEquals(
            preset == PerformancePreset.Balanced ? [FanMode.Automatic] : []));
        Assert.Empty(capabilities.LightingEffects);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void MaximumCapabilityRequiresBalancedPairAndAllFourFixedActions(
        int missingActionIndex)
    {
        CapturedCommandFixture[] fixtures = MaximumCapabilityFixtures()
            .Where((_, index) => index != missingActionIndex)
            .ToArray();

        DeviceCapabilities capabilities = ProductionBatteryPort.CreateCapabilities(
            Catalog(fixtures),
            NoTemporaryReadEvidence());
        FanMode[] expectedFans = missingActionIndex < 2
            ? []
            : [FanMode.Automatic];

        Assert.DoesNotContain(FanMode.Maximum, capabilities.FanModes);
        Assert.Equal(expectedFans, capabilities.FanModes.Order().ToArray());
        Assert.Empty(capabilities.LightingEffects);
    }

    [Fact]
    public void CompleteMaximumCommandSetAdmitsCapturedAdjustableFanModes()
    {
        DeviceCapabilities capabilities = ProductionBatteryPort.CreateCapabilities(
            Catalog(MaximumCapabilityFixtures()),
            NoTemporaryReadEvidence());

        Assert.True(capabilities.PerformancePresets.SetEquals([PerformancePreset.Balanced]));
        Assert.True(capabilities.FanModes.SetEquals(
            [FanMode.Automatic, FanMode.Fixed, FanMode.ManualCurve, FanMode.Maximum]));
        Assert.Empty(capabilities.LightingEffects);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 210)]
    [InlineData(2, 65)]
    public void MaximumSequenceUsesCapturedInterCommandTiming(
        int completedCommandIndex,
        int expectedMilliseconds)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMilliseconds),
            ThermalWriteTiming.MaximumInterCommandDelay(completedCommandIndex));
    }

    [Theory]
    [InlineData(
        PerformancePreset.Balanced,
        ProductionBatteryPort.PerformanceBalancedFirstAction,
        ProductionBatteryPort.PerformanceBalancedSecondAction,
        0x00)]
    [InlineData(
        PerformancePreset.Silent,
        ProductionBatteryPort.PerformanceSilentFirstAction,
        ProductionBatteryPort.PerformanceSilentSecondAction,
        0x05)]
    [InlineData(
        PerformancePreset.Performance,
        ProductionBatteryPort.PerformanceBoostFirstAction,
        ProductionBatteryPort.PerformanceBoostSecondAction,
        0x02)]
    public async Task AdmittedPerformanceSetterExecutesExactPairedAction(
        PerformancePreset preset,
        string firstAction,
        string secondAction,
        byte mode)
    {
        byte[] first = [0x01, 0x01, mode, 0x00];
        byte[] second = [0x01, 0x02, mode, 0x00];
        byte baselineMode = preset == PerformancePreset.Balanced ? (byte)0x05 : (byte)0x00;
        var session = new ScriptedSession(
            Success([0x01, 0x01, baselineMode, 0x00]),
            Success([0x01, 0x02, baselineMode, 0x00]),
            Success(first),
            Success(second));
        await using var port = CreatePort(
            session,
            Catalog(
                Fixture(firstAction, 0x0D, 0x02, first),
                Fixture(secondAction, 0x0D, 0x02, second)));

        await port.SetPerformanceAsync(preset, default);

        Assert.Equal(
            [(0x0D, 0x82, "01010000"),
             (0x0D, 0x82, "01020000"),
             (0x0D, 0x02, Convert.ToHexString(first)),
             (0x0D, 0x02, Convert.ToHexString(second))],
            session.Commands.Select(command => (
                command.ClassId,
                command.CommandId,
                Convert.ToHexString(command.Arguments.Span))).ToArray());
    }

    [Fact]
    public async Task AdmittedMaximumFanSetterExecutesExactFourCommandAction()
    {
        byte[] state1 = [0x01, 0x01, 0x00, 0x01];
        byte[] state2 = [0x01, 0x02, 0x00, 0x01];
        byte[] speed1 = [0x01, 0x01, 0x36];
        byte[] speed2 = [0x01, 0x02, 0x36];
        byte[][] payloads = [state1, state2, speed1, speed2];
        var session = new ScriptedSession(
            [
                Success([0x01, 0x01, 0x00, 0x00]),
                Success([0x01, 0x02, 0x00, 0x00]),
                .. payloads.Select(Success),
            ]);
        await using var port = CreatePort(
            session,
            Catalog(
                Fixture(ProductionBatteryPort.FanMaximumStateFirstAction, 0x0D, 0x02, state1),
                Fixture(ProductionBatteryPort.FanMaximumStateSecondAction, 0x0D, 0x02, state2),
                Fixture(ProductionBatteryPort.FanMaximumSpeedFirstAction, 0x0D, 0x01, speed1),
                Fixture(ProductionBatteryPort.FanMaximumSpeedSecondAction, 0x0D, 0x01, speed2)));

        await port.SetFanModeAsync(FanMode.Maximum, default);

        Assert.Equal(
            [(0x0D, 0x82, "01010000"), (0x0D, 0x82, "01020000"),
             (0x0D, 0x02, "01010001"), (0x0D, 0x02, "01020001"),
             (0x0D, 0x01, "010136"), (0x0D, 0x01, "010236")],
            session.Commands.Select(command => (
                command.ClassId,
                command.CommandId,
                Convert.ToHexString(command.Arguments.Span))).ToArray());
    }

    [Fact]
    public async Task FixedFanSetterBuildsCapturedTargetPayloadForBothChannels()
    {
        byte[] state1 = [0x01, 0x01, 0x00, 0x01];
        byte[] state2 = [0x01, 0x02, 0x00, 0x01];
        byte[] template1 = [0x01, 0x01, 0x36];
        byte[] template2 = [0x01, 0x02, 0x36];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]),
            Success(state1),
            Success(state2),
            Success([0x01, 0x01, 0x25]),
            Success([0x01, 0x02, 0x25]));
        await using var port = CreatePort(
            session,
            Catalog(
                Fixture(ProductionBatteryPort.FanMaximumStateFirstAction, 0x0D, 0x02, state1),
                Fixture(ProductionBatteryPort.FanMaximumStateSecondAction, 0x0D, 0x02, state2),
                Fixture(ProductionBatteryPort.FanMaximumSpeedFirstAction, 0x0D, 0x01, template1),
                Fixture(ProductionBatteryPort.FanMaximumSpeedSecondAction, 0x0D, 0x01, template2)));

        await port.SetFanControlAsync(
            new FanControlSettings(FanMode.Fixed, 3700, ManualFanCurve.Default),
            default);

        Assert.Equal(
            ["01010000", "01020000", "01010001", "01020001", "010125", "010225"],
            session.Commands.Select(command =>
                Convert.ToHexString(command.Arguments.Span)).ToArray());
    }

    [Fact]
    public async Task ExactDeviceFixedFanSetterUsesCapturedRangeAndRejectsUnadmittedModes()
    {
        byte[] state1 = [0x01, 0x01, 0x00, 0x01];
        byte[] state2 = [0x01, 0x02, 0x00, 0x01];
        byte[] template1 = [0x01, 0x01, 0x32];
        byte[] template2 = [0x01, 0x02, 0x32];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]),
            Success(state1),
            Success(state2),
            Success([0x01, 0x01, 0x26]),
            Success([0x01, 0x02, 0x26]));
        await using var port = CreatePort(
            session,
            Catalog(
                Fixture(ProductionBatteryPort.FanMaximumStateFirstAction, 0x0D, 0x02, state1),
                Fixture(ProductionBatteryPort.FanMaximumStateSecondAction, 0x0D, 0x02, state2),
                Fixture(ProductionBatteryPort.FanMaximumSpeedFirstAction, 0x0D, 0x01, template1),
                Fixture(ProductionBatteryPort.FanMaximumSpeedSecondAction, 0x0D, 0x01, template2)),
            fanSpeedRange: new FanSpeedRange(2000, 5000, 100),
            admittedFanModes: new HashSet<FanMode>
            {
                FanMode.Automatic,
                FanMode.Fixed,
                FanMode.Maximum,
            });

        await port.SetFanControlAsync(
            new FanControlSettings(FanMode.Fixed, 3800, ManualFanCurve.Default),
            default);

        Assert.Equal(
            ["01010000", "01020000", "01010001", "01020001", "010126", "010226"],
            session.Commands.Select(command =>
                Convert.ToHexString(command.Arguments.Span)).ToArray());
        await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => port.SetFanControlAsync(
                new FanControlSettings(
                    FanMode.ManualCurve,
                    5000,
                    ManualFanCurve.Default),
                default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => port.SetFanTargetsAsync(5100, 5000, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => port.SetFanTargetsAsync(5400, 5000, default));
    }

    [Fact]
    public async Task CurveTargetUpdateRefusesToReclaimAutomaticFirmwareState()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]));
        await using var port = CreatePort(session, Catalog(MaximumCapabilityFixtures()));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanTargetsAsync(3000, 3200, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(2, session.Commands.Count);
    }

    [Fact]
    public async Task AutomaticFanSetterUsesExactBalancedAutomaticPair()
    {
        byte[] first = [0x01, 0x01, 0x00, 0x00];
        byte[] second = [0x01, 0x02, 0x00, 0x00];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x01]),
            Success([0x01, 0x02, 0x00, 0x01]),
            Success(first),
            Success(second));
        await using var port = CreatePort(
            session,
            Catalog(
                Fixture(ProductionBatteryPort.PerformanceBalancedFirstAction, 0x0D, 0x02, first),
                Fixture(ProductionBatteryPort.PerformanceBalancedSecondAction, 0x0D, 0x02, second)));

        await port.SetFanModeAsync(FanMode.Automatic, default);

        Assert.Equal(
            ["01010000", "01020000", "01010000", "01020000"],
            session.Commands.Select(command =>
                Convert.ToHexString(command.Arguments.Span)).ToArray());
    }

    [Fact]
    public async Task ThermalActionHoldsOperationGateAcrossWholeSequence()
    {
        byte[] first = [0x01, 0x01, 0x05, 0x00];
        byte[] second = [0x01, 0x02, 0x05, 0x00];
        var session = new BlockingFirstSession();
        await using var port = CreatePort(
            session,
            Catalog(
                Fixture(ProductionBatteryPort.PerformanceSilentFirstAction, 0x0D, 0x02, first),
                Fixture(ProductionBatteryPort.PerformanceSilentSecondAction, 0x0D, 0x02, second),
                Fixture(ProductionBatteryPort.Limit80Action, 0x07, 0x12, [0xD0])));

        Task thermal = port.SetPerformanceAsync(PerformancePreset.Silent, default);
        await session.FirstExecuteStarted.Task;
        Task battery = port.SetChargeLimitAsync(80, default);
        await Task.Yield();

        Assert.Equal(1, session.ExecuteCount);

        session.ReleaseFirst();
        await Task.WhenAll(thermal, battery);
        Assert.Equal(
            [(0x0D, 0x82), (0x0D, 0x82),
             (0x0D, 0x02), (0x0D, 0x02), (0x07, 0x12)],
            session.Commands.Select(command =>
                (command.ClassId, command.CommandId)).ToArray());
    }

    [Fact]
    public async Task MissingThermalActionRejectsWholePairBeforeSubmission()
    {
        var session = new ScriptedSession();
        await using var port = CreatePort(
            session,
            Catalog(Fixture(
                ProductionBatteryPort.PerformanceSilentFirstAction,
                0x0D,
                0x02,
                [0x01, 0x01, 0x05, 0x00])));

        CapabilityUnavailableException error =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => port.SetPerformanceAsync(PerformancePreset.Silent, default));

        Assert.Contains("not admitted", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task ThermalFailureAfterFirstSubmissionIsIndeterminateAndPoisonsPort()
    {
        byte[] first = [0x01, 0x01, 0x00, 0x00];
        byte[] second = [0x01, 0x02, 0x00, 0x00];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x05, 0x00]),
            Success([0x01, 0x02, 0x05, 0x00]),
            Success(first),
            new(default, HidSessionFailureKind.ConflictBlocked),
            Success([0xD0]));
        await using var port = CreatePort(
            session,
            Catalog(
                Fixture(ProductionBatteryPort.PerformanceBalancedFirstAction, 0x0D, 0x02, first),
                Fixture(ProductionBatteryPort.PerformanceBalancedSecondAction, 0x0D, 0x02, second),
                Fixture(ProductionBatteryPort.Limit80Action, 0x07, 0x12, [0xD0])));

        BladeWriteException failed = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(PerformancePreset.Balanced, default));
        BladeWriteException poisoned = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetChargeLimitAsync(80, default));

        Assert.Equal(WriteSubmissionState.Indeterminate, failed.SubmissionState);
        Assert.Equal(WriteSubmissionState.Indeterminate, poisoned.SubmissionState);
        Assert.Equal(4, session.Commands.Count);
    }

    [Fact]
    public async Task CancellationAfterFirstThermalSubmissionIsIndeterminateAndPoisonsPort()
    {
        byte[] first = [0x01, 0x01, 0x05, 0x00];
        byte[] second = [0x01, 0x02, 0x05, 0x00];
        using var cancellation = new CancellationTokenSource();
        var session = new CancelAfterFirstWriteSession(cancellation);
        await using var port = CreatePort(
            session,
            Catalog(
                Fixture(ProductionBatteryPort.PerformanceSilentFirstAction, 0x0D, 0x02, first),
                Fixture(ProductionBatteryPort.PerformanceSilentSecondAction, 0x0D, 0x02, second),
                Fixture(ProductionBatteryPort.Limit80Action, 0x07, 0x12, [0xD0])));

        BladeWriteException failed = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(PerformancePreset.Silent, cancellation.Token));
        BladeWriteException poisoned = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetChargeLimitAsync(80, default));

        Assert.Equal(WriteSubmissionState.Indeterminate, failed.SubmissionState);
        Assert.Equal(WriteSubmissionState.Indeterminate, poisoned.SubmissionState);
        Assert.Equal(3, session.ExecuteCount);
    }

    [Fact]
    public async Task PerformanceSetterRejectsMaximumBaselineBeforeAnyWrite()
    {
        byte[] first = [0x01, 0x01, 0x05, 0x00];
        byte[] second = [0x01, 0x02, 0x05, 0x00];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x01]),
            Success([0x01, 0x02, 0x00, 0x01]));
        await using var port = CreatePort(
            session,
            Catalog(
                Fixture(ProductionBatteryPort.PerformanceSilentFirstAction, 0x0D, 0x02, first),
                Fixture(ProductionBatteryPort.PerformanceSilentSecondAction, 0x0D, 0x02, second)));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(PerformancePreset.Silent, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(2, session.Commands.Count);
        Assert.All(session.Commands, command => Assert.Equal(0x82, command.CommandId));
    }

    [Fact]
    public async Task MaximumFanSetterRejectsNonBalancedAutomaticBaselineBeforeAnyWrite()
    {
        byte[] state1 = [0x01, 0x01, 0x00, 0x01];
        byte[] state2 = [0x01, 0x02, 0x00, 0x01];
        byte[] speed1 = [0x01, 0x01, 0x36];
        byte[] speed2 = [0x01, 0x02, 0x36];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x05, 0x00]),
            Success([0x01, 0x02, 0x05, 0x00]));
        await using var port = CreatePort(
            session,
            Catalog(
                Fixture(ProductionBatteryPort.FanMaximumStateFirstAction, 0x0D, 0x02, state1),
                Fixture(ProductionBatteryPort.FanMaximumStateSecondAction, 0x0D, 0x02, state2),
                Fixture(ProductionBatteryPort.FanMaximumSpeedFirstAction, 0x0D, 0x01, speed1),
                Fixture(ProductionBatteryPort.FanMaximumSpeedSecondAction, 0x0D, 0x01, speed2)));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanModeAsync(FanMode.Maximum, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(2, session.Commands.Count);
        Assert.All(session.Commands, command => Assert.Equal(0x82, command.CommandId));
    }

    [Theory]
    [InlineData(0x00, 0x00, PerformancePreset.Balanced, FanMode.Automatic)]
    [InlineData(0x05, 0x00, PerformancePreset.Silent, FanMode.Automatic)]
    [InlineData(0x02, 0x00, PerformancePreset.Performance, FanMode.Automatic)]
    [InlineData(0x03, 0x00, PerformancePreset.BatterySaver, FanMode.Automatic)]
    [InlineData(0x06, 0x00, PerformancePreset.BatteryBalanced, FanMode.Automatic)]
    public async Task ThermalPair_MapsRecognizedAutomaticStateWithoutAdmittingWrites(
        byte mode,
        byte fanValue,
        PerformancePreset expectedPerformance,
        FanMode expectedFans)
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, mode, fanValue]),
            Success([0x01, 0x02, mode, fanValue]));
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        ProductionPerformanceFanState state =
            await port.QueryPerformanceFanStateAsync(default);

        Assert.Equal(new(expectedPerformance, expectedFans), state);
        Assert.Empty(port.Capabilities.PerformancePresets);
        Assert.Empty(port.Capabilities.FanModes);
        Assert.Equal(
            [(0x0D, 0x82, "01010000"), (0x0D, 0x82, "01020000")],
            session.Commands.Select(command => (
                command.ClassId,
                command.CommandId,
                Convert.ToHexString(command.Arguments.Span))).ToArray());
    }

    [Fact]
    public async Task HyperBoostPair_MapsTypedActiveStateWithoutPoisoningPort()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]),
            Success([0xD0]));
        await using var port = CreatePort(
            session,
            Catalog(Fixture(
                ProductionBatteryPort.Limit80Action,
                0x07,
                0x12,
                [0xD0])));

        ProductionPerformanceFanState state =
            await port.QueryPerformanceFanStateAsync(default);
        await port.SetChargeLimitAsync(80, default);

        Assert.True(state.HyperBoostActive);
        Assert.Null(state.Performance);
        Assert.Equal(FanMode.Automatic, state.Fans);
        Assert.Equal(3, session.Commands.Count);
    }

    [Fact]
    public async Task SetHyperBoost_EnableRequiresPadFullPowerBalancedAndVerifiesReadback()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]),
            Success([0x11, 0x11]),
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]),
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]));
        var eligibility = new StubHyperBoostEligibility(padPresent: true);
        await using var port = CreateHyperBoostPort(session, eligibility);

        await port.SetHyperBoostAsync(enabled: true, default);

        Assert.Equal(1, eligibility.ReadCount);
        Assert.Equal(
            [(0x0D, 0x82), (0x0D, 0x82), (0x07, 0x8C),
             (0x0D, 0x02), (0x0D, 0x02), (0x0D, 0x82), (0x0D, 0x82)],
            session.Commands.Select(command => (command.ClassId, command.CommandId)).ToArray());
    }

    [Fact]
    public async Task SetHyperBoost_EnableFailsBeforeWriteWhenPadIsAbsent()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]));
        await using var port = CreateHyperBoostPort(
            session,
            new StubHyperBoostEligibility(padPresent: false));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetHyperBoostAsync(enabled: true, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(2, session.Commands.Count);
        Assert.All(session.Commands, command => Assert.Equal(0x82, command.CommandId));
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.Absent)]
    [InlineData(CoolingPadConnectionState.Ambiguous)]
    [InlineData(CoolingPadConnectionState.ObservationFailed)]
    public async Task SetHyperBoost_EnableRequiresFreshExactPadState(
        CoolingPadConnectionState padState)
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]));
        var eligibility = new StubHyperBoostEligibility(padState);
        await using var port = CreateHyperBoostPort(session, eligibility);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetHyperBoostAsync(enabled: true, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(1, eligibility.ReadCount);
        Assert.Equal(2, session.Commands.Count);
        Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x02);
    }

    [Fact]
    public async Task SetHyperBoost_EnableFailsBeforeWriteWithoutFullPowerAdapter()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]),
            Success([0x07, 0x11]));
        await using var port = CreateHyperBoostPort(
            session,
            new StubHyperBoostEligibility(padPresent: true));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetHyperBoostAsync(enabled: true, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(3, session.Commands.Count);
        Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x02);
    }

    [Fact]
    public async Task SetHyperBoost_EnableRejectsNonBalancedBaselineBeforeEligibility()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x02, 0x00]),
            Success([0x01, 0x02, 0x02, 0x00]));
        var eligibility = new StubHyperBoostEligibility(padPresent: true);
        await using var port = CreateHyperBoostPort(session, eligibility);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetHyperBoostAsync(enabled: true, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(0, eligibility.ReadCount);
        Assert.Equal(2, session.Commands.Count);
    }

    [Fact]
    public async Task SetHyperBoost_DisableOnlyRestoresBalancedAndVerifiesReadback()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]),
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]),
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]));
        var eligibility = new StubHyperBoostEligibility(padPresent: false);
        await using var port = CreateHyperBoostPort(session, eligibility);

        await port.SetHyperBoostAsync(enabled: false, default);

        Assert.Equal(0, eligibility.ReadCount);
        Assert.Equal(
            ["01010000", "01020000"],
            session.Commands
                .Where(command => command.CommandId == 0x02)
                .Select(command => Convert.ToHexString(command.Arguments.Span))
                .ToArray());
        Assert.Equal(6, session.Commands.Count);
    }

    [Fact]
    public async Task SetHyperBoost_DisableRejectsNonHyperBoostBaselineWithoutWrites()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x02, 0x00]),
            Success([0x01, 0x02, 0x02, 0x00]));
        await using var port = CreateHyperBoostPort(
            session,
            new StubHyperBoostEligibility(padPresent: false));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetHyperBoostAsync(enabled: false, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(2, session.Commands.Count);
        Assert.All(session.Commands, command => Assert.Equal(0x82, command.CommandId));
    }

    [Theory]
    [InlineData(
        PerformancePreset.Silent,
        ProductionBatteryPort.PerformanceSilentFirstAction,
        ProductionBatteryPort.PerformanceSilentSecondAction,
        (byte)0x05)]
    [InlineData(
        PerformancePreset.Performance,
        ProductionBatteryPort.PerformanceBoostFirstAction,
        ProductionBatteryPort.PerformanceBoostSecondAction,
        (byte)0x02)]
    public async Task SetHyperBoost_InjectedDirectBaseEnablesWithoutBalancedAndRestoresExactMode(
        PerformancePreset baseMode,
        string restoreFirstAction,
        string restoreSecondAction,
        byte baseCode)
    {
        byte[] baseFirst = [0x01, 0x01, baseCode, 0x00];
        byte[] baseSecond = [0x01, 0x02, baseCode, 0x00];
        var session = new ScriptedSession(
            Success(baseFirst),
            Success(baseSecond),
            Success([0x11, 0x11]),
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]),
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]),
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]),
            Success(baseFirst),
            Success(baseSecond),
            Success(baseFirst),
            Success(baseSecond));
        await using var port = CreateHyperBoostPort(
            session,
            new StubHyperBoostEligibility(padPresent: true),
            Catalog(
                Fixture(restoreFirstAction, 0x0D, 0x02, baseFirst),
                Fixture(restoreSecondAction, 0x0D, 0x02, baseSecond)),
            hyperBoostBaseModes: new HyperBoostBaseModeCapabilities(
                baseMode == PerformancePreset.Silent
                    ? DeviceFeatureAccess.ReadWrite
                    : DeviceFeatureAccess.ObservedOnly,
                DeviceFeatureAccess.ReadWrite,
                baseMode == PerformancePreset.Performance
                    ? DeviceFeatureAccess.ReadWrite
                    : DeviceFeatureAccess.ObservedOnly));

        await port.SetHyperBoostAsync(true, baseMode, default);
        await port.SetHyperBoostAsync(false, baseMode, default);

        string[] writes = session.Commands
            .Where(command => command.CommandId == 0x02)
            .Select(command => Convert.ToHexString(command.Arguments.Span))
            .ToArray();
        Assert.Equal(
            ["01010700", "01020700", Convert.ToHexString(baseFirst), Convert.ToHexString(baseSecond)],
            writes);
        Assert.DoesNotContain("01010000", writes);
        Assert.DoesNotContain("01020000", writes);
    }

    [Fact]
    public async Task SetPerformance_BalancedRestoresHyperBoostAndStopsAfterVerifiedBaseline()
    {
        byte[] balancedFirst = [0x01, 0x01, 0x00, 0x00];
        byte[] balancedSecond = [0x01, 0x02, 0x00, 0x00];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]),
            Success(balancedFirst),
            Success(balancedSecond),
            Success(balancedFirst),
            Success(balancedSecond));
        await using var port = CreateHyperBoostPort(
            session,
            new StubHyperBoostEligibility(padPresent: false),
            Catalog(
                Fixture(
                    ProductionBatteryPort.PerformanceBalancedFirstAction,
                    0x0D,
                    0x02,
                    balancedFirst),
                Fixture(
                    ProductionBatteryPort.PerformanceBalancedSecondAction,
                    0x0D,
                    0x02,
                    balancedSecond)));

        await port.SetPerformanceAsync(PerformancePreset.Balanced, default);

        Assert.Equal(
            [(0x82, "01010000"), (0x82, "01020000"),
             (0x02, "01010000"), (0x02, "01020000"),
             (0x82, "01010000"), (0x82, "01020000")],
            session.Commands.Select(command => (
                command.CommandId,
                Convert.ToHexString(command.Arguments.Span))).ToArray());
    }

    [Fact]
    public async Task SetPerformance_PerformanceRestoresHyperBoostBeforeApplyingAndVerifyingTarget()
    {
        byte[] balancedFirst = [0x01, 0x01, 0x00, 0x00];
        byte[] balancedSecond = [0x01, 0x02, 0x00, 0x00];
        byte[] targetFirst = [0x01, 0x01, 0x02, 0x00];
        byte[] targetSecond = [0x01, 0x02, 0x02, 0x00];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]),
            Success(balancedFirst),
            Success(balancedSecond),
            Success(balancedFirst),
            Success(balancedSecond),
            Success(targetFirst),
            Success(targetSecond),
            Success(targetFirst),
            Success(targetSecond));
        await using var port = CreateHyperBoostPort(
            session,
            new StubHyperBoostEligibility(padPresent: false),
            Catalog(
                Fixture(
                    ProductionBatteryPort.PerformanceBoostFirstAction,
                    0x0D,
                    0x02,
                    targetFirst),
                Fixture(
                    ProductionBatteryPort.PerformanceBoostSecondAction,
                    0x0D,
                    0x02,
                    targetSecond)));

        await port.SetPerformanceAsync(PerformancePreset.Performance, default);

        Assert.Equal(
            ["01010000", "01020000", "01010200", "01020200"],
            session.Commands
                .Where(command => command.CommandId == 0x02)
                .Select(command => Convert.ToHexString(command.Arguments.Span))
                .ToArray());
        Assert.Equal(10, session.Commands.Count);
    }

    [Fact]
    public async Task SetPerformance_HyperBoostRestoreMismatchStopsBeforeTargetAndPoisonsPort()
    {
        byte[] balancedFirst = [0x01, 0x01, 0x00, 0x00];
        byte[] balancedSecond = [0x01, 0x02, 0x00, 0x00];
        byte[] targetFirst = [0x01, 0x01, 0x02, 0x00];
        byte[] targetSecond = [0x01, 0x02, 0x02, 0x00];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]),
            Success(balancedFirst),
            Success(balancedSecond),
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]));
        await using var port = CreateHyperBoostPort(
            session,
            new StubHyperBoostEligibility(padPresent: false),
            Catalog(
                Fixture(
                    ProductionBatteryPort.PerformanceBoostFirstAction,
                    0x0D,
                    0x02,
                    targetFirst),
                Fixture(
                    ProductionBatteryPort.PerformanceBoostSecondAction,
                    0x0D,
                    0x02,
                    targetSecond)));

        BladeWriteException failed = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(PerformancePreset.Performance, default));
        BladeWriteException poisoned = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(PerformancePreset.Performance, default));

        Assert.Equal(WriteSubmissionState.Indeterminate, failed.SubmissionState);
        Assert.Equal(WriteSubmissionState.Indeterminate, poisoned.SubmissionState);
        Assert.Equal(["01010000", "01020000"], session.Commands
            .Where(command => command.CommandId == 0x02)
            .Select(command => Convert.ToHexString(command.Arguments.Span))
            .ToArray());
        Assert.Equal(6, session.Commands.Count);
    }

    [Fact]
    public async Task FanSettersRejectConfirmedHyperBoostWithoutWrites()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]),
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]));
        await using var port = CreateHyperBoostPort(
            session,
            new StubHyperBoostEligibility(padPresent: false),
            Catalog(MaximumCapabilityFixtures()));

        BladeWriteException automatic = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanModeAsync(FanMode.Automatic, default));
        BladeWriteException fixedMode = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanControlAsync(
                new FanControlSettings(FanMode.Fixed, 3700, ManualFanCurve.Default),
                default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, automatic.SubmissionState);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, fixedMode.SubmissionState);
        Assert.Equal(4, session.Commands.Count);
        Assert.All(session.Commands, command => Assert.Equal(0x82, command.CommandId));
    }

    [Fact]
    public async Task CustomPerformanceRestoresHyperBoostBeforeApplyingAndConfirmingTarget()
    {
        CustomPerformanceSettings target = new(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Medium);
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]),
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]),
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]),
            Success([0x01, 0x01, 0x04, 0x00]),
            Success([0x01, 0x02, 0x04, 0x00]),
            Success([0x01, 0x01, 0x02]),
            Success([0x01, 0x02, 0x01]),
            Success([0x01, 0x01, 0x04, 0x00]),
            Success([0x01, 0x02, 0x04, 0x00]),
            Success([0x01, 0x01, 0x02]),
            Success([0x01, 0x02, 0x01]));
        await using var port = CreateHyperBoostPort(
            session,
            new StubHyperBoostEligibility(padPresent: false),
            Catalog(CustomPerformanceFixtures()));

        await port.SetCustomPerformanceAsync(target, default);

        Assert.Equal(
            ["01010000", "01020000", "01010400", "01020400"],
            session.Commands
                .Where(command => command.CommandId == 0x02)
                .Select(command => Convert.ToHexString(command.Arguments.Span))
                .ToArray());
        Assert.Equal(14, session.Commands.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(DeviceFeatureAccess.ObservedOnly)]
    [InlineData(DeviceFeatureAccess.ReadOnly)]
    public async Task NonWritableHyperBoostAccessNeverUsesRestorationFixture(
        DeviceFeatureAccess? access)
    {
        byte[] targetFirst = [0x01, 0x01, 0x02, 0x00];
        byte[] targetSecond = [0x01, 0x02, 0x02, 0x00];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]));
        await using var port = new ProductionBatteryPort(
            session,
            Catalog(
                Fixture(
                    ProductionBatteryPort.PerformanceBoostFirstAction,
                    0x0D,
                    0x02,
                    targetFirst),
                Fixture(
                    ProductionBatteryPort.PerformanceBoostSecondAction,
                    0x0D,
                    0x02,
                    targetSecond)),
            NoTemporaryReadEvidence(),
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1),
            hyperBoostAccess: access,
            hyperBoostCatalog: HyperBoostCatalog());

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(PerformancePreset.Performance, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(2, session.Commands.Count);
        Assert.All(session.Commands, command => Assert.Equal(0x82, command.CommandId));
    }

    [Fact]
    public void WritableHyperBoostRequiresFixtureAndPadEligibilityInvariant()
    {
        Assert.Throws<UnvalidatedCommandException>(() => new ProductionBatteryPort(
            new ScriptedSession(),
            DeviceCommandCatalog.Empty,
            NoTemporaryReadEvidence(),
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1),
            hyperBoostAccess: DeviceFeatureAccess.ReadWrite));

        Assert.Throws<UnvalidatedCommandException>(() => new ProductionBatteryPort(
            new ScriptedSession(),
            DeviceCommandCatalog.Empty,
            NoTemporaryReadEvidence(),
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1),
            hyperBoostAccess: DeviceFeatureAccess.ReadWrite,
            hyperBoostCatalog: HyperBoostCatalog()));

        Assert.Throws<ArgumentException>(() => new ProductionBatteryPort(
            new ScriptedSession(),
            DeviceCommandCatalog.Empty,
            NoTemporaryReadEvidence(),
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1),
            hyperBoostAccess: DeviceFeatureAccess.ObservedOnly,
            hyperBoostBaseModes: new HyperBoostBaseModeCapabilities(
                DeviceFeatureAccess.ObservedOnly,
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ObservedOnly)));

        Assert.Throws<ArgumentException>(() => new ProductionBatteryPort(
            new ScriptedSession(),
            DeviceCommandCatalog.Empty,
            NoTemporaryReadEvidence(),
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1),
            hyperBoostAccess: DeviceFeatureAccess.ReadWrite,
            hyperBoostBaseModes: new HyperBoostBaseModeCapabilities(
                (DeviceFeatureAccess)999,
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ObservedOnly)));
    }

    [Theory]
    [InlineData(
        PerformancePreset.BatterySaver,
        ProductionBatteryPort.PerformanceBatterySaverFirstAction,
        ProductionBatteryPort.PerformanceBatterySaverSecondAction,
        0x03)]
    [InlineData(
        PerformancePreset.BatteryBalanced,
        ProductionBatteryPort.PerformanceBatteryBalancedFirstAction,
        ProductionBatteryPort.PerformanceBatteryBalancedSecondAction,
        0x06)]
    public async Task BatteryPerformanceSetter_ExactMatchPerformsNoWrite(
        PerformancePreset preset,
        string firstAction,
        string secondAction,
        byte mode)
    {
        byte[] first = [0x01, 0x01, mode, 0x00];
        byte[] second = [0x01, 0x02, mode, 0x00];
        var session = new ScriptedSession(Success(first), Success(second));
        await using var port = CreatePort(
            session,
            Catalog(
                Fixture(firstAction, 0x0D, 0x02, first),
                Fixture(secondAction, 0x0D, 0x02, second)));

        await port.SetPerformanceAsync(preset, default);

        Assert.Equal(2, session.Commands.Count);
        Assert.All(session.Commands, command => Assert.Equal(0x82, command.CommandId));
    }

    [Theory]
    [InlineData(
        PerformancePreset.BatterySaver,
        ProductionBatteryPort.PerformanceBatterySaverFirstAction,
        ProductionBatteryPort.PerformanceBatterySaverSecondAction,
        0x06,
        0x03)]
    [InlineData(
        PerformancePreset.BatteryBalanced,
        ProductionBatteryPort.PerformanceBatteryBalancedFirstAction,
        ProductionBatteryPort.PerformanceBatteryBalancedSecondAction,
        0x03,
        0x06)]
    public async Task BatteryPerformanceSetter_OppositeBatteryStateWritesDistinctPairAndConfirms(
        PerformancePreset preset,
        string firstAction,
        string secondAction,
        byte initialMode,
        byte targetMode)
    {
        byte[] targetFirst = [0x01, 0x01, targetMode, 0x00];
        byte[] targetSecond = [0x01, 0x02, targetMode, 0x00];
        var session = new ScriptedSession(
            Success([0x01, 0x01, initialMode, 0x00]),
            Success([0x01, 0x02, initialMode, 0x00]),
            Success(targetFirst),
            Success(targetSecond),
            Success(targetFirst),
            Success(targetSecond));
        await using var port = CreatePort(
            session,
            Catalog(
                Fixture(firstAction, 0x0D, 0x02, targetFirst),
                Fixture(secondAction, 0x0D, 0x02, targetSecond)));

        await port.SetPerformanceAsync(preset, default);

        Assert.Equal(
            [(0x82, "01010000"), (0x82, "01020000"),
             (0x02, Convert.ToHexString(targetFirst)),
             (0x02, Convert.ToHexString(targetSecond)),
             (0x82, "01010000"), (0x82, "01020000")],
            session.Commands.Select(command => (
                command.CommandId,
                Convert.ToHexString(command.Arguments.Span))).ToArray());
    }

    [Fact]
    public async Task BatteryPerformanceSetter_FinalStateMismatchFailsIndeterminate()
    {
        byte[] targetFirst = [0x01, 0x01, 0x06, 0x00];
        byte[] targetSecond = [0x01, 0x02, 0x06, 0x00];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x03, 0x00]),
            Success([0x01, 0x02, 0x03, 0x00]),
            Success(targetFirst),
            Success(targetSecond),
            Success([0x01, 0x01, 0x03, 0x00]),
            Success([0x01, 0x02, 0x03, 0x00]));
        await using var port = CreatePort(
            session,
            Catalog(
                Fixture(
                    ProductionBatteryPort.PerformanceBatteryBalancedFirstAction,
                    0x0D,
                    0x02,
                    targetFirst),
                Fixture(
                    ProductionBatteryPort.PerformanceBatteryBalancedSecondAction,
                    0x0D,
                    0x02,
                    targetSecond)));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(
                PerformancePreset.BatteryBalanced,
                default));

        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(6, session.Commands.Count);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x04)]
    public async Task FixedMaximumPair_PublishesOnlyFanState(byte mode)
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, mode, 0x01]),
            Success([0x01, 0x02, mode, 0x01]));
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        ProductionPerformanceFanState state =
            await port.QueryPerformanceFanStateAsync(default);

        Assert.Null(state.Performance);
        Assert.Equal(FanMode.Maximum, state.Fans);
        Assert.Empty(port.Capabilities.PerformancePresets);
        Assert.Empty(port.Capabilities.FanModes);
    }

    [Theory]
    [InlineData(
        PerformancePreset.Balanced,
        ProductionBatteryPort.PerformanceBalancedFirstAction,
        ProductionBatteryPort.PerformanceBalancedSecondAction,
        0x00,
        true)]
    [InlineData(
        PerformancePreset.Silent,
        ProductionBatteryPort.PerformanceSilentFirstAction,
        ProductionBatteryPort.PerformanceSilentSecondAction,
        0x05,
        false)]
    [InlineData(
        PerformancePreset.Performance,
        ProductionBatteryPort.PerformanceBoostFirstAction,
        ProductionBatteryPort.PerformanceBoostSecondAction,
        0x02,
        false)]
    [InlineData(
        PerformancePreset.BatterySaver,
        ProductionBatteryPort.PerformanceBatterySaverFirstAction,
        ProductionBatteryPort.PerformanceBatterySaverSecondAction,
        0x03,
        false)]
    [InlineData(
        PerformancePreset.BatteryBalanced,
        ProductionBatteryPort.PerformanceBatteryBalancedFirstAction,
        ProductionBatteryPort.PerformanceBatteryBalancedSecondAction,
        0x06,
        false)]
    public async Task CompleteThermalPair_ExposesOnlyItsValidatedCapability(
        PerformancePreset preset,
        string firstAction,
        string secondAction,
        byte mode,
        bool exposesAutomatic)
    {
        await using var port = CreatePort(
            new ScriptedSession(),
            Catalog(
                Fixture(firstAction, 0x0D, 0x02, [0x01, 0x01, mode, 0x00]),
                Fixture(secondAction, 0x0D, 0x02, [0x01, 0x02, mode, 0x00])));

        Assert.Equal([preset], port.Capabilities.PerformancePresets);
        Assert.Equal(
            exposesAutomatic ? [FanMode.Automatic] : [],
            port.Capabilities.FanModes);
        Assert.DoesNotContain(FanMode.Maximum, port.Capabilities.FanModes);
    }

    [Fact]
    public async Task IncompleteThermalPair_DoesNotExposePerformanceOrFanCapability()
    {
        await using var port = CreatePort(
            new ScriptedSession(),
            Catalog(Fixture(
                ProductionBatteryPort.PerformanceBalancedFirstAction,
                0x0D,
                0x02,
                [0x01, 0x01, 0x00, 0x00])));

        Assert.Empty(port.Capabilities.PerformancePresets);
        Assert.Empty(port.Capabilities.FanModes);
    }

    [Theory]
    [InlineData(
        PerformancePreset.BatterySaver,
        ProductionBatteryPort.PerformanceBatterySaverFirstAction,
        0x03)]
    [InlineData(
        PerformancePreset.BatteryBalanced,
        ProductionBatteryPort.PerformanceBatteryBalancedFirstAction,
        0x06)]
    public async Task IncompleteBatteryPerformancePair_IsNotAdmittedAndCannotWrite(
        PerformancePreset preset,
        string firstAction,
        byte mode)
    {
        var session = new ScriptedSession();
        await using var port = CreatePort(
            session,
            Catalog(Fixture(
                firstAction,
                0x0D,
                0x02,
                [0x01, 0x01, mode, 0x00])));

        CapabilityUnavailableException error =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => port.SetPerformanceAsync(preset, default));

        Assert.DoesNotContain(preset, port.Capabilities.PerformancePresets);
        Assert.Contains("not admitted", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task MaximumActionsAlone_NeverExposeMaximumCapability()
    {
        await using var port = CreatePort(
            new ScriptedSession(),
            Catalog(
                Fixture(ProductionBatteryPort.FanMaximumStateFirstAction, 0x0D, 0x02, [0x01, 0x01, 0x00, 0x01]),
                Fixture(ProductionBatteryPort.FanMaximumStateSecondAction, 0x0D, 0x02, [0x01, 0x02, 0x00, 0x01]),
                Fixture(ProductionBatteryPort.FanMaximumSpeedFirstAction, 0x0D, 0x01, [0x01, 0x01, 0x36]),
                Fixture(ProductionBatteryPort.FanMaximumSpeedSecondAction, 0x0D, 0x01, [0x01, 0x02, 0x36])));

        Assert.Empty(port.Capabilities.PerformancePresets);
        Assert.Empty(port.Capabilities.FanModes);
    }

    [Fact]
    public async Task ThermalChannelMismatch_FailsClosedAfterTwoReads()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x05, 0x00]));
        await using var port = CreatePort(
            session,
            Catalog(Fixture(
                ProductionBatteryPort.Limit80Action,
                0x07,
                0x12,
                [0xD0])));

        RazerProtocolException error = await Assert.ThrowsAsync<RazerProtocolException>(
            () => port.QueryPerformanceFanStateAsync(default));
        BladeWriteException blockedWrite = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetChargeLimitAsync(80, default));

        Assert.Equal(HidProtocolValidationFailure.PayloadValidator, error.ValidationFailure);
        Assert.Equal(WriteSubmissionState.Indeterminate, blockedWrite.SubmissionState);
        Assert.Equal(2, session.Commands.Count);
        Assert.All(session.Commands, command => Assert.Equal(0x82, command.CommandId));
    }

    [Fact]
    public async Task UnknownThermalPayload_FailsClosedWithoutSecondRead()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x08, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]));
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        RazerProtocolException error = await Assert.ThrowsAsync<RazerProtocolException>(
            () => port.QueryPerformanceFanStateAsync(default));

        Assert.Equal(HidProtocolValidationFailure.PayloadValidator, error.ValidationFailure);
        Assert.Single(session.Commands);
    }

    [Theory]
    [InlineData(50, "battery-limit-50", 0xB2)]
    [InlineData(55, "battery-limit-55", 0xB7)]
    [InlineData(60, "battery-limit-60", 0xBC)]
    [InlineData(65, "battery-limit-65", 0xC1)]
    [InlineData(70, "battery-limit-70", 0xC6)]
    [InlineData(75, "battery-limit-75", 0xCB)]
    [InlineData(80, "battery-limit-80", 0xD0)]
    public async Task AdmittedLimitCatalog_ExposesAndResolvesExactTrustedAction(
        int limit,
        string action,
        int payload)
    {
        DeviceCommandCatalog catalog = Catalog(
            Fixture(action, 0x07, 0x12, [(byte)payload]));
        var session = new ScriptedSession(Success([(byte)payload]));
        await using var port = CreatePort(session, catalog);

        Assert.Equal([limit], port.Capabilities.ChargeLimits);

        await port.SetChargeLimitAsync(limit, default);

        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal((0x07, 0x12), (command.ClassId, command.CommandId));
        Assert.Equal([(byte)payload], command.Arguments.ToArray());
    }

    [Fact]
    public async Task ProtectionOff_RequiresBothTrustedActionAndTemporaryReadEvidence()
    {
        DeviceCommandCatalog catalog = Catalog(
            Fixture(ProductionBatteryPort.Limit80Action, 0x07, 0x12, [0xD0]),
            Fixture(ProductionBatteryPort.ProtectionOffAction, 0x07, 0x12, [0x50]));
        var withoutEvidenceSession = new ScriptedSession();
        await using var withoutEvidence = CreatePort(withoutEvidenceSession, catalog);

        Assert.False(withoutEvidence.Capabilities.SupportsBatteryProtectionOff);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => withoutEvidence.SetProtectionOffAsync(default));
        Assert.Empty(withoutEvidenceSession.Commands);

        var withEvidenceSession = new ScriptedSession(Success([0x00]), Success([0x50]));
        await using var withEvidence = CreatePort(
            withEvidenceSession,
            catalog,
            AcceptedTemporaryReadEvidence());

        Assert.True(withEvidence.Capabilities.SupportsBatteryProtectionOff);
        Assert.False(await withEvidence.QueryTemporaryFullChargeAsync(default));
        await withEvidence.SetProtectionOffAsync(default);
        Assert.Equal([(0x07, 0x8F), (0x07, 0x12)], withEvidenceSession.Commands
            .Select(command => (command.ClassId, command.CommandId))
            .ToArray());
    }

    [Theory]
    [InlineData(50)]
    [InlineData(55)]
    [InlineData(60)]
    [InlineData(65)]
    [InlineData(70)]
    [InlineData(75)]
    [InlineData(80)]
    public async Task MissingAdmittedLimitActionRejectsWithoutSubmission(int limit)
    {
        var session = new ScriptedSession();
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        CapabilityUnavailableException error =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => port.SetChargeLimitAsync(limit, default));

        Assert.Contains("not admitted", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(session.Commands);
        Assert.Empty(port.Capabilities.ChargeLimits);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(85)]
    public async Task InvalidLimitRejectsWithoutSubmissionEvenIfNamedActionIsInjected(int limit)
    {
        var session = new ScriptedSession();
        await using var port = CreatePort(
            session,
            Catalog(Fixture($"battery-limit-{limit}", 0x07, 0x12, [0x01])));

        CapabilityUnavailableException error =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => port.SetChargeLimitAsync(limit, default));

        Assert.Contains("not admitted", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(session.Commands);
        Assert.Empty(port.Capabilities.ChargeLimits);
    }

    [Theory]
    [InlineData(false, 0x00)]
    [InlineData(true, 0x04)]
    public async Task AdmittedTemporarySetterResolvesExactTrustedAction(
        bool enabled,
        byte payload)
    {
        DeviceCommandCatalog catalog = Catalog(
            Fixture(ProductionBatteryPort.TemporaryEnableAction, 0x07, 0x0F, [0x04]),
            Fixture(ProductionBatteryPort.TemporaryCancelAction, 0x07, 0x0F, [0x00]));
        var session = new ScriptedSession(Success([payload]));
        await using var port = CreatePort(
            session,
            catalog,
            AcceptedTemporaryReadEvidence());

        Assert.True(port.Capabilities.SupportsTemporaryFullCharge);
        await port.SetTemporaryFullChargeAsync(enabled, default);

        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal((0x07, 0x0F), (command.ClassId, command.CommandId));
        Assert.Equal([payload], command.Arguments.ToArray());
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task TemporarySetterRequiresReadEvidenceAndBothDirections(
        bool hasReadEvidence,
        bool hasEnable,
        bool hasCancel)
    {
        var fixtures = new List<CapturedCommandFixture>();
        if (hasEnable)
        {
            fixtures.Add(Fixture(
                ProductionBatteryPort.TemporaryEnableAction,
                0x07,
                0x0F,
                [0x04]));
        }

        if (hasCancel)
        {
            fixtures.Add(Fixture(
                ProductionBatteryPort.TemporaryCancelAction,
                0x07,
                0x0F,
                [0x00]));
        }

        var session = new ScriptedSession();
        await using var port = CreatePort(
            session,
            Catalog(fixtures.ToArray()),
            hasReadEvidence ? AcceptedTemporaryReadEvidence() : NoTemporaryReadEvidence());

        Assert.False(port.Capabilities.SupportsTemporaryFullCharge);
        foreach (bool enabled in new[] { false, true })
        {
            CapabilityUnavailableException error =
                await Assert.ThrowsAsync<CapabilityUnavailableException>(
                () => port.SetTemporaryFullChargeAsync(enabled, default));
            Assert.Contains("requires admitted", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task OneShotLaptopControlsUseExactCapturedRequestsAndResponses()
    {
        DeviceCommandCatalog catalog = Catalog(
            OneShotFixture(ProductionBatteryPort.StartupAnimationOnAction, 0x0F, 0x18, [0x00, 0x00], [0x00, 0x00]),
            OneShotFixture(ProductionBatteryPort.StartupAnimationOffAction, 0x0F, 0x18, [0x00, 0x01], [0x00, 0x01]),
            OneShotFixture(ProductionBatteryPort.FunctionKeysPrimaryAction, 0x02, 0x06, [0x01, 0x00], [0x01, 0x00]),
            OneShotFixture(ProductionBatteryPort.MultimediaKeysPrimaryAction, 0x02, 0x06, [0x01, 0x01], [0x01, 0x01]),
            OneShotFixture(ProductionBatteryPort.GamingModeOnAction, 0x00, 0x08, [0x01, 0x00, 0x00, 0x00], [0x32, 0x0A, 0x00, 0x00]),
            OneShotFixture(ProductionBatteryPort.GamingModeOffAction, 0x00, 0x08, [0x00, 0x00, 0x00, 0x00], [0x32, 0x0A, 0x00, 0x00]),
            OneShotFixture(ProductionBatteryPort.MicrophoneMuteIndicatorOnAction, 0x18, 0x04, [0x00, 0x02, 0x01], [0x00, 0x02, 0x01]),
            OneShotFixture(ProductionBatteryPort.MicrophoneMuteIndicatorOffAction, 0x18, 0x04, [0x00, 0x02, 0x00], [0x00, 0x02, 0x00]));
        var cases = new[]
        {
            (new ApplyOneShotLaptopControl(OneShotLaptopControl.StartupAnimation, Enabled: true), (byte)0x0F, (byte)0x18, new byte[] { 0x00, 0x00 }, new byte[] { 0x00, 0x00 }),
            (new ApplyOneShotLaptopControl(OneShotLaptopControl.StartupAnimation, Enabled: false), (byte)0x0F, (byte)0x18, new byte[] { 0x00, 0x01 }, new byte[] { 0x00, 0x01 }),
            (new ApplyOneShotLaptopControl(OneShotLaptopControl.FunctionKeyPrimary, FunctionKeyMode: FunctionKeyPrimaryMode.FunctionKeys), (byte)0x02, (byte)0x06, new byte[] { 0x01, 0x00 }, new byte[] { 0x01, 0x00 }),
            (new ApplyOneShotLaptopControl(OneShotLaptopControl.FunctionKeyPrimary, FunctionKeyMode: FunctionKeyPrimaryMode.MultimediaKeys), (byte)0x02, (byte)0x06, new byte[] { 0x01, 0x01 }, new byte[] { 0x01, 0x01 }),
            (new ApplyOneShotLaptopControl(OneShotLaptopControl.GamingMode, Enabled: true), (byte)0x00, (byte)0x08, new byte[] { 0x01, 0x00, 0x00, 0x00 }, new byte[] { 0x32, 0x0A, 0x00, 0x00 }),
            (new ApplyOneShotLaptopControl(OneShotLaptopControl.GamingMode, Enabled: false), (byte)0x00, (byte)0x08, new byte[] { 0x00, 0x00, 0x00, 0x00 }, new byte[] { 0x32, 0x0A, 0x00, 0x00 }),
            (new ApplyOneShotLaptopControl(OneShotLaptopControl.MicrophoneMuteIndicator, Enabled: true), (byte)0x18, (byte)0x04, new byte[] { 0x00, 0x02, 0x01 }, new byte[] { 0x00, 0x02, 0x01 }),
            (new ApplyOneShotLaptopControl(OneShotLaptopControl.MicrophoneMuteIndicator, Enabled: false), (byte)0x18, (byte)0x04, new byte[] { 0x00, 0x02, 0x00 }, new byte[] { 0x00, 0x02, 0x00 }),
        };

        DeviceCapabilities capabilities = ProductionBatteryPort.CreateCapabilities(
            catalog,
            NoTemporaryReadEvidence());
        Assert.True(capabilities.OneShotLaptopControls.SetEquals(
        [
            OneShotLaptopControl.StartupAnimation,
            OneShotLaptopControl.FunctionKeyPrimary,
            OneShotLaptopControl.GamingMode,
            OneShotLaptopControl.MicrophoneMuteIndicator,
        ]));

        foreach ((ApplyOneShotLaptopControl value, byte classId, byte commandId, byte[] request, byte[] response) in cases)
        {
            var session = new ScriptedSession(Success(response));
            await using var port = CreatePort(session, catalog);

            await port.ApplyOneShotLaptopControlAsync(value, default);

            RazerCommandSpec command = Assert.Single(session.Commands);
            Assert.Equal((classId, commandId), (command.ClassId, command.CommandId));
            Assert.Equal(request, command.Arguments.ToArray());
            Assert.Equal(response.Length, command.ExpectedPayloadLength);
            Assert.NotNull(command.PayloadValidator);
            Assert.True(command.PayloadValidator(response));
            Assert.False(command.PayloadValidator(new byte[] { 0xFF }));
        }
    }

    [Fact]
    public void PublicBoundaryContainsNoRawPayloadPathOrSettingSurface()
    {
        string[] memberNames = typeof(IProductionBatteryPort)
            .GetMembers()
            .Select(member => member.Name)
            .ToArray();
        Type[] parameterTypes = typeof(IProductionBatteryPort)
            .GetMethods()
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(memberNames, name =>
            name.Contains("Raw", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Path", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Payload", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Setting", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parameterTypes, type =>
            type == typeof(byte[])
            || type == typeof(ReadOnlyMemory<byte>)
            || type == typeof(BatteryValidationSetting));
        Assert.DoesNotContain(
            typeof(IProductionBatteryPort).GetMethods(),
            method => method.ReturnType.ToString().Contains(
                nameof(BatteryValidationSetting),
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task CustomPerformanceQuery_ReturnsBothIndependentComponentLevels()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x04, 0x00]),
            Success([0x01, 0x02, 0x04, 0x00]),
            Success([0x01, 0x01, 0x01]),
            Success([0x01, 0x02, 0x02]));
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        ProductionPerformanceFanState state =
            await port.QueryPerformanceFanStateAsync(default);

        Assert.Equal(PerformancePreset.Custom, state.Performance);
        Assert.Equal(FanMode.Automatic, state.Fans);
        Assert.Equal(CustomPerformanceSettings.Default, state.CustomPerformance);
        Assert.Equal(
            [(0x82, "01010000"), (0x82, "01020000"),
             (0x87, "010100"), (0x87, "010200")],
            session.Commands.Select(command => (
                command.CommandId,
                Convert.ToHexString(command.Arguments.Span))).ToArray());
    }

    [Fact]
    public async Task CustomPerformanceSetter_AppliesModeAndComponentsThenConfirms()
    {
        CustomPerformanceSettings target = new(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Medium);
        CapturedCommandFixture[] fixtures = CustomPerformanceFixtures();
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]),
            Success([0x01, 0x01, 0x04, 0x00]),
            Success([0x01, 0x02, 0x04, 0x00]),
            Success([0x01, 0x01, 0x02]),
            Success([0x01, 0x02, 0x01]),
            Success([0x01, 0x01, 0x04, 0x00]),
            Success([0x01, 0x02, 0x04, 0x00]),
            Success([0x01, 0x01, 0x02]),
            Success([0x01, 0x02, 0x01]));
        await using var port = CreatePort(session, Catalog(fixtures));

        await port.SetCustomPerformanceAsync(target, default);

        Assert.Equal(
            [0x82, 0x82, 0x02, 0x02, 0x07, 0x07, 0x82, 0x82, 0x87, 0x87],
            session.Commands.Select(command => command.CommandId).ToArray());
        Assert.Contains(PerformancePreset.Custom, port.Capabilities.PerformancePresets);
    }

    [Fact]
    public void CustomPerformanceCapability_RequiresModeAndEveryLevelCommand()
    {
        CapturedCommandFixture[] complete = CustomPerformanceFixtures();
        Assert.Contains(
            PerformancePreset.Custom,
            ProductionBatteryPort.CreateCapabilities(
                Catalog(complete),
                NoTemporaryReadEvidence()).PerformancePresets);

        for (int missing = 0; missing < complete.Length; missing++)
        {
            CapturedCommandFixture[] incomplete = complete
                .Where((_, index) => index != missing)
                .ToArray();
            Assert.DoesNotContain(
                PerformancePreset.Custom,
                ProductionBatteryPort.CreateCapabilities(
                    Catalog(incomplete),
                    NoTemporaryReadEvidence()).PerformancePresets);
        }
    }

    private static ProductionBatteryPort CreatePort(
        IRazerCommandSession session,
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence? evidence = null,
        FanSpeedRange? fanSpeedRange = null,
        IReadOnlySet<FanMode>? admittedFanModes = null,
        IReadOnlySet<AdapterPowerState>? admittedAdapterPowerStates = null) => new(
            session,
            catalog,
            evidence ?? NoTemporaryReadEvidence(),
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1),
            fanSpeedRange,
            admittedFanModes,
            admittedAdapterPowerStates);

    private static ProductionBatteryPort CreateHyperBoostPort(
        IRazerCommandSession session,
        IHyperBoostEligibilitySource eligibility,
        DeviceCommandCatalog? catalog = null,
        HyperBoostBaseModeCapabilities? hyperBoostBaseModes = null) => new(
            session,
            catalog ?? DeviceCommandCatalog.Empty,
            NoTemporaryReadEvidence(),
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1),
            hyperBoostAccess: DeviceFeatureAccess.ReadWrite,
            hyperBoostBaseModes: hyperBoostBaseModes
                ?? new HyperBoostBaseModeCapabilities(
                    DeviceFeatureAccess.ObservedOnly,
                    DeviceFeatureAccess.ReadWrite,
                    DeviceFeatureAccess.ObservedOnly),
            hyperBoostCatalog: HyperBoostCatalog(),
            hyperBoostEligibility: eligibility);

    private static DeviceCommandCatalog Catalog(params CapturedCommandFixture[] fixtures) =>
        DeviceCommandCatalog.Create(CapturedFixtureFile.TargetDevice, fixtures);

    private static DeviceCommandCatalog HyperBoostCatalog()
    {
        var target = new DeviceIdentity("RZ09-0581", 0x1532, 0x02E0, "4.00");
        return DeviceCommandCatalog.Create(
            target,
            [
                HyperBoostFixture(
                    target,
                    ProductionBatteryPort.HyperBoostEnableFirstAction,
                    [0x01, 0x01, 0x07, 0x00]),
                HyperBoostFixture(
                    target,
                    ProductionBatteryPort.HyperBoostEnableSecondAction,
                    [0x01, 0x02, 0x07, 0x00]),
                HyperBoostFixture(
                    target,
                    ProductionBatteryPort.HyperBoostRestoreFirstAction,
                    [0x01, 0x01, 0x00, 0x00]),
                HyperBoostFixture(
                    target,
                    ProductionBatteryPort.HyperBoostRestoreSecondAction,
                    [0x01, 0x02, 0x00, 0x00]),
            ]);
    }

    private static CapturedCommandFixture HyperBoostFixture(
        DeviceIdentity target,
        string action,
        byte[] payload) => new(
            target,
            action,
            0x0D,
            0x02,
            payload,
            Captured: true,
            Decoded: true,
            Applied: true,
            ReadBack: true,
            Restored: true);

    private static ProductionBatteryReadEvidence AcceptedTemporaryReadEvidence() => new(
        new HashSet<ProductionBatteryQueryCapability>
        {
            ProductionBatteryQueryCapability.TemporaryFullCharge,
        });

    private static ProductionBatteryReadEvidence AcceptedAdapterPowerReadEvidence() => new(
        new HashSet<ProductionBatteryQueryCapability>
        {
            ProductionBatteryQueryCapability.AdapterPower,
        });

    private static ProductionBatteryReadEvidence AcceptedMcuVersionReadEvidence() => new(
        new HashSet<ProductionBatteryQueryCapability>
        {
            ProductionBatteryQueryCapability.McuVersion,
        });

    private static ProductionBatteryReadEvidence AcceptedStartupAnimationReadEvidence() => new(
        new HashSet<ProductionBatteryQueryCapability>
        {
            ProductionBatteryQueryCapability.StartupAnimation,
        });

    private static ProductionBatteryReadEvidence AcceptedFunctionKeyPrimaryReadEvidence() => new(
        new HashSet<ProductionBatteryQueryCapability>
        {
            ProductionBatteryQueryCapability.FunctionKeyPrimary,
        });

    private static ProductionBatteryReadEvidence AcceptedGamingModeReadEvidence() => new(
        new HashSet<ProductionBatteryQueryCapability>
        {
            ProductionBatteryQueryCapability.GamingMode,
        });

    private static ProductionBatteryReadEvidence
        AcceptedRz090581CapabilityDiagnosticsReadEvidence() => new(
            new HashSet<ProductionBatteryQueryCapability>
            {
                ProductionBatteryQueryCapability.Rz090581CapabilityDiagnostics,
            });

    private static ProductionBatteryReadEvidence NoTemporaryReadEvidence() => new(
        new HashSet<ProductionBatteryQueryCapability>());

    private static void AssertQuery(
        RazerCommandSpec command,
        byte commandId,
        byte[] arguments,
        int expectedPayloadLength)
    {
        Assert.Equal((0x0D, commandId), (command.ClassId, command.CommandId));
        Assert.Equal(arguments, command.Arguments.ToArray());
        Assert.Equal(expectedPayloadLength, command.ExpectedPayloadLength);
    }

    private static CapturedCommandFixture Fixture(
        string action,
        byte commandClass,
        byte commandId,
        byte[] payload) => new(
            CapturedFixtureFile.TargetDevice,
            action,
            commandClass,
            commandId,
            payload,
            Captured: true,
            Decoded: true,
            Applied: true,
            ReadBack: true,
            Restored: true);

    private static CapturedCommandFixture OneShotFixture(
        string action,
        byte commandClass,
        byte commandId,
        byte[] payload,
        byte[] expectedResponse) => Fixture(action, commandClass, commandId, payload) with
        {
            ReadBack = false,
            OneShotOnly = true,
            ExpectedResponsePayload = expectedResponse,
        };

    private static CapturedCommandFixture[] MaximumCapabilityFixtures() =>
    [
        Fixture(
            ProductionBatteryPort.PerformanceBalancedFirstAction,
            0x0D,
            0x02,
            [0x01, 0x01, 0x00, 0x00]),
        Fixture(
            ProductionBatteryPort.PerformanceBalancedSecondAction,
            0x0D,
            0x02,
            [0x01, 0x02, 0x00, 0x00]),
        Fixture(
            ProductionBatteryPort.FanMaximumStateFirstAction,
            0x0D,
            0x02,
            [0x01, 0x01, 0x00, 0x01]),
        Fixture(
            ProductionBatteryPort.FanMaximumStateSecondAction,
            0x0D,
            0x02,
            [0x01, 0x02, 0x00, 0x01]),
        Fixture(
            ProductionBatteryPort.FanMaximumSpeedFirstAction,
            0x0D,
            0x01,
            [0x01, 0x01, 0x36]),
        Fixture(
            ProductionBatteryPort.FanMaximumSpeedSecondAction,
            0x0D,
            0x01,
            [0x01, 0x02, 0x36]),
    ];

    private static CapturedCommandFixture[] CustomPerformanceFixtures() =>
    [
        Fixture(
            ProductionBatteryPort.PerformanceCustomFirstAction,
            0x0D,
            0x02,
            [0x01, 0x01, 0x04, 0x00]),
        Fixture(
            ProductionBatteryPort.PerformanceCustomSecondAction,
            0x0D,
            0x02,
            [0x01, 0x02, 0x04, 0x00]),
        Fixture(ProductionBatteryPort.CustomCpuLowAction, 0x0D, 0x07, [0x01, 0x01, 0x00]),
        Fixture(ProductionBatteryPort.CustomCpuMediumAction, 0x0D, 0x07, [0x01, 0x01, 0x01]),
        Fixture(ProductionBatteryPort.CustomCpuHighAction, 0x0D, 0x07, [0x01, 0x01, 0x02]),
        Fixture(ProductionBatteryPort.CustomGpuLowAction, 0x0D, 0x07, [0x01, 0x02, 0x00]),
        Fixture(ProductionBatteryPort.CustomGpuMediumAction, 0x0D, 0x07, [0x01, 0x02, 0x01]),
        Fixture(ProductionBatteryPort.CustomGpuHighAction, 0x0D, 0x07, [0x01, 0x02, 0x02]),
    ];

    private static HidSessionResult<ReadOnlyMemory<byte>> Success(byte[] payload) =>
        new(payload, null);

    internal sealed class ScriptedSession(
        params HidSessionResult<ReadOnlyMemory<byte>>[] results) : IRazerCommandSession
    {
        private readonly Queue<HidSessionResult<ReadOnlyMemory<byte>>> results = new(results);

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public List<RazerCommandSpec> Commands { get; } = [];

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = assignTransactionId();
            Commands.Add(command);
            return Task.FromResult(results.Count == 0
                ? new HidSessionResult<ReadOnlyMemory<byte>>(
                    default,
                    HidSessionFailureKind.NativeIoFailure)
                : results.Dequeue());
        }

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubHyperBoostEligibility : IHyperBoostEligibilitySource
    {
        private readonly CoolingPadConnectionState state;

        internal StubHyperBoostEligibility(bool padPresent)
            : this(padPresent
                ? CoolingPadConnectionState.PresentExact
                : CoolingPadConnectionState.Absent)
        {
        }

        internal StubHyperBoostEligibility(CoolingPadConnectionState state)
        {
            this.state = state;
        }

        public int ReadCount { get; private set; }

        public CoolingPadConnectionState ObserveCoolingPad()
        {
            ReadCount++;
            return state;
        }
    }

    private sealed class CancelAfterFirstWriteSession(CancellationTokenSource cancellation)
        : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public int ExecuteCount { get; private set; }

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            _ = assignTransactionId();
            ExecuteCount++;
            if (ExecuteCount == 3)
            {
                cancellation.Cancel();
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(
                command.Arguments,
                null));
        }

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingFirstSession : IRazerCommandSession
    {
        private readonly TaskCompletionSource firstExecuteRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<RazerCommandSpec> commands = [];
        private int executeCount;

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public int ExecuteCount => Volatile.Read(ref executeCount);

        public IReadOnlyList<RazerCommandSpec> Commands
        {
            get
            {
                lock (commands)
                {
                    return commands.ToArray();
                }
            }
        }

        public TaskCompletionSource FirstExecuteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            _ = assignTransactionId();
            int current = Interlocked.Increment(ref executeCount);
            lock (commands)
            {
                commands.Add(command);
            }

            if (current == 1)
            {
                FirstExecuteStarted.TrySetResult();
                await firstExecuteRelease.Task.WaitAsync(cancellationToken);
            }

            return new(command.Arguments, null);
        }

        public void ReleaseFirst() => firstExecuteRelease.TrySetResult();

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }
    }
}
