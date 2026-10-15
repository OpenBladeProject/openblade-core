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

/// <summary>
/// A decoded 07/8F reading. <see cref="OtherSettings"/> holds the
/// non-temporary-full-charge bits, which are nonzero only on the exact RZ09-0581.
/// </summary>
internal readonly record struct TemporaryFullChargeReading(bool Active, byte OtherSettings);

internal sealed class TemporaryFullChargeCommandClient : ITemporaryFullChargeCommandClient
{
    private readonly RazerCommandSpec queryCommand;
    private readonly bool decodesPowerModeControlBitmask;
    private readonly IRazerCommandSession session;
    private readonly TimeSpan exchangeTimeout;

    internal TemporaryFullChargeCommandClient(
        IRazerCommandSession session,
        TimeSpan exchangeTimeout,
        bool decodesPowerModeControlBitmask = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.session = session;
        this.exchangeTimeout = exchangeTimeout;
        this.decodesPowerModeControlBitmask = decodesPowerModeControlBitmask;
        queryCommand = new RazerCommandSpec(
            0x07,
            0x8F,
            [0x00],
            1,
            payload => TemporaryFullChargeProtocol.TryDecodeQuery(
                payload.Span,
                out _,
                decodesPowerModeControlBitmask));
    }

    public async Task<HidSessionResult<bool>> QueryAsync(
        byte transactionId,
        CancellationToken cancellationToken)
    {
        HidSessionResult<TemporaryFullChargeReading> reading =
            await QueryReadingAsync(transactionId, cancellationToken).ConfigureAwait(false);
        return reading.Failure is null
            ? new(reading.Value.Active, null)
            : new(default, reading.Failure) { Diagnostic = reading.Diagnostic };
    }

    internal async Task<HidSessionResult<TemporaryFullChargeReading>> QueryReadingAsync(
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
            out byte otherSettings,
            decodesPowerModeControlBitmask)
            ? new(new TemporaryFullChargeReading(active, otherSettings), null)
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
