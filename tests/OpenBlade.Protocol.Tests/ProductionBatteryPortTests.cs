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
    [InlineData("0711", AdapterPowerState.UsbCPower100W)]
    [InlineData("1111", AdapterPowerState.FullPower280W)]
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

        BladeWriteException limit = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetChargeLimitAsync(80, default));
        BladeWriteException protection = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetProtectionOffAsync(default));
        BladeWriteException temporary = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetTemporaryFullChargeAsync(true, default));
        BladeWriteException performance = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(PerformancePreset.Balanced, default));
        BladeWriteException fans = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanModeAsync(FanMode.Automatic, default));

        Assert.All(
            [limit, protection, temporary, performance, fans],
            error => Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState));
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
    public void CompleteMaximumCommandSetAdmitsAutomaticAndMaximumOnly()
    {
        DeviceCapabilities capabilities = ProductionBatteryPort.CreateCapabilities(
            Catalog(MaximumCapabilityFixtures()),
            NoTemporaryReadEvidence());

        Assert.True(capabilities.PerformancePresets.SetEquals([PerformancePreset.Balanced]));
        Assert.True(capabilities.FanModes.SetEquals([FanMode.Automatic, FanMode.Maximum]));
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

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(PerformancePreset.Silent, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
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

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(preset, default));

        Assert.DoesNotContain(preset, port.Capabilities.PerformancePresets);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
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
            Success([0x01, 0x01, 0x07, 0x00]),
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
        await Assert.ThrowsAsync<BladeWriteException>(
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

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetChargeLimitAsync(limit, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
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

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetChargeLimitAsync(limit, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
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
            BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
                () => port.SetTemporaryFullChargeAsync(enabled, default));
            Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
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
            OneShotFixture(ProductionBatteryPort.GamingModeOffAction, 0x00, 0x08, [0x00, 0x00, 0x00, 0x00], [0x32, 0x0A, 0x00, 0x00]));
        var cases = new[]
        {
            (new ApplyOneShotLaptopControl(OneShotLaptopControl.StartupAnimation, Enabled: true), (byte)0x0F, (byte)0x18, new byte[] { 0x00, 0x00 }, new byte[] { 0x00, 0x00 }),
            (new ApplyOneShotLaptopControl(OneShotLaptopControl.StartupAnimation, Enabled: false), (byte)0x0F, (byte)0x18, new byte[] { 0x00, 0x01 }, new byte[] { 0x00, 0x01 }),
            (new ApplyOneShotLaptopControl(OneShotLaptopControl.FunctionKeyPrimary, FunctionKeyMode: FunctionKeyPrimaryMode.FunctionKeys), (byte)0x02, (byte)0x06, new byte[] { 0x01, 0x00 }, new byte[] { 0x01, 0x00 }),
            (new ApplyOneShotLaptopControl(OneShotLaptopControl.FunctionKeyPrimary, FunctionKeyMode: FunctionKeyPrimaryMode.MultimediaKeys), (byte)0x02, (byte)0x06, new byte[] { 0x01, 0x01 }, new byte[] { 0x01, 0x01 }),
            (new ApplyOneShotLaptopControl(OneShotLaptopControl.GamingMode, Enabled: true), (byte)0x00, (byte)0x08, new byte[] { 0x01, 0x00, 0x00, 0x00 }, new byte[] { 0x32, 0x0A, 0x00, 0x00 }),
            (new ApplyOneShotLaptopControl(OneShotLaptopControl.GamingMode, Enabled: false), (byte)0x00, (byte)0x08, new byte[] { 0x00, 0x00, 0x00, 0x00 }, new byte[] { 0x32, 0x0A, 0x00, 0x00 }),
        };

        DeviceCapabilities capabilities = ProductionBatteryPort.CreateCapabilities(
            catalog,
            NoTemporaryReadEvidence());
        Assert.True(capabilities.OneShotLaptopControls.SetEquals(
        [
            OneShotLaptopControl.StartupAnimation,
            OneShotLaptopControl.FunctionKeyPrimary,
            OneShotLaptopControl.GamingMode,
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

    private static ProductionBatteryPort CreatePort(
        IRazerCommandSession session,
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence? evidence = null) => new(
            session,
            catalog,
            evidence ?? NoTemporaryReadEvidence(),
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1));

    private static DeviceCommandCatalog Catalog(params CapturedCommandFixture[] fixtures) =>
        DeviceCommandCatalog.Create(CapturedFixtureFile.TargetDevice, fixtures);

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

    private static ProductionBatteryReadEvidence NoTemporaryReadEvidence() => new(
        new HashSet<ProductionBatteryQueryCapability>());

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
