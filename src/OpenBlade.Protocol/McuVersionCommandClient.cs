namespace OpenBlade.Protocol;

internal sealed class McuVersionCommandClient
{
    private static readonly RazerCommandSpec QueryCommand = new(
        McuVersionProtocol.CommandClass,
        McuVersionProtocol.CommandId,
        [0x00, 0x00, 0x00, 0x00],
        McuVersionProtocol.PayloadLength,
        static payload => McuVersionProtocol.TryDecodeQuery(payload.Span, out _));

    private readonly IRazerCommandSession session;
    private readonly TimeSpan exchangeTimeout;

    internal McuVersionCommandClient(
        IRazerCommandSession session,
        TimeSpan exchangeTimeout)
    {
        ArgumentNullException.ThrowIfNull(session);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.session = session;
        this.exchangeTimeout = exchangeTimeout;
    }

    public async Task<HidSessionResult<string?>> QueryAsync(
        byte transactionId,
        CancellationToken cancellationToken)
    {
        TransactionIdAssignment assignment =
            TransactionIdAssignment.Valid(transactionId);
        HidSessionResult<ReadOnlyMemory<byte>> response = await session.ExecuteAsync(
            QueryCommand,
            () => assignment,
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);

        if (response.Failure is not null)
        {
            return new(null, response.Failure)
            {
                Diagnostic = response.Diagnostic,
            };
        }

        return McuVersionProtocol.TryDecodeQuery(response.Value.Span, out string version)
            ? new(version, null)
            : new(null, HidSessionFailureKind.ProtocolFailure)
            {
                Diagnostic = new HidSessionFailureDiagnostic(
                    HidSessionFailurePhase.ProtocolValidation,
                    HidSessionFailureKind.ProtocolFailure,
                    NativeWin32Error: null,
                    TransferredByteCount: null)
                {
                    ProtocolValidationFailure =
                        HidProtocolValidationFailure.PayloadValidator,
                },
            };
    }
}
