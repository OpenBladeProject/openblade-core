using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadFanGetterProbeTests
{
    [Fact]
    public void Target_IsExactBios400HostAndExactPadCollection()
    {
        ExpectedHostIdentity host = CoolingPadFanGetterProbeTarget.HostIdentity;
        HidHandleIdentity pad = CoolingPadFanGetterProbeTarget.PadHidIdentity;

        Assert.Equal("Razer", host.Manufacturer);
        Assert.Equal("Blade 16 - RZ09-0581", host.ProductName);
        Assert.Equal("RZ09-05819EN4", host.Sku);
        Assert.Equal(["4.00"], host.ValidatedBiosVersions);
        Assert.Equal(FirmwareAdmissionScope.ExactVersions, host.FirmwareAdmission);
        Assert.Equal(0x1532, pad.VendorId);
        Assert.Equal(0x0F43, pad.ProductId);
        Assert.Equal(0x000C, pad.UsagePage);
        Assert.Equal(0x0001, pad.Usage);
        Assert.Equal(91, pad.FeatureReportByteLength);
        Assert.Equal((ushort)0x0200, pad.VersionNumber);
        Assert.Equal(
            ControllerExclusivityPolicy.Strict,
            CoolingPadFanGetterProbeTarget.ExclusivityPolicy);
    }

    [Theory]
    [InlineData(0, 0x81, 0x8B)]
    [InlineData(1, 0x88, 0x82)]
    [InlineData(2, 0x90, 0x9E)]
    [InlineData(3, 0x90, 0x9F)]
    public async Task Runner_QueriesOneTypedPayloadOnceThenDisposes(
        int kindValue,
        byte expectedCommandId,
        byte expectedChecksum)
    {
        var kind = (CoolingPadFanGetterKind)kindValue;
        byte[] response = kind switch
        {
            CoolingPadFanGetterKind.ConfiguredSpeed
                or CoolingPadFanGetterKind.CurrentSpeed => [0x01, 0x05, 0x2A],
            CoolingPadFanGetterKind.FirmwareCoolingMode => [0x00, 0x02],
            CoolingPadFanGetterKind.SynapseCoolingMode => [0x01, 0x04, 0x2C],
            _ => throw new ArgumentOutOfRangeException(nameof(kindValue)),
        };
        byte[] expectedArguments = kind switch
        {
            CoolingPadFanGetterKind.ConfiguredSpeed
                or CoolingPadFanGetterKind.CurrentSpeed => [0x01, 0x05, 0x00],
            CoolingPadFanGetterKind.FirmwareCoolingMode => [0x00, 0x00, 0x00],
            CoolingPadFanGetterKind.SynapseCoolingMode => [0x01, 0x00, 0x00],
            _ => throw new ArgumentOutOfRangeException(nameof(kindValue)),
        };
        var session = new RecordingSession(response);
        var runner = CreateRunner(session, kind);

        HidSessionResult<GetterCandidateProbeSnapshot> result =
            await runner.RunAsync(default);

        Assert.Null(result.Failure);
        Assert.Equal(Convert.ToHexString(response), result.Value!.ResponsePayloadHex);
        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal(0x0D, command.ClassId);
        Assert.Equal(expectedCommandId, command.CommandId);
        Assert.Equal(expectedArguments, command.Arguments.ToArray());
        Assert.Equal(response.Length, command.ExpectedPayloadLength);
        Assert.Equal(1, session.DisposeCount);

        byte[] envelope = RazerReport.Create(
            transactionId: 1,
            commandClass: 0x0D,
            expectedCommandId,
            expectedArguments).Encode();
        byte[] featureReport = new byte[RazerReport.ReportLength + 1];
        featureReport[0] = 0x05;
        envelope.CopyTo(featureReport, 1);
        Assert.Equal(91, featureReport.Length);
        Assert.Equal(0x05, featureReport[0]);
        Assert.Equal(expectedChecksum, featureReport[89]);

        HidSessionResult<GetterCandidateProbeSnapshot> second =
            await runner.RunAsync(default);
        Assert.Equal(HidSessionFailureKind.Disposed, second.Failure);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task Runner_RejectsEchoMismatch()
    {
        var session = new RecordingSession([0x00, 0x05, 0x2A]);

        HidSessionResult<GetterCandidateProbeSnapshot> result =
            await CreateRunner(
                session,
                CoolingPadFanGetterKind.ConfiguredSpeed).RunAsync(default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(
            HidProtocolValidationFailure.PayloadValidator,
            result.Diagnostic?.ProtocolValidationFailure);
        Assert.Single(session.Commands);
        Assert.Equal(1, session.DisposeCount);
    }

    [Theory]
    [InlineData(2, 0x01, 0x00)]
    [InlineData(2, 0x00, 0x07)]
    [InlineData(3, 0x00, 0x04)]
    [InlineData(3, 0x01, 0x07)]
    public async Task CoolingModeRunner_RejectsSelectorOrModeMismatch(
        int kindValue,
        byte returnedSelector,
        byte returnedMode)
    {
        byte[] response = (CoolingPadFanGetterKind)kindValue switch
        {
            CoolingPadFanGetterKind.FirmwareCoolingMode =>
                [returnedSelector, returnedMode],
            _ => [returnedSelector, returnedMode, 0x2C],
        };
        var session = new RecordingSession(response);

        HidSessionResult<GetterCandidateProbeSnapshot> result =
            await CreateRunner(
                session,
                (CoolingPadFanGetterKind)kindValue).RunAsync(default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(
            HidProtocolValidationFailure.PayloadValidator,
            result.Diagnostic?.ProtocolValidationFailure);
        Assert.Single(session.Commands);
        Assert.Equal(1, session.DisposeCount);
    }

    private static CoolingPadFanGetterProbeRunner CreateRunner(
        IRazerCommandSession session,
        CoolingPadFanGetterKind kind) => new(
        static () => new ValidationRunContext(),
        new ActorFactory(new ValidationActor(
            session,
            new UnsupportedBatteryCommands(),
            new UnsupportedTemporaryCommands())),
        TimeProvider.System,
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        kind,
        new PendingDisposalRegistry());

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
    }

    private sealed class RecordingSession(byte[] response) : IRazerCommandSession
    {
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal int DisposeCount { get; private set; }

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            Assert.Equal(1, assignTransactionId().TransactionId);
            Commands.Add(command);
            if (response.Length != command.ExpectedPayloadLength)
            {
                return Task.FromResult(
                    new HidSessionResult<ReadOnlyMemory<byte>>(
                        default,
                        HidSessionFailureKind.ProtocolFailure)
                    {
                        Diagnostic = new HidSessionFailureDiagnostic(
                            HidSessionFailurePhase.ProtocolValidation,
                            HidSessionFailureKind.ProtocolFailure,
                            null,
                            null)
                        {
                            ProtocolValidationFailure =
                                HidProtocolValidationFailure.PayloadLength,
                        },
                    });
            }
            if (command.PayloadValidator is not null
                && !command.PayloadValidator(response))
            {
                return Task.FromResult(
                    new HidSessionResult<ReadOnlyMemory<byte>>(
                        default,
                        HidSessionFailureKind.ProtocolFailure)
                    {
                        Diagnostic = new HidSessionFailureDiagnostic(
                            HidSessionFailurePhase.ProtocolValidation,
                            HidSessionFailureKind.ProtocolFailure,
                            null,
                            null)
                        {
                            ProtocolValidationFailure =
                                HidProtocolValidationFailure.PayloadValidator,
                        },
                    });
            }

            return Task.FromResult(
                new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }
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
        public Task<HidSessionResult<bool>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            bool enabled,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
