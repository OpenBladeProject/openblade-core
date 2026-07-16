namespace OpenBlade.Protocol;

internal interface ITemporaryFullChargeCommandClient
{
    Task<HidSessionResult<bool>> QueryAsync(
        byte transactionId,
        CancellationToken cancellationToken);
}

internal sealed class TemporaryFullChargeCommandClient : ITemporaryFullChargeCommandClient
{
    private static readonly RazerCommandSpec QueryCommand = new(
        0x07,
        0x8F,
        [0x00],
        1,
        static payload => TemporaryFullChargeProtocol.TryDecodeQuery(payload.Span, out _));

    private readonly IRazerCommandSession session;
    private readonly TimeSpan exchangeTimeout;

    internal TemporaryFullChargeCommandClient(
        IRazerCommandSession session,
        TimeSpan exchangeTimeout)
    {
        ArgumentNullException.ThrowIfNull(session);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.session = session;
        this.exchangeTimeout = exchangeTimeout;
    }

    public async Task<HidSessionResult<bool>> QueryAsync(
        byte transactionId,
        CancellationToken cancellationToken)
    {
        TransactionIdAssignment assignment = TransactionIdAssignment.Valid(transactionId);
        HidSessionResult<ReadOnlyMemory<byte>> response = await session.ExecuteAsync(
            QueryCommand,
            () => assignment,
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);

        if (response.Failure is not null)
        {
            return new(default, response.Failure) { Diagnostic = response.Diagnostic };
        }

        return TemporaryFullChargeProtocol.TryDecodeQuery(response.Value.Span, out bool active)
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
                },
            };
    }
}
