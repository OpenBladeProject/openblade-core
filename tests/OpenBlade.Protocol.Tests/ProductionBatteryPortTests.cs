using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProductionBatteryPortTests
{
    [Fact]
    public void HyperBoostCommandSet_RequiresEveryWritableRestorationWithoutInventingBalancedAliases()
    {
        DeviceCommandCatalog enable = Catalog(
            Fixture(ProductionBatteryPort.HyperBoostEnableFirstAction, 0x0D, 2, [1, 1, 7, 0]),
            Fixture(ProductionBatteryPort.HyperBoostEnableSecondAction, 0x0D, 2, [1, 2, 7, 0]));
        DeviceCommandCatalog performance = Catalog(
            Fixture(ProductionBatteryPort.PerformanceBoostFirstAction, 0x0D, 2, [1, 1, 2, 0]),
            Fixture(ProductionBatteryPort.PerformanceBoostSecondAction, 0x0D, 2, [1, 2, 2, 0]));
        var modes = new HyperBoostBaseModeCapabilities(
            DeviceFeatureAccess.ObservedOnly, DeviceFeatureAccess.ObservedOnly, DeviceFeatureAccess.ReadWrite);
        Assert.True(ProductionBatteryPort.HasHyperBoostCommandSet(enable, performance, modes));
        Assert.False(ProductionBatteryPort.HasHyperBoostCommandSet(enable, DeviceCommandCatalog.Empty, modes));
        Assert.False(ProductionBatteryPort.HasHyperBoostCommandSet(enable, performance,
            modes with { Balanced = DeviceFeatureAccess.ReadWrite }));
        Assert.False(ProductionBatteryPort.HasHyperBoostCommandSet(enable, performance,
            modes with { Silent = DeviceFeatureAccess.ReadWrite }));
        Assert.False(ProductionBatteryPort.HasHyperBoostCommandSet(enable, performance, null));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, DeviceFeatureAccess.ObservedOnly)]
    [InlineData(true, DeviceFeatureAccess.ObservedOnly)]
    public async Task PadRequiredPerformance_SecondChannelEligibilityFailureIsIndeterminateAndNeverReplayed(
        bool padLost, DeviceFeatureAccess? padlessAccess)
    {
        var pad = new FixedHyperBoostEligibilitySource(CoolingPadConnectionState.PresentExact);
        var session = new ScriptedSession(
            Success([1, 1, 2, 0]), Success([1, 2, 2, 0]),
            Success([0x11, 0x11]), Success([1, 1, 7, 0]), Success([0, 0]))
        {
            BeforeResult = (count, _) =>
            {
                if (padLost && count == 4)
                {
                    pad.State = CoolingPadConnectionState.Absent;
                }
            },
        };
        await using ProductionBatteryPort port = CreatePadRequiredPerformancePort(session, pad, padlessAccess);
        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Performance, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Single(session.Commands, command => command.CommandId == 0x02);
        int count = session.Commands.Count;
        await Assert.ThrowsAnyAsync<Exception>(() => port.SetHyperBoostAsync(true, PerformancePreset.Performance, default));
        Assert.Equal(count, session.Commands.Count);
    }

    [Theory]
    [InlineData(0x11, 0x11)]
    [InlineData(0x0C, 0x0C)]
    public async Task PadRequiredPerformance_AdmitsOnlyPerformanceRestoreAndRechecksEveryEnableChannel(
        byte actual, byte recommended)
    {
        var session = new ScriptedSession(
            Success([1, 1, 2, 0]), Success([1, 2, 2, 0]),
            Success([actual, recommended]), Success([1, 1, 7, 0]),
            Success([actual, recommended]), Success([1, 2, 7, 0]),
            Success([1, 1, 7, 0]), Success([1, 2, 7, 0]),
            Success([1, 1, 7, 0]), Success([1, 2, 7, 0]),
            Success([1, 1, 2, 0]), Success([1, 2, 2, 0]),
            Success([1, 1, 2, 0]), Success([1, 2, 2, 0]));
        var pad = new FixedHyperBoostEligibilitySource(CoolingPadConnectionState.PresentExact);
        await using ProductionBatteryPort port = CreatePadRequiredPerformancePort(session, pad);
        await port.SetHyperBoostAsync(true, PerformancePreset.Performance, default);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x8C));
        pad.State = CoolingPadConnectionState.Absent;
        await port.SetHyperBoostAsync(false, PerformancePreset.Performance, default);
        Assert.Equal(new[] { "01010700", "01020700", "01010200", "01020200" },
            session.Commands.Where(command => command.CommandId == 2).Select(command => Convert.ToHexString(command.Arguments.Span)));
        int count = session.Commands.Count;
        await Assert.ThrowsAnyAsync<Exception>(() => port.SetHyperBoostAsync(true, PerformancePreset.Balanced, default));
        Assert.Equal(count, session.Commands.Count);
    }

    [Theory]
    [InlineData(0x0C, 0x11)]
    [InlineData(0x11, 0x12)]
    [InlineData(0x0C, 0x10)]
    public async Task PadRequiredPerformance_RejectsInsufficientOrUnknownWattageBeforeWrites(
        byte actual, byte recommended)
    {
        var session = new ScriptedSession(
            Success([1, 1, 2, 0]), Success([1, 2, 2, 0]),
            Success([actual, recommended]));
        await using ProductionBatteryPort port = CreatePadRequiredPerformancePort(session,
            new FixedHyperBoostEligibilitySource(CoolingPadConnectionState.PresentExact));
        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(true, PerformancePreset.Performance, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x02);
    }

    private static ProductionBatteryPort CreatePadRequiredPerformancePort(
        ScriptedSession session, IHyperBoostEligibilitySource pad,
        DeviceFeatureAccess? padlessAccess = null) => new(
            session,
            Catalog(Fixture(ProductionBatteryPort.PerformanceBoostFirstAction, 0x0D, 2, [1, 1, 2, 0]),
                Fixture(ProductionBatteryPort.PerformanceBoostSecondAction, 0x0D, 2, [1, 2, 2, 0])),
            AcceptedAdapterPowerReadEvidence(), new ValidationRunContext(), TimeSpan.FromSeconds(1),
            hyperBoostAccess: DeviceFeatureAccess.ReadWrite,
            hyperBoostWithoutCoolingPadAccess: padlessAccess,
            hyperBoostBaseModes: new HyperBoostBaseModeCapabilities(
                DeviceFeatureAccess.ObservedOnly, DeviceFeatureAccess.ObservedOnly, DeviceFeatureAccess.ReadWrite),
            hyperBoostCatalog: Catalog(
                Fixture(ProductionBatteryPort.HyperBoostEnableFirstAction, 0x0D, 2, [1, 1, 7, 0]),
                Fixture(ProductionBatteryPort.HyperBoostEnableSecondAction, 0x0D, 2, [1, 2, 7, 0])),
            hyperBoostEligibility: pad);

    [Fact]
    public async Task PadlessHyperBoost_DirectManualEntryPreservesEligibilityAndPairOrdering()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x01]),
            Success([0x01, 0x02, 0x00, 0x01]),
            Success([0x12, 0x11]),
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x12, 0x11]),
            Success([0x01, 0x02, 0x07, 0x00]),
            Success([0x01, 0x01, 0x07, 0x00]),
            Success([0x01, 0x02, 0x07, 0x00]));
        await using ProductionBatteryPort port = CreateDirectManualHyperBoostPort(
            session,
            CoolingPadConnectionState.Absent);

        await port.SetHyperBoostAsync(
            enabled: true,
            baseMode: PerformancePreset.Performance,
            requiresCoolingPad: false,
            cancellationToken: default);

        Assert.Equal(
            [0x82, 0x82, 0x8C, 0x02, 0x8C, 0x02, 0x82, 0x82],
            session.Commands.Select(command => command.CommandId).ToArray());
        Assert.Equal(
            ["01010700", "01020700"],
            session.Commands
                .Where(command => command.CommandId == 0x02)
                .Select(command => Convert.ToHexString(command.Arguments.Span))
                .ToArray());
    }

    [Fact]
    public async Task PadPresentHyperBoost_DoesNotRelaxManualEntryPrecondition()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x01]),
            Success([0x01, 0x02, 0x00, 0x01]));
        await using ProductionBatteryPort port = CreateDirectManualHyperBoostPort(
            session,
            CoolingPadConnectionState.PresentExact);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(
                enabled: true,
                baseMode: PerformancePreset.Performance,
                requiresCoolingPad: true,
                cancellationToken: default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(2, session.Commands.Count);
        Assert.All(session.Commands, command => Assert.Equal(0x82, command.CommandId));
    }

    private static ProductionBatteryPort CreateDirectManualHyperBoostPort(
        ScriptedSession session,
        CoolingPadConnectionState padState)
    {
        CapturedCommandFixture[] performanceAndFanFixtures =
        [
            .. MaximumCapabilityFixtures(),
            Fixture(
                ProductionBatteryPort.PerformanceBoostFirstAction,
                0x0D,
                0x02,
                [0x01, 0x01, 0x02, 0x00]),
            Fixture(
                ProductionBatteryPort.PerformanceBoostSecondAction,
                0x0D,
                0x02,
                [0x01, 0x02, 0x02, 0x00]),
        ];
        return new ProductionBatteryPort(
            session,
            Catalog(performanceAndFanFixtures),
            AcceptedAdapterPowerReadEvidence(),
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1),
            admittedAdapterPowerStates:
                new HashSet<AdapterPowerState> { AdapterPowerState.FullPower330W },
            hyperBoostAccess: DeviceFeatureAccess.ReadWrite,
            hyperBoostWithoutCoolingPadAccess: DeviceFeatureAccess.ReadWrite,
            hyperBoostBaseModes: new HyperBoostBaseModeCapabilities(
                DeviceFeatureAccess.ObservedOnly,
                DeviceFeatureAccess.ObservedOnly,
                DeviceFeatureAccess.ReadWrite),
            hyperBoostCatalog: Catalog(
                Fixture(
                    ProductionBatteryPort.HyperBoostEnableFirstAction,
                    0x0D,
                    0x02,
                    [0x01, 0x01, 0x07, 0x00]),
                Fixture(
                    ProductionBatteryPort.HyperBoostEnableSecondAction,
                    0x0D,
                    0x02,
                    [0x01, 0x02, 0x07, 0x00])),
            hyperBoostEligibility: new FixedHyperBoostEligibilitySource(padState),
            supportsDirectManualFanTransitions: true);
    }

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
    [InlineData("0C0C", AdapterPowerState.SufficientExternal)]
    [InlineData("0C11", AdapterPowerState.LimitedExternal)]
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
    public async Task HyperBoost_TransientAdapterReadFailureIsTaggedBeforeSubmission()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]),
            new HidSessionResult<ReadOnlyMemory<byte>>(
                default,
                HidSessionFailureKind.NativeIoFailure));
        DeviceCommandCatalog hyperBoostCatalog = Catalog(
            Fixture(
                ProductionBatteryPort.HyperBoostEnableFirstAction,
                0x0D,
                0x02,
                [0x01, 0x01, 0x07, 0x00]),
            Fixture(
                ProductionBatteryPort.HyperBoostEnableSecondAction,
                0x0D,
                0x02,
                [0x01, 0x02, 0x07, 0x00]),
            Fixture(
                ProductionBatteryPort.HyperBoostRestoreFirstAction,
                0x0D,
                0x02,
                [0x01, 0x01, 0x00, 0x00]),
            Fixture(
                ProductionBatteryPort.HyperBoostRestoreSecondAction,
                0x0D,
                0x02,
                [0x01, 0x02, 0x00, 0x00]));
        await using var port = new ProductionBatteryPort(
            session,
            DeviceCommandCatalog.Empty,
            AcceptedAdapterPowerReadEvidence(),
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1),
            admittedAdapterPowerStates:
                new HashSet<AdapterPowerState>
                {
                    AdapterPowerState.FullPower330W,
                },
            hyperBoostAccess: DeviceFeatureAccess.ReadWrite,
            hyperBoostWithoutCoolingPadAccess: DeviceFeatureAccess.ReadWrite,
            hyperBoostBaseModes: new HyperBoostBaseModeCapabilities(
                DeviceFeatureAccess.ObservedOnly,
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ObservedOnly),
            hyperBoostCatalog: hyperBoostCatalog,
            hyperBoostEligibility: new FixedHyperBoostEligibilitySource(
                CoolingPadConnectionState.Absent));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetHyperBoostAsync(
                enabled: true,
                PerformancePreset.Balanced,
                default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(
            BladeWriteFailurePhase.HyperBoostAdapterEligibilityRead,
            error.FailurePhase);
        Assert.IsType<NativeDeviceReadException>(error.InnerException);
        Assert.Equal(3, session.Commands.Count);
        Assert.DoesNotContain(
            session.Commands,
            command => command.Arguments.Span.Contains((byte)0x07));
    }

    [Fact]
    public async Task HyperBoost_FinalConfirmationReadFailurePreservesPostAckPhase()
    {
        byte[] first = [0x01, 0x01, 0x07, 0x00];
        byte[] second = [0x01, 0x02, 0x07, 0x00];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]),
            Success([0x12, 0x11]),
            Success(first),
            Success(second),
            new HidSessionResult<ReadOnlyMemory<byte>>(
                default,
                HidSessionFailureKind.Timeout));
        DeviceCommandCatalog hyperBoostCatalog = Catalog(
            Fixture(
                ProductionBatteryPort.HyperBoostEnableFirstAction,
                0x0D,
                0x02,
                first),
            Fixture(
                ProductionBatteryPort.HyperBoostEnableSecondAction,
                0x0D,
                0x02,
                second),
            Fixture(
                ProductionBatteryPort.HyperBoostRestoreFirstAction,
                0x0D,
                0x02,
                [0x01, 0x01, 0x00, 0x00]),
            Fixture(
                ProductionBatteryPort.HyperBoostRestoreSecondAction,
                0x0D,
                0x02,
                [0x01, 0x02, 0x00, 0x00]));
        await using var port = new ProductionBatteryPort(
            session,
            DeviceCommandCatalog.Empty,
            AcceptedAdapterPowerReadEvidence(),
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1),
            admittedAdapterPowerStates:
                new HashSet<AdapterPowerState>
                {
                    AdapterPowerState.FullPower330W,
                },
            hyperBoostAccess: DeviceFeatureAccess.ReadWrite,
            hyperBoostWithoutCoolingPadAccess: DeviceFeatureAccess.ReadWrite,
            hyperBoostBaseModes: new HyperBoostBaseModeCapabilities(
                DeviceFeatureAccess.ObservedOnly,
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ObservedOnly),
            hyperBoostCatalog: hyperBoostCatalog,
            hyperBoostEligibility: new FixedHyperBoostEligibilitySource(
                CoolingPadConnectionState.Absent));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetHyperBoostAsync(
                enabled: true,
                PerformancePreset.Balanced,
                default));

        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(
            BladeWriteFailurePhase.ThermalFinalConfirmationRead,
            error.FailurePhase);
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Equal(6, session.Commands.Count);
        Assert.Equal(
            ["01010700", "01020700"],
            session.Commands
                .Where(command => command.CommandId == 0x02)
                .Select(command => Convert.ToHexString(command.Arguments.Span))
                .ToArray());
    }

    [Fact]
    public async Task HyperBoost_FinalConfirmationCancellationPreservesPostAckPhase()
    {
        byte[] first = [0x01, 0x01, 0x07, 0x00];
        byte[] second = [0x01, 0x02, 0x07, 0x00];
        using var cancellation = new CancellationTokenSource();
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]),
            Success([0x12, 0x11]),
            Success(first),
            Success(second))
        {
            BeforeResult = (commandCount, token) =>
            {
                if (commandCount == 6)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
            },
        };
        DeviceCommandCatalog hyperBoostCatalog = Catalog(
            Fixture(
                ProductionBatteryPort.HyperBoostEnableFirstAction,
                0x0D,
                0x02,
                first),
            Fixture(
                ProductionBatteryPort.HyperBoostEnableSecondAction,
                0x0D,
                0x02,
                second),
            Fixture(
                ProductionBatteryPort.HyperBoostRestoreFirstAction,
                0x0D,
                0x02,
                [0x01, 0x01, 0x00, 0x00]),
            Fixture(
                ProductionBatteryPort.HyperBoostRestoreSecondAction,
                0x0D,
                0x02,
                [0x01, 0x02, 0x00, 0x00]));
        await using var port = new ProductionBatteryPort(
            session,
            DeviceCommandCatalog.Empty,
            AcceptedAdapterPowerReadEvidence(),
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1),
            admittedAdapterPowerStates:
                new HashSet<AdapterPowerState>
                {
                    AdapterPowerState.FullPower330W,
                },
            hyperBoostAccess: DeviceFeatureAccess.ReadWrite,
            hyperBoostWithoutCoolingPadAccess: DeviceFeatureAccess.ReadWrite,
            hyperBoostBaseModes: new HyperBoostBaseModeCapabilities(
                DeviceFeatureAccess.ObservedOnly,
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ObservedOnly),
            hyperBoostCatalog: hyperBoostCatalog,
            hyperBoostEligibility: new FixedHyperBoostEligibilitySource(
                CoolingPadConnectionState.Absent));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetHyperBoostAsync(
                enabled: true,
                PerformancePreset.Balanced,
                cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(
            BladeWriteFailurePhase.ThermalFinalConfirmationRead,
            error.FailurePhase);
        Assert.IsType<OperationCanceledException>(error.InnerException);
        Assert.Equal(6, session.Commands.Count);
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
    public async Task DirectMaximumFanSetter_EntersFromPerformanceAutomaticThenWritesTargets()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x02, 0x00]),
            Success([0x01, 0x02, 0x02, 0x00]),
            Success([0x01, 0x01, 0x00, 0x01]),
            Success([0x01, 0x02, 0x00, 0x01]),
            Success([0x01, 0x01, 0x36]),
            Success([0x01, 0x02, 0x36]));
        await using var port = CreatePort(
            session,
            Catalog(MaximumCapabilityFixtures()),
            supportsDirectManualFanTransitions: true);

        await port.SetFanModeAsync(FanMode.Maximum, default);

        Assert.Equal(
            ["01010000", "01020000", "01010001", "01020001", "010136", "010236"],
            session.Commands.Select(command =>
                Convert.ToHexString(command.Arguments.Span)).ToArray());
    }

    [Fact]
    public async Task DirectMaximumFanSetter_AlreadyManualWritesOnlyTargets()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x01]),
            Success([0x01, 0x02, 0x00, 0x01]),
            Success([0x01, 0x01, 0x36]),
            Success([0x01, 0x02, 0x36]));
        await using var port = CreatePort(
            session,
            Catalog(MaximumCapabilityFixtures()),
            supportsDirectManualFanTransitions: true);

        await port.SetFanModeAsync(FanMode.Maximum, default);

        Assert.Equal(
            ["01010000", "01020000", "010136", "010236"],
            session.Commands.Select(command =>
                Convert.ToHexString(command.Arguments.Span)).ToArray());
        Assert.Equal([0x82, 0x82, 0x01, 0x01],
            session.Commands.Select(command => command.CommandId).ToArray());
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
    public async Task DirectManualFanSetter_EntersFromPerformanceAutomaticThenWritesTarget()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x02, 0x00]),
            Success([0x01, 0x02, 0x02, 0x00]),
            Success([0x01, 0x01, 0x00, 0x01]),
            Success([0x01, 0x02, 0x00, 0x01]),
            Success([0x01, 0x01, 0x28]),
            Success([0x01, 0x02, 0x28]));
        await using var port = CreatePort(
            session,
            Catalog(MaximumCapabilityFixtures()),
            supportsDirectManualFanTransitions: true);

        await port.SetFanControlAsync(
            new FanControlSettings(FanMode.Fixed, 4000, ManualFanCurve.Default),
            default);

        Assert.True(port.Capabilities.SupportsDirectManualFanTransitions);
        Assert.Equal(
            ["01010000", "01020000", "01010001", "01020001", "010128", "010228"],
            session.Commands.Select(command =>
                Convert.ToHexString(command.Arguments.Span)).ToArray());
    }

    [Theory]
    [InlineData(0x03, 0x12, 0x11)]
    [InlineData(0x06, 0x12, 0x11)]
    [InlineData(0x03, 0x0C, 0x0C)]
    public async Task DirectManualFanSetter_BatteryAutomaticRechecksFullPowerBeforeEveryWrite(
        byte thermalMode, byte actual, byte recommended)
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, thermalMode, 0x00]),
            Success([0x01, 0x02, thermalMode, 0x00]),
            Success([actual, recommended]),
            Success([0x01, 0x01, 0x00, 0x01]),
            Success([actual, recommended]),
            Success([0x01, 0x02, 0x00, 0x01]),
            Success([actual, recommended]),
            Success([0x01, 0x01, 0x28]),
            Success([actual, recommended]),
            Success([0x01, 0x02, 0x28]));
        await using var port = CreatePort(
            session,
            Catalog(MaximumCapabilityFixtures()),
            AcceptedAdapterPowerReadEvidence(),
            admittedAdapterPowerStates: AdapterPowerProtocol.SynapseAdapterStates,
            supportsDirectManualFanTransitions: true);

        await port.SetFanControlAsync(
            new FanControlSettings(FanMode.Fixed, 4000, ManualFanCurve.Default),
            default);

        Assert.Equal(
            [0x82, 0x82, 0x8C, 0x02, 0x8C, 0x02, 0x8C, 0x01, 0x8C, 0x01],
            session.Commands.Select(command => command.CommandId).ToArray());
    }

    [Theory]
    [InlineData(0x03)]
    [InlineData(0x06)]
    public async Task DirectManualFanSetter_BatteryAutomaticRejectsFreshBatteryPower(
        byte thermalMode)
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, thermalMode, 0x00]),
            Success([0x01, 0x02, thermalMode, 0x00]),
            Success([0x00, 0x11]));
        await using var port = CreatePort(
            session,
            Catalog(MaximumCapabilityFixtures()),
            AcceptedAdapterPowerReadEvidence(),
            admittedAdapterPowerStates:
                new HashSet<AdapterPowerState> { AdapterPowerState.FullPower330W },
            supportsDirectManualFanTransitions: true);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanControlAsync(
                new FanControlSettings(FanMode.Fixed, 4000, ManualFanCurve.Default),
                default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(BladeWriteFailurePhase.ThermalPreWriteRead, error.FailurePhase);
        Assert.Equal([0x82, 0x82, 0x8C],
            session.Commands.Select(command => command.CommandId).ToArray());
    }

    [Fact]
    public async Task DirectManualFanSetter_BatteryAutomaticPowerLossAfterEntryIsIndeterminate()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x03, 0x00]),
            Success([0x01, 0x02, 0x03, 0x00]),
            Success([0x12, 0x11]),
            Success([0x01, 0x01, 0x00, 0x01]),
            Success([0x00, 0x11]));
        await using var port = CreatePort(
            session,
            Catalog(MaximumCapabilityFixtures()),
            AcceptedAdapterPowerReadEvidence(),
            admittedAdapterPowerStates:
                new HashSet<AdapterPowerState> { AdapterPowerState.FullPower330W },
            supportsDirectManualFanTransitions: true);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanControlAsync(
                new FanControlSettings(FanMode.Fixed, 4000, ManualFanCurve.Default),
                default));

        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(BladeWriteFailurePhase.Unspecified, error.FailurePhase);
        Assert.Equal([0x82, 0x82, 0x8C, 0x02, 0x8C],
            session.Commands.Select(command => command.CommandId).ToArray());
        int commandCount = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanControlAsync(
                new FanControlSettings(FanMode.Fixed, 4000, ManualFanCurve.Default),
                default));
        Assert.Equal(commandCount, session.Commands.Count);
    }

    [Fact]
    public void DirectManualFanAutomaticEntry_RejectsMissingOrHyperBoostPerformanceIdentity()
    {
        Assert.False(ProductionBatteryPort.IsDirectManualFanAutomaticEntryState(
            new ProductionPerformanceFanState(null, FanMode.Automatic)));
        Assert.False(ProductionBatteryPort.IsDirectManualFanAutomaticEntryState(
            new ProductionPerformanceFanState(
                PerformancePreset.Performance,
                FanMode.Automatic)
            {
                HyperBoostActive = true,
            }));
        Assert.True(ProductionBatteryPort.IsDirectManualFanAutomaticEntryState(
            new ProductionPerformanceFanState(
                PerformancePreset.BatterySaver,
                FanMode.Automatic)));
    }

    [Fact]
    public async Task DirectManualFanSetter_SecondEntryFailureRemainsIndeterminate()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x02, 0x00]),
            Success([0x01, 0x02, 0x02, 0x00]),
            Success([0x01, 0x01, 0x00, 0x01]),
            new HidSessionResult<ReadOnlyMemory<byte>>(
                default,
                HidSessionFailureKind.NativeIoFailure));
        await using var port = CreatePort(
            session,
            Catalog(MaximumCapabilityFixtures()),
            supportsDirectManualFanTransitions: true);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanControlAsync(
                new FanControlSettings(FanMode.Fixed, 4000, ManualFanCurve.Default),
                default));

        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(
            BladeWriteFailurePhase.AdjustableFanSecondManualEntrySubmission,
            error.FailurePhase);
        Assert.Equal(4, session.Commands.Count);
    }

    [Fact]
    public async Task AdjustableFanPreWriteReadFailureReportsExactPreSubmissionPhase()
    {
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.GetCompletion, HidSessionFailureKind.NativeIoFailure, 31, 0);
        var session = new ScriptedSession(
            new HidSessionResult<ReadOnlyMemory<byte>>(
                default,
                HidSessionFailureKind.NativeIoFailure)
            { Diagnostic = diagnostic });
        await using var port = CreatePort(
            session,
            Catalog(MaximumCapabilityFixtures()));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanControlAsync(
                new FanControlSettings(
                    FanMode.Fixed,
                    3700,
                    ManualFanCurve.Default),
                default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(BladeWriteFailurePhase.ThermalPreWriteRead, error.FailurePhase);
        NativeDeviceReadException read = Assert.IsType<NativeDeviceReadException>(error.InnerException);
        Assert.Equal(HidSessionFailureKind.NativeIoFailure, read.Failure);
        Assert.Equal(diagnostic, read.Diagnostic);
        Assert.Equal(DeviceStateQuery.ThermalChannelOne, read.StateQuery);
        Assert.Equal(diagnostic, error.Diagnostic);
        Assert.Equal(read.StateQuery, error.StateQuery);
        var wrapped = new BladeWriteException(WriteSubmissionState.Indeterminate, "outer", error);
        Assert.Equal(diagnostic, wrapped.Diagnostic);
        Assert.Equal(read.StateQuery, wrapped.StateQuery);
        Assert.Single(session.Commands);
        Assert.Equal(0x82, session.Commands[0].CommandId);
    }

    [Fact]
    public async Task AdjustableFanFailureAfterSubmissionRemainsIndeterminate()
    {
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.GetCompletion, HidSessionFailureKind.NativeIoFailure, 31, 0);
        byte[] state1 = [0x01, 0x01, 0x00, 0x01];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]),
            Success(state1),
            new HidSessionResult<ReadOnlyMemory<byte>>(
                default,
                HidSessionFailureKind.NativeIoFailure)
            { Diagnostic = diagnostic });
        await using var port = CreatePort(
            session,
            Catalog(MaximumCapabilityFixtures()));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanControlAsync(
                FanControlSettings.FromLegacy(FanMode.Maximum),
                default));

        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(
            BladeWriteFailurePhase.AdjustableFanSecondManualEntrySubmission,
            error.FailurePhase);
        Assert.Contains("manual entry channel 2 of 2", error.Message);
        Assert.Contains(nameof(HidSessionFailureKind.NativeIoFailure), error.Message);
        Assert.Equal(diagnostic, error.Diagnostic);
        Assert.Equal((byte)0x0D, error.CommandClass);
        Assert.Equal((byte)0x02, error.CommandId);
        var wrapped = new BladeWriteException(WriteSubmissionState.Indeterminate, "outer", error);
        Assert.Equal(diagnostic, wrapped.Diagnostic);
        Assert.Equal(error.CommandClass, wrapped.CommandClass);
        Assert.Equal(error.CommandId, wrapped.CommandId);
        Assert.Equal(4, session.Commands.Count);
    }

    [Fact]
    public async Task AdjustableFanFirstManualEntryFailureHasTypedContinuationPhase()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]),
            new HidSessionResult<ReadOnlyMemory<byte>>(
                default,
                HidSessionFailureKind.NativeIoFailure));
        await using var port = CreatePort(
            session,
            Catalog(MaximumCapabilityFixtures()));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanControlAsync(
                FanControlSettings.FromLegacy(FanMode.Maximum),
                default));

        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(
            BladeWriteFailurePhase.AdjustableFanFirstManualEntrySubmission,
            error.FailurePhase);
        Assert.Contains("manual entry channel 1 of 2", error.Message);
        Assert.Equal(3, session.Commands.Count);
    }

    [Fact]
    public async Task AdjustableFanTargetFailureReportsSanitizedStageAndChannel()
    {
        byte[] state1 = [0x01, 0x01, 0x00, 0x01];
        byte[] state2 = [0x01, 0x02, 0x00, 0x01];
        byte[] speed1 = [0x01, 0x01, 0x25];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x00]),
            Success([0x01, 0x02, 0x00, 0x00]),
            Success(state1),
            Success(state2),
            Success(speed1),
            new HidSessionResult<ReadOnlyMemory<byte>>(
                default,
                HidSessionFailureKind.Timeout));
        await using var port = CreatePort(
            session,
            Catalog(MaximumCapabilityFixtures()));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanControlAsync(
                new FanControlSettings(
                    FanMode.Fixed,
                    3700,
                    ManualFanCurve.Default),
                default));

        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(BladeWriteFailurePhase.Unspecified, error.FailurePhase);
        Assert.Contains("fan target channel 2 of 2", error.Message);
        Assert.Contains(nameof(HidSessionFailureKind.Timeout), error.Message);
        Assert.DoesNotContain("0102", error.Message);
        Assert.Equal(6, session.Commands.Count);
    }

    [Fact]
    public async Task AdjustableFanTargetUpdateFailureReportsTargetStageAndChannel()
    {
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.GetCompletion, HidSessionFailureKind.Timeout, 995, 0);
        byte[] manualState1 = [0x01, 0x01, 0x00, 0x01];
        byte[] manualState2 = [0x01, 0x02, 0x00, 0x01];
        byte[] speed1 = [0x01, 0x01, 0x20];
        var session = new ScriptedSession(
            Success(manualState1),
            Success(manualState2),
            Success(speed1),
            new HidSessionResult<ReadOnlyMemory<byte>>(
                default,
                HidSessionFailureKind.Timeout)
            { Diagnostic = diagnostic });
        await using var port = CreatePort(
            session,
            Catalog(MaximumCapabilityFixtures()));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanTargetsAsync(3200, 3400, default));

        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(BladeWriteFailurePhase.Unspecified, error.FailurePhase);
        Assert.Contains("fan target channel 2 of 2", error.Message);
        Assert.DoesNotContain("manual entry", error.Message);
        Assert.Contains(nameof(HidSessionFailureKind.Timeout), error.Message);
        Assert.DoesNotContain("0102", error.Message);
        Assert.Equal(diagnostic, error.Diagnostic);
        Assert.Equal((byte)0x0D, error.CommandClass);
        Assert.Equal((byte)0x01, error.CommandId);
        Assert.Equal(4, session.Commands.Count);
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
    public async Task CurveTargetUpdateRefusesMismatchedManualChannelState()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x01]),
            Success([0x01, 0x02, 0x00, 0x00]));
        await using var port = CreatePort(session, Catalog(MaximumCapabilityFixtures()));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetFanTargetsAsync(3000, 3200, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(BladeWriteFailurePhase.ThermalPreWriteRead, error.FailurePhase);
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
    public async Task ThermalPreWriteReadFailureReportsExactPreSubmissionPhase()
    {
        var session = new ScriptedSession(
            new HidSessionResult<ReadOnlyMemory<byte>>(
                default,
                HidSessionFailureKind.NativeIoFailure));
        await using var port = CreatePort(
            session,
            Catalog(
                Fixture(
                    ProductionBatteryPort.PerformanceSilentFirstAction,
                    0x0D,
                    0x02,
                    [0x01, 0x01, 0x05, 0x00]),
                Fixture(
                    ProductionBatteryPort.PerformanceSilentSecondAction,
                    0x0D,
                    0x02,
                    [0x01, 0x02, 0x05, 0x00])));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(PerformancePreset.Silent, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(BladeWriteFailurePhase.ThermalPreWriteRead, error.FailurePhase);
        Assert.IsType<NativeDeviceReadException>(error.InnerException);
        Assert.Single(session.Commands);
        Assert.Equal(0x82, session.Commands[0].CommandId);
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

    [Fact]
    public async Task DirectPerformanceSetter_ExitsManualWithOnlySelectedPerformancePair()
    {
        byte[] first = [0x01, 0x01, 0x02, 0x00];
        byte[] second = [0x01, 0x02, 0x02, 0x00];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x01]),
            Success([0x01, 0x02, 0x00, 0x01]),
            Success(first),
            Success(second));
        CapturedCommandFixture[] fixtures =
        [
            .. MaximumCapabilityFixtures(),
            Fixture(
                ProductionBatteryPort.PerformanceBoostFirstAction,
                0x0D,
                0x02,
                first),
            Fixture(
                ProductionBatteryPort.PerformanceBoostSecondAction,
                0x0D,
                0x02,
                second),
        ];
        await using var port = CreatePort(
            session,
            Catalog(fixtures),
            supportsDirectManualFanTransitions: true);

        await port.SetPerformanceAsync(PerformancePreset.Performance, default);

        Assert.Equal(
            ["01010000", "01020000", "01010200", "01020200"],
            session.Commands.Select(command =>
                Convert.ToHexString(command.Arguments.Span)).ToArray());
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
    public async Task DirectPerformanceSetter_RejectsManualOriginForBatteryDestinations(
        PerformancePreset preset,
        string firstAction,
        string secondAction,
        byte mode)
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x01]),
            Success([0x01, 0x02, 0x00, 0x01]));
        await using var port = CreatePort(
            session,
            Catalog(
                [
                    .. MaximumCapabilityFixtures(),
                    Fixture(firstAction, 0x0D, 0x02, [0x01, 0x01, mode, 0x00]),
                    Fixture(secondAction, 0x0D, 0x02, [0x01, 0x02, mode, 0x00]),
                ]),
            supportsDirectManualFanTransitions: true);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(preset, default));

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
        Assert.Equal(
            BladeWriteFailurePhase.ThermalFinalConfirmationRead,
            error.FailurePhase);
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
        Assert.Equal(DeviceStateQuery.ThermalChannelAgreement, error.StateQuery);
        Assert.Equal("01 01 00 00 / 01 02 05 00", error.RejectedPayloadHex);
        Assert.Equal(WriteSubmissionState.Indeterminate, blockedWrite.SubmissionState);
        Assert.Equal(2, session.Commands.Count);
        Assert.All(session.Commands, command => Assert.Equal(0x82, command.CommandId));
    }

    [Theory]
    [InlineData(true, (int)DeviceStateQuery.ThermalChannelOne, 1)]
    [InlineData(false, (int)DeviceStateQuery.ThermalChannelTwo, 2)]
    public async Task UnknownThermalPayload_ReportsExactChannelAndPoisonsFurtherGetters(
        bool failFirst,
        int expectedQuery,
        int expectedCommandCount)
    {
        var session = failFirst
            ? new ScriptedSession(Success([0x01, 0x01, 0x08, 0x00]))
            : new ScriptedSession(
                Success([0x01, 0x01, 0x00, 0x00]),
                Success([0x01, 0x02, 0x08, 0x00]));
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        RazerProtocolException error = await Assert.ThrowsAsync<RazerProtocolException>(
            () => port.QueryPerformanceFanStateAsync(default));
        await Assert.ThrowsAsync<IOException>(() => port.QueryProtectionAsync(default));

        Assert.Equal(HidProtocolValidationFailure.PayloadValidator, error.ValidationFailure);
        Assert.Equal(failFirst ? "01 01 08 00" : "01 02 08 00", error.RejectedPayloadHex);
        Assert.Equal((DeviceStateQuery)expectedQuery, error.StateQuery);
        Assert.Equal(expectedCommandCount, session.Commands.Count);
    }

    [Theory]
    [InlineData(false, (int)DeviceStateQuery.BatteryProtection, 0x92)]
    [InlineData(true, (int)DeviceStateQuery.TemporaryFullCharge, 0x8F)]
    public async Task BatteryGetterProtocolFailure_ReportsExactQueryAndPreservesCategory(
        bool temporaryFullCharge,
        int expectedQuery,
        int expectedCommandId)
    {
        var session = new ScriptedSession(ProtocolFailure(HidProtocolValidationFailure.Checksum));
        await using var port = CreatePort(
            session,
            DeviceCommandCatalog.Empty,
            temporaryFullCharge ? AcceptedTemporaryReadEvidence() : NoTemporaryReadEvidence());

        RazerProtocolException error = temporaryFullCharge
            ? await Assert.ThrowsAsync<RazerProtocolException>(
                () => port.QueryTemporaryFullChargeAsync(default))
            : await Assert.ThrowsAsync<RazerProtocolException>(
                () => port.QueryProtectionAsync(default));
        await Assert.ThrowsAsync<IOException>(() => port.QueryProtectionAsync(default));

        Assert.Equal(HidProtocolValidationFailure.Checksum, error.ValidationFailure);
        Assert.Equal((DeviceStateQuery)expectedQuery, error.StateQuery);
        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal(0x07, command.ClassId);
        Assert.Equal((byte)expectedCommandId, command.CommandId);
        Assert.Equal([0x00], command.Arguments.ToArray());
    }

    [Theory]
    [InlineData(false, new byte[] { 0xB3 }, "B3")]
    [InlineData(true, new byte[] { 0x15 }, "15")]
    public async Task BatteryGetterContentRejection_SurfacesBoundedRejectedPayload(
        bool temporaryFullCharge,
        byte[] payload,
        string expectedHex)
    {
        var session = new ScriptedSession(Success(payload));
        await using var port = CreatePort(
            session,
            DeviceCommandCatalog.Empty,
            temporaryFullCharge ? AcceptedTemporaryReadEvidence() : NoTemporaryReadEvidence());

        RazerProtocolException error = temporaryFullCharge
            ? await Assert.ThrowsAsync<RazerProtocolException>(
                () => port.QueryTemporaryFullChargeAsync(default))
            : await Assert.ThrowsAsync<RazerProtocolException>(
                () => port.QueryProtectionAsync(default));
        await Assert.ThrowsAsync<IOException>(() => port.QueryProtectionAsync(default));

        Assert.Equal(HidProtocolValidationFailure.PayloadValidator, error.ValidationFailure);
        Assert.Equal(expectedHex, error.RejectedPayloadHex);
        Assert.True(DeviceStateContentRejection.IsContentRejection(error));
        Assert.Equal(
            temporaryFullCharge
                ? "Temporary full charge query (07/8F) returned unrecognized payload 15 (PayloadValidator)."
                : "Battery protection query (07/92) returned unrecognized payload B3 (PayloadValidator).",
            DeviceStateContentRejection.Describe(error, "Device state query"));
    }

    [Fact]
    public async Task BatteryGetterChannelFailure_CarriesNoPayloadAndIsNotContentRejection()
    {
        var session = new ScriptedSession(
            ProtocolFailure(HidProtocolValidationFailure.TransactionMismatch));
        await using var port = CreatePort(
            session,
            DeviceCommandCatalog.Empty,
            AcceptedTemporaryReadEvidence());

        RazerProtocolException error = await Assert.ThrowsAsync<RazerProtocolException>(
            () => port.QueryTemporaryFullChargeAsync(default));

        Assert.Null(error.RejectedPayloadHex);
        Assert.False(DeviceStateContentRejection.IsContentRejection(error));
        Assert.Equal(
            "Temporary full charge query (07/8F) failed protocol validation (TransactionMismatch).",
            DeviceStateContentRejection.Describe(error, "Device state query"));
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

    [Theory]
    [InlineData(HidSessionFailureKind.NativeIoFailure, WriteSubmissionState.Indeterminate)]
    [InlineData(HidSessionFailureKind.ConflictBlocked, WriteSubmissionState.KnownNotApplied)]
    public async Task BatterySetterPreservesDiagnosticAndSubmissionClassification(
        HidSessionFailureKind failure, WriteSubmissionState expectedState)
    {
        var diagnostic = new HidSessionFailureDiagnostic(
            failure == HidSessionFailureKind.ConflictBlocked
                ? HidSessionFailurePhase.Guard : HidSessionFailurePhase.SetCompletion,
            failure, 31, 0);
        var session = new ScriptedSession(
            new HidSessionResult<ReadOnlyMemory<byte>>(default, failure) { Diagnostic = diagnostic });
        await using var port = CreatePort(session,
            Catalog(Fixture("battery-limit-80", 0x07, 0x12, [0xD0])));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetChargeLimitAsync(80, default));

        Assert.Equal(expectedState, error.SubmissionState);
        Assert.Equal(diagnostic, error.Diagnostic);
        Assert.Equal((byte)0x07, error.CommandClass);
        Assert.Equal((byte)0x12, error.CommandId);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task QueryTemporaryFullCharge_DecodesPowerModeControlBitmaskOnEveryModel()
    {
        foreach (ProductionBatteryReadEvidence evidence in TemporaryFullChargeEvidence())
        {
            var session = new ScriptedSession(Success([0x08]), Success([0x0C]), Success([0x10]));
            await using var port = CreatePort(session, Catalog(), evidence);

            Assert.False(await port.QueryTemporaryFullChargeAsync(default));
            Assert.True(await port.QueryTemporaryFullChargeAsync(default));
            await Assert.ThrowsAnyAsync<Exception>(
                () => port.QueryTemporaryFullChargeAsync(default));
            Assert.All(session.Commands, query =>
                Assert.Equal((0x07, 0x8F), (query.ClassId, query.CommandId)));
        }
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
        var session = new ScriptedSession(Success([0x00]), Success([payload]));
        await using var port = CreatePort(
            session,
            catalog,
            AcceptedTemporaryReadEvidence());

        Assert.True(port.Capabilities.SupportsTemporaryFullCharge);
        await port.SetTemporaryFullChargeAsync(enabled, default);

        Assert.Equal(2, session.Commands.Count);
        RazerCommandSpec command = session.Commands[1];
        Assert.Equal((0x07, 0x0F), (command.ClassId, command.CommandId));
        Assert.Equal([payload], command.Arguments.ToArray());
    }

    [Theory]
    [InlineData(false, 0x00, 0x00)]
    [InlineData(true, 0x00, 0x04)]
    [InlineData(false, 0x04, 0x00)]
    [InlineData(true, 0x04, 0x04)]
    public async Task TemporarySetterWritesAfterFreshReadShowsNoOtherSettingsOnEveryModel(
        bool enabled,
        byte current,
        byte payload)
    {
        foreach (ProductionBatteryReadEvidence evidence in TemporaryFullChargeEvidence())
        {
            var session = new ScriptedSession(Success([current]), Success([payload]));
            await using var port = CreatePort(session, TemporarySetterCatalog(), evidence);

            await port.SetTemporaryFullChargeAsync(enabled, default);

            Assert.Collection(
                session.Commands,
                read => Assert.Equal((0x07, 0x8F), (read.ClassId, read.CommandId)),
                write =>
                {
                    Assert.Equal((0x07, 0x0F), (write.ClassId, write.CommandId));
                    Assert.Equal([payload], write.Arguments.ToArray());
                });
        }
    }

    [Theory]
    [InlineData(true, 0x08, "local dimming")]
    [InlineData(false, 0x08, "local dimming")]
    [InlineData(false, 0x0C, "local dimming")]
    [InlineData(true, 0x01, "CPU boost")]
    [InlineData(false, 0x05, "CPU boost")]
    [InlineData(true, 0x02, "maximum fan speed")]
    [InlineData(false, 0x06, "maximum fan speed")]
    public async Task TemporarySetterBlocksWhenOtherPowerModeSettingsAreOnOnEveryModel(
        bool enabled,
        byte current,
        string settingName)
    {
        foreach (ProductionBatteryReadEvidence evidence in TemporaryFullChargeEvidence())
        {
            var session = new ScriptedSession(Success([current]), Success([0x00]), Success([0x04]));
            await using var port = CreatePort(session, TemporarySetterCatalog(), evidence);

            BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
                () => port.SetTemporaryFullChargeAsync(enabled, default));

            Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
            Assert.StartsWith(
                "OpenBlade won't change temporary full charge because it would also change "
                + "other laptop settings that are on (",
                error.Message,
                StringComparison.Ordinal);
            Assert.Contains(settingName, error.Message, StringComparison.Ordinal);
            RazerCommandSpec read = Assert.Single(session.Commands);
            Assert.Equal((0x07, 0x8F), (read.ClassId, read.CommandId));

            // The block is not a session failure: a later admitted read still works.
            Assert.False(await port.QueryTemporaryFullChargeAsync(default));
        }
    }

    [Theory]
    [InlineData("rejected", false)]
    [InlineData("rejected", true)]
    [InlineData("failed", false)]
    [InlineData("failed", true)]
    [InlineData("throws", false)]
    [InlineData("throws", true)]
    public async Task TemporarySetterSendsNothingWhenPreWriteReadFailsOnEveryModel(
        string failure,
        bool enabled)
    {
        foreach (ProductionBatteryReadEvidence evidence in TemporaryFullChargeEvidence())
        {
            HidSessionResult<ReadOnlyMemory<byte>> preWriteRead = failure switch
            {
                "rejected" => Success([0x10]),
                "failed" => new(default, HidSessionFailureKind.NativeIoFailure),
                _ => Success([0x00]),
            };
            var session = new ScriptedSession(preWriteRead, Success([0x00]), Success([0x04]))
            {
                BeforeResult = (call, _) =>
                {
                    if (failure == "throws" && call == 1)
                    {
                        throw new IOException("pre-write read transport failure");
                    }
                },
            };
            await using var port = CreatePort(session, TemporarySetterCatalog(), evidence);

            BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
                () => port.SetTemporaryFullChargeAsync(enabled, default));

            Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
            Assert.Contains(
                "pre-write read failed before submission",
                error.Message,
                StringComparison.Ordinal);
            RazerCommandSpec read = Assert.Single(session.Commands);
            Assert.Equal((0x07, 0x8F), (read.ClassId, read.CommandId));
        }
    }

    // Every evidence set that admits 07/8F: the generic production set, the exact
    // RZ09-0528 set, and the exact RZ09-0581 set. Power Mode Control decoding and
    // the pre-write guard are shared, not model-specific.
    private static IEnumerable<ProductionBatteryReadEvidence> TemporaryFullChargeEvidence() =>
    [
        AcceptedTemporaryReadEvidence(),
        ProductionBatteryReadEvidence.Production,
        BatteryValidationFactory.CreateRz090528ProductionBatteryReadEvidence(),
        ProductionBatteryReadEvidence.Rz090581ModelScoped,
    ];

    private static DeviceCommandCatalog TemporarySetterCatalog() => Catalog(
        Fixture(ProductionBatteryPort.TemporaryEnableAction, 0x07, 0x0F, [0x04]),
        Fixture(ProductionBatteryPort.TemporaryCancelAction, 0x07, 0x0F, [0x00]));

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

    [Theory]
    [InlineData(true, (int)DeviceStateQuery.CustomCpuPerformance, 3, "010100")]
    [InlineData(false, (int)DeviceStateQuery.CustomGpuPerformance, 4, "010200")]
    public async Task CustomPerformancePayloadFailure_ReportsExactComponentAndPoisonsFurtherGetters(
        bool failCpu,
        int expectedQuery,
        int expectedCommandCount,
        string expectedArguments)
    {
        var results = new List<HidSessionResult<ReadOnlyMemory<byte>>>
        {
            Success([0x01, 0x01, 0x04, 0x00]),
            Success([0x01, 0x02, 0x04, 0x00]),
            Success([0x01, 0x01, failCpu ? (byte)0x09 : (byte)0x01]),
        };
        if (!failCpu)
        {
            results.Add(Success([0x01, 0x02, 0x09]));
        }
        var session = new ScriptedSession([.. results]);
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        RazerProtocolException error = await Assert.ThrowsAsync<RazerProtocolException>(
            () => port.QueryPerformanceFanStateAsync(default));
        await Assert.ThrowsAsync<IOException>(() => port.QueryProtectionAsync(default));

        Assert.Equal(HidProtocolValidationFailure.PayloadValidator, error.ValidationFailure);
        Assert.Equal((DeviceStateQuery)expectedQuery, error.StateQuery);
        Assert.Equal(expectedCommandCount, session.Commands.Count);
        Assert.Equal(expectedArguments, Convert.ToHexString(session.Commands[^1].Arguments.Span));
    }

    [Fact]
    public async Task UnsupportedCustomPerformanceCombination_ReportsCombinationQueryAndPoisonsFurtherGetters()
    {
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x04, 0x00]),
            Success([0x01, 0x02, 0x04, 0x00]),
            Success([0x01, 0x01, 0x01]),
            Success([0x01, 0x02, 0x02]));
        var capabilities = new CustomPerformanceCapabilities(
            new HashSet<CustomPerformanceCombination>
            {
                new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low),
            });
        await using var port = CreatePort(
            session,
            Catalog(CustomPerformanceFixtures()),
            customPerformanceCapabilities: capabilities);

        RazerProtocolException error = await Assert.ThrowsAsync<RazerProtocolException>(
            () => port.QueryPerformanceFanStateAsync(default));
        await Assert.ThrowsAsync<IOException>(() => port.QueryProtectionAsync(default));

        Assert.Equal(HidProtocolValidationFailure.PayloadValidator, error.ValidationFailure);
        Assert.Equal(DeviceStateQuery.CustomPerformanceCombination, error.StateQuery);
        Assert.Equal("01 01 01 / 01 02 02", error.RejectedPayloadHex);
        Assert.Equal(4, session.Commands.Count);
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
    public async Task DirectCustomPerformanceSetter_ExitsManualAndPreservesLevelWrites()
    {
        CustomPerformanceSettings target = new(
            CustomPerformanceLevel.Medium,
            CustomPerformanceLevel.High);
        CapturedCommandFixture[] fixtures =
        [
            .. MaximumCapabilityFixtures(),
            .. CustomPerformanceFixtures(),
        ];
        var session = new ScriptedSession(
            Success([0x01, 0x01, 0x00, 0x01]),
            Success([0x01, 0x02, 0x00, 0x01]),
            Success([0x01, 0x01, 0x04, 0x00]),
            Success([0x01, 0x02, 0x04, 0x00]),
            Success([0x01, 0x01, 0x01]),
            Success([0x01, 0x02, 0x02]),
            Success([0x01, 0x01, 0x04, 0x00]),
            Success([0x01, 0x02, 0x04, 0x00]),
            Success([0x01, 0x01, 0x01]),
            Success([0x01, 0x02, 0x02]));
        await using var port = CreatePort(
            session,
            Catalog(fixtures),
            customPerformanceCapabilities: new CustomPerformanceCapabilities(
                new HashSet<CustomPerformanceCombination>
                {
                    new(target.Cpu, target.Gpu),
                }),
            supportsDirectManualFanTransitions: true);

        await port.SetCustomPerformanceAsync(target, default);

        Assert.Equal(
            [
                "01010000", "01020000",
                "01010400", "01020400", "010101", "010202",
                "01010000", "01020000", "010100", "010200",
            ],
            session.Commands.Select(command =>
                Convert.ToHexString(command.Arguments.Span)).ToArray());
        Assert.Equal(
            [0x82, 0x82, 0x02, 0x02, 0x07, 0x07, 0x82, 0x82, 0x87, 0x87],
            session.Commands.Select(command => command.CommandId).ToArray());
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
        IReadOnlySet<AdapterPowerState>? admittedAdapterPowerStates = null,
        CustomPerformanceCapabilities? customPerformanceCapabilities = null,
        bool supportsDirectManualFanTransitions = false) => new(
            session,
            catalog,
            evidence ?? NoTemporaryReadEvidence(),
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1),
            fanSpeedRange,
            admittedFanModes,
            admittedAdapterPowerStates,
            customPerformanceCapabilities,
            supportsDirectManualFanTransitions: supportsDirectManualFanTransitions);

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

    private static HidSessionResult<ReadOnlyMemory<byte>> ProtocolFailure(
        HidProtocolValidationFailure validationFailure) =>
        new(default, HidSessionFailureKind.ProtocolFailure)
        {
            Diagnostic = new HidSessionFailureDiagnostic(
                HidSessionFailurePhase.ProtocolValidation,
                HidSessionFailureKind.ProtocolFailure,
                NativeWin32Error: null,
                TransferredByteCount: null)
            {
                ProtocolValidationFailure = validationFailure,
            },
        };

    internal sealed class ScriptedSession(
        params HidSessionResult<ReadOnlyMemory<byte>>[] results) : IRazerCommandSession
    {
        private readonly Queue<HidSessionResult<ReadOnlyMemory<byte>>> results = new(results);

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public List<RazerCommandSpec> Commands { get; } = [];

        public Action<int, CancellationToken>? BeforeResult { get; init; }

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = assignTransactionId();
            Commands.Add(command);
            BeforeResult?.Invoke(Commands.Count, cancellationToken);
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

    private sealed class FixedHyperBoostEligibilitySource(
        CoolingPadConnectionState state) : IHyperBoostEligibilitySource
    {
        internal CoolingPadConnectionState State { get; set; } = state;
        public CoolingPadConnectionState ObserveCoolingPad() => State;
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
