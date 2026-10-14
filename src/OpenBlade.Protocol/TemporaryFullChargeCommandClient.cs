namespace OpenBlade.Protocol;

internal interface ITemporaryFullChargeCommandClient
{
    Task<HidSessionResult<bool>> QueryAsync(
        byte transactionId,
        CancellationToken cancellationToken);

    Task<HidSessionResult<HidUnit>> SetAsync(
        byte transactionId,
        bool enabled,
        CancellationToken cancellationToken);
}

internal sealed class TemporaryFullChargeCommandClient : ITemporaryFullChargeCommandClient
{

    private readonly RazerCommandSpec queryCommand;
    private readonly bool acceptCompletedMarker;
    private readonly IRazerCommandSession session;
    private readonly TimeSpan exchangeTimeout;

    internal TemporaryFullChargeCommandClient(
        IRazerCommandSession session,
        TimeSpan exchangeTimeout,
        bool acceptCompletedMarker = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.session = session;
        this.exchangeTimeout = exchangeTimeout;
        this.acceptCompletedMarker = acceptCompletedMarker;
        queryCommand = new RazerCommandSpec(
            0x07,
            0x8F,
            [0x00],
            1,
            payload => TemporaryFullChargeProtocol.TryDecodeQuery(
                payload.Span,
                out _,
                acceptCompletedMarker));
    }

    public async Task<HidSessionResult<bool>> QueryAsync(
        byte transactionId,
        CancellationToken cancellationToken)
    {
        TransactionIdAssignment assignment = TransactionIdAssignment.Valid(transactionId);
        HidSessionResult<ReadOnlyMemory<byte>> response = await session.ExecuteAsync(
            queryCommand,
            () => assignment,
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);

        if (response.Failure is not null)
        {
            return new(default, response.Failure) { Diagnostic = response.Diagnostic };
        }

        return TemporaryFullChargeProtocol.TryDecodeQuery(
            response.Value.Span,
            out bool active,
            acceptCompletedMarker)
            ? new(active, null)
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

    public async Task<HidSessionResult<HidUnit>> SetAsync(
        byte transactionId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        byte payload = TemporaryFullChargeProtocol.Encode(enabled);
        TransactionIdAssignment assignment = TransactionIdAssignment.Valid(transactionId);
        HidSessionResult<ReadOnlyMemory<byte>> response = await session.ExecuteAsync(
            new RazerCommandSpec(
                0x07,
                0x0F,
                [payload],
                1,
                acknowledgement =>
                    acknowledgement.Length == 1 && acknowledgement.Span[0] == payload),
            () => assignment,
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);

        if (response.Failure is not null)
        {
            return new(default, response.Failure) { Diagnostic = response.Diagnostic };
        }

        return response.Value.Length == 1 && response.Value.Span[0] == payload
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
