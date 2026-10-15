using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class DirectThermalTransitionValidationRunnerTests
{
    [Fact]
    public async Task ManualTargetRoundTrip_UsesTargetOnlyPairsWithoutBalancedBridge()
    {
        var session = new StatefulSession();
        var safety = new RecordingSafetyGate();
        var runner = CreateRunner(session, safety);

        DirectThermalTransitionValidationResult result = await runner.RunAsync(
            DirectThermalTransitionScenario.ManualTargetRoundTrip,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(10, result.SafetyChecks);
        Assert.Equal(10, result.WritesAttempted);
        Assert.Equal(
            [
                "01010001", "01020001", "010136", "010236",
                "010128", "010228",
                "010136", "010236",
                "01010000", "01020000",
            ],
            session.Writes);
        Assert.Equal(new FanRpmReadings(4000, 4000), result.CandidateFanRpm);
        Assert.Equal(new FanRpmReadings(5400, 5400), result.ReturnFanRpm);
        Assert.Equal(10, safety.Calls);
    }

    [Fact]
    public async Task ManualToCustomAutomatic_PreservesCapturedCustomLevels()
    {
        var session = new StatefulSession
        {
            Cpu = CustomPerformanceLevel.Medium,
            Gpu = CustomPerformanceLevel.High,
        };
        var safety = new RecordingSafetyGate();
        var runner = CreateRunner(session, safety);

        DirectThermalTransitionValidationResult result = await runner.RunAsync(
            DirectThermalTransitionScenario.ManualToCustomAutomatic,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new CustomPerformanceSettings(
            CustomPerformanceLevel.Medium,
            CustomPerformanceLevel.High), result.BaselineCustomPerformance);
        Assert.Equal(result.BaselineCustomPerformance, result.CandidateCustomPerformance);
        Assert.Equal(result.BaselineCustomPerformance, result.RestorationCustomPerformance);
        Assert.Equal(
            [
                "01010001", "01020001", "010136", "010236",
                "01010400", "01020400", "010101", "010202",
                "01010000", "01020000",
            ],
            session.Writes);
        Assert.DoesNotContain("01010000", session.Writes.Take(8));
        Assert.Equal(10, safety.Calls);
    }

    [Fact]
    public async Task ManualToHyperBoost_UsesPadAwareGateForEveryWrite()
    {
        var session = new StatefulSession();
        var ordinarySafety = new RecordingSafetyGate();
        var hyperBoostSafety = new RecordingSafetyGate();
        var runner = CreateRunner(session, ordinarySafety, hyperBoostSafety);

        DirectThermalTransitionValidationResult result = await runner.RunAsync(
            DirectThermalTransitionScenario.ManualToHyperBoost,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(
            [
                "01010001", "01020001", "010136", "010236",
                "01010700", "01020700",
                "01010000", "01020000",
            ],
            session.Writes);
        Assert.DoesNotContain("01010000", session.Writes.Take(6));
        Assert.Equal(0, ordinarySafety.Calls);
        Assert.Equal(8, hyperBoostSafety.Calls);
    }

    [Fact]
    public async Task TargetWriteFailure_StopsWithoutRestorationOrReplay()
    {
        var session = new StatefulSession { FailWriteNumber = 5 };
        var safety = new RecordingSafetyGate();
        var runner = CreateRunner(session, safety);

        DirectThermalTransitionValidationResult result = await runner.RunAsync(
            DirectThermalTransitionScenario.ManualTargetRoundTrip,
            CancellationToken.None);

        Assert.Equal(
            DirectThermalTransitionFailure.TargetWriteIndeterminate,
            result.Failure);
        Assert.True(result.IndeterminatePartialWrite);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.Restored);
        Assert.Equal(5, result.WritesAttempted);
        Assert.Equal(
            ["01010001", "01020001", "010136", "010236", "010128"],
            session.Writes);
    }

    [Fact]
    public async Task SafetyLossBeforeTarget_StopsBeforeAnotherSubmission()
    {
        var session = new StatefulSession();
        var safety = new RecordingSafetyGate(failOnCall: 5);
        var runner = CreateRunner(session, safety);

        DirectThermalTransitionValidationResult result = await runner.RunAsync(
            DirectThermalTransitionScenario.ManualTargetRoundTrip,
            CancellationToken.None);

        Assert.Equal(
            DirectThermalTransitionFailure.SafetyAdmissionLost,
            result.Failure);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.IndeterminatePartialWrite);
        Assert.Equal(4, result.WritesAttempted);
        Assert.Equal(5, result.SafetyChecks);
        Assert.Equal(
            ["01010001", "01020001", "010136", "010236"],
            session.Writes);
    }

    [Fact]
    public async Task ManualStateLostAfterRpmSampling_StopsBeforeTargetWrite()
    {
        var session = new StatefulSession();
        session.AfterCompletedRpmSample = count =>
        {
            if (count == 3)
            {
                session.SetThermalMode(0x00, 0x00);
            }
        };
        var runner = CreateRunner(session, new RecordingSafetyGate());

        DirectThermalTransitionValidationResult result = await runner.RunAsync(
            DirectThermalTransitionScenario.ManualTargetRoundTrip,
            CancellationToken.None);

        Assert.Equal(
            DirectThermalTransitionFailure.TargetPreconditionMismatch,
            result.Failure);
        Assert.Equal(4, result.WritesAttempted);
        Assert.Equal(
            ["01010001", "01020001", "010136", "010236"],
            session.Writes);
    }

    private static DirectThermalTransitionValidationRunner CreateRunner(
        IRazerCommandSession session,
        IPerformanceFanValidationSafetyGate safety,
        IPerformanceFanValidationSafetyGate? hyperBoostSafety = null) => new(
            new ActorFactory(new ValidationActor(
                session,
                new UnsupportedBatteryCommands(),
                new UnsupportedTemporaryCommands())),
            safety,
            CreateCapturedCatalog(),
            CreateHyperBoostCatalog(),
            TimeProvider.System,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            hyperBoostSafety,
            new PendingDisposalRegistry(),
            rpmSampleInterval: TimeSpan.Zero,
            maximumRpmSamples: 3,
            honorFanInterCommandTiming: false);

    private static DeviceCommandCatalog CreateCapturedCatalog()
    {
        DeviceIdentity target = Target();
        return DeviceCommandCatalog.Create(target,
        [
            Fixture(target, ProductionBatteryPort.FanMaximumStateFirstAction,
                0x0D, 0x02, "01010001"),
            Fixture(target, ProductionBatteryPort.FanMaximumStateSecondAction,
                0x0D, 0x02, "01020001"),
            Fixture(target, ProductionBatteryPort.FanMaximumSpeedFirstAction,
                0x0D, 0x01, "010136"),
            Fixture(target, ProductionBatteryPort.FanMaximumSpeedSecondAction,
                0x0D, 0x01, "010236"),
            Fixture(target, ProductionBatteryPort.PerformanceBalancedFirstAction,
                0x0D, 0x02, "01010000"),
            Fixture(target, ProductionBatteryPort.PerformanceBalancedSecondAction,
                0x0D, 0x02, "01020000"),
            Fixture(target, ProductionBatteryPort.PerformanceCustomFirstAction,
                0x0D, 0x02, "01010400"),
            Fixture(target, ProductionBatteryPort.PerformanceCustomSecondAction,
                0x0D, 0x02, "01020400"),
            Fixture(target, ProductionBatteryPort.CustomCpuLowAction,
                0x0D, 0x07, "010100"),
            Fixture(target, ProductionBatteryPort.CustomCpuMediumAction,
                0x0D, 0x07, "010101"),
            Fixture(target, ProductionBatteryPort.CustomCpuHighAction,
                0x0D, 0x07, "010102"),
            Fixture(target, ProductionBatteryPort.CustomGpuLowAction,
                0x0D, 0x07, "010200"),
            Fixture(target, ProductionBatteryPort.CustomGpuMediumAction,
                0x0D, 0x07, "010201"),
            Fixture(target, ProductionBatteryPort.CustomGpuHighAction,
                0x0D, 0x07, "010202"),
        ]);
    }

    private static DeviceCommandCatalog CreateHyperBoostCatalog()
    {
        DeviceIdentity target = Target();
        return DeviceCommandCatalog.Create(target,
        [
            Fixture(target, ProductionBatteryPort.HyperBoostEnableFirstAction,
                0x0D, 0x02, "01010700"),
            Fixture(target, ProductionBatteryPort.HyperBoostEnableSecondAction,
                0x0D, 0x02, "01020700"),
        ]);
    }

    private static DeviceIdentity Target() => new(
        TargetBladeDevice.Model,
        TargetBladeDevice.VendorId,
        TargetBladeDevice.ProductId,
        "4.00");

    private static CapturedCommandFixture Fixture(
        DeviceIdentity target,
        string action,
        byte commandClass,
        byte commandId,
        string payload) => new(
            target,
            action,
            commandClass,
            commandId,
            Convert.FromHexString(payload),
            Captured: true,
            Decoded: true,
            Applied: true,
            ReadBack: true,
            Restored: true);

    private sealed class RecordingSafetyGate(int? failOnCall = null)
        : IPerformanceFanValidationSafetyGate
    {
        internal int Calls { get; private set; }

        public Task<PerformanceFanValidationSafetyGateResult> ValidateAsync(
            ValidationRunContext context,
            IRazerCommandSession session,
            CancellationToken cancellationToken)
        {
            Calls++;
            bool success = Calls != failOnCall;
            return Task.FromResult(new PerformanceFanValidationSafetyGateResult(
                FullPowerAdapterConfirmed: success,
                CoolingPadAbsentConfirmed: success,
                CoolingPadPresentConfirmed: false,
                CoolingPadOpaqueZeroConfirmed: false,
                success
                    ? PerformanceFanValidationSafetyFailure.None
                    : PerformanceFanValidationSafetyFailure.FullPowerAdapterRequired)
            {
                AdapterPowerState = success
                    ? AdapterPowerState.FullPower330W
                    : AdapterPowerState.Battery,
                CoolingPadNotRequired = true,
            });
        }
    }

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
    }

    private sealed class StatefulSession : IRazerCommandSession
    {
        private readonly byte[][] thermal =
        [
            Convert.FromHexString("01010000"),
            Convert.FromHexString("01020000"),
        ];
        private readonly int[] fanRpm = [5400, 5400];
        private int writeCount;
        private int completedRpmSamples;

        internal int? FailWriteNumber { get; init; }
        internal CustomPerformanceLevel Cpu { get; set; } =
            CustomPerformanceLevel.Medium;
        internal CustomPerformanceLevel Gpu { get; set; } =
            CustomPerformanceLevel.High;
        internal List<string> Writes { get; } = [];
        internal Action<int>? AfterCompletedRpmSample { get; set; }

        internal void SetThermalMode(byte mode, byte fanValue)
        {
            thermal[0] = [0x01, 0x01, mode, fanValue];
            thermal[1] = [0x01, 0x02, mode, fanValue];
        }

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            _ = assignTransactionId();
            ReadOnlySpan<byte> arguments = command.Arguments.Span;
            if (command.CommandId == ThermalModeProtocol.QueryCommandId)
            {
                return Success(thermal[arguments[1] - 1]);
            }
            if (command.CommandId == CustomPerformanceProtocol.QueryCommandId)
            {
                byte domain = arguments[1];
                byte value = (byte)(domain == (byte)CustomPerformanceDomain.Cpu
                    ? Cpu : Gpu);
                return Success([0x01, domain, value]);
            }
            if (command.CommandId == 0x88)
            {
                int index = arguments[1] - 1;
                if (index == 1)
                {
                    completedRpmSamples++;
                    AfterCompletedRpmSample?.Invoke(completedRpmSamples);
                }
                return Success([0x01, arguments[1], checked((byte)(fanRpm[index] / 100))]);
            }

            writeCount++;
            byte[] payload = command.Arguments.ToArray();
            Writes.Add(Convert.ToHexString(payload));
            if (writeCount == FailWriteNumber)
            {
                return Failure(HidSessionFailureKind.Timeout);
            }
            if (command.CommandId == 0x02)
            {
                thermal[payload[1] - 1] = payload;
            }
            else if (command.CommandId == 0x01)
            {
                fanRpm[payload[1] - 1] = payload[2] * 100;
            }
            else if (command.CommandId == CustomPerformanceProtocol.SetCommandId)
            {
                if (payload[1] == (byte)CustomPerformanceDomain.Cpu)
                {
                    Cpu = (CustomPerformanceLevel)payload[2];
                }
                else
                {
                    Gpu = (CustomPerformanceLevel)payload[2];
                }
            }

            return Success(payload);
        }

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }

        private static Task<HidSessionResult<ReadOnlyMemory<byte>>> Success(
            byte[] payload) => Task.FromResult(
                new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));

        private static Task<HidSessionResult<ReadOnlyMemory<byte>>> Failure(
            HidSessionFailureKind failure) => Task.FromResult(
                new HidSessionResult<ReadOnlyMemory<byte>>(default, failure));
    }

    private sealed class UnsupportedBatteryCommands : IBatteryCommandClient
    {
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            BatteryValidationSetting setting,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnsupportedTemporaryCommands
        : ITemporaryFullChargeCommandClient
    {
        public Task<HidSessionResult<TemporaryFullChargeReading>> QueryReadingAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<bool>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            bool enabled,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
