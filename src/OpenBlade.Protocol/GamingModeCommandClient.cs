namespace OpenBlade.Protocol;

internal sealed class GamingModeCommandClient
{
    private static readonly RazerCommandSpec QueryCommand = new(
        GamingModeProtocol.CommandClass,
        GamingModeProtocol.CommandId,
        [0x00, 0x00, 0x00, 0x00],
        4,
        static payload => GamingModeProtocol.TryDecodeQuery(payload.Span, out _));

    private readonly IRazerCommandSession session;
    private readonly TimeSpan exchangeTimeout;

    internal GamingModeCommandClient(
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
        TransactionIdAssignment assignment =
            TransactionIdAssignment.Valid(transactionId);
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

        return GamingModeProtocol.TryDecodeQuery(response.Value.Span, out bool enabled)
            ? new(enabled, null)
            : new(default, HidSessionFailureKind.ProtocolFailure)
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
