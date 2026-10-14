namespace OpenBlade.Protocol;

internal interface IBatteryCommandClient
{
    Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
        byte transactionId,
        CancellationToken cancellationToken);

    Task<HidSessionResult<HidUnit>> SetAsync(
        byte transactionId,
        BatteryValidationSetting setting,
        CancellationToken cancellationToken);
}

internal sealed class BatteryCommandClient : IBatteryCommandClient
{
    private static readonly RazerCommandSpec QueryCommand = new(
        0x07,
        0x92,
        [0x00],
        1,
        static payload => BatteryValidationProtocol.TryDecodeQuery(
            payload.Span,
            out _));

    private readonly IRazerCommandSession session;
    private readonly TimeSpan exchangeTimeout;

    internal BatteryCommandClient(IRazerCommandSession session, TimeSpan exchangeTimeout)
    {
        ArgumentNullException.ThrowIfNull(session);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);

        this.session = session;
        this.exchangeTimeout = exchangeTimeout;
    }

    public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
        byte transactionId,
        CancellationToken cancellationToken)
    {
        TransactionIdAssignment assignment = TransactionIdAssignment.Valid(transactionId);
        return QueryCoreAsync(assignment, cancellationToken);
    }

    public Task<HidSessionResult<HidUnit>> SetAsync(
        byte transactionId,
        BatteryValidationSetting setting,
        CancellationToken cancellationToken)
    {
        TransactionIdAssignment assignment = TransactionIdAssignment.Valid(transactionId);
        byte payload = BatteryValidationProtocol.Encode(setting);
        return SetCoreAsync(assignment, payload, cancellationToken);
    }

    private async Task<HidSessionResult<BatteryValidationSetting>> QueryCoreAsync(
        TransactionIdAssignment assignment,
        CancellationToken cancellationToken)
    {
        HidSessionResult<ReadOnlyMemory<byte>> response = await session.ExecuteAsync(
            QueryCommand,
            () => assignment,
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);

        if (response.Failure is not null)
        {
            return new(default, response.Failure)
            {
                Diagnostic = response.Diagnostic,
            };
        }

        return BatteryValidationProtocol.TryDecodeQuery(response.Value.Span, out BatteryValidationSetting setting)
            ? new(setting, null)
            : new(default, HidSessionFailureKind.ProtocolFailure)
            {
                Diagnostic = new HidSessionFailureDiagnostic(
                    HidSessionFailurePhase.ProtocolValidation,
                    HidSessionFailureKind.ProtocolFailure,
                    NativeWin32Error: null,
                    TransferredByteCount: null)
                {
                    ProtocolValidationFailure = HidProtocolValidationFailure.PayloadValidator,
                    RejectedPayloadHex =
                        DeviceStateContentRejection.FormatPayload(response.Value.Span),
                },
            };
    }

    private async Task<HidSessionResult<HidUnit>> SetCoreAsync(
        TransactionIdAssignment assignment,
        byte payload,
        CancellationToken cancellationToken)
    {
        HidSessionResult<ReadOnlyMemory<byte>> response = await session.ExecuteAsync(
            new RazerCommandSpec(
                0x07,
                0x12,
                [payload],
                1,
                acknowledgement =>
                    acknowledgement.Length == 1 && acknowledgement.Span[0] == payload),
            () => assignment,
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);

        if (response.Failure is not null)
        {
            return new(default, response.Failure)
            {
                Diagnostic = response.Diagnostic,
            };
        }

        ReadOnlySpan<byte> acknowledgement = response.Value.Span;
        return acknowledgement.Length == 1 && acknowledgement[0] == payload
            ? new(new HidUnit(), null)
            : new(default, HidSessionFailureKind.ProtocolFailure)
            {
                Diagnostic = new HidSessionFailureDiagnostic(
                    HidSessionFailurePhase.ProtocolValidation,
                    HidSessionFailureKind.ProtocolFailure,
                    NativeWin32Error: null,
                    TransferredByteCount: null)
                {
                    ProtocolValidationFailure = HidProtocolValidationFailure.PayloadValidator,
                },
            };
    }
}
