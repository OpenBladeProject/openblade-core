namespace OpenBlade.Protocol;

internal sealed class StartupAnimationCommandClient
{
    private static readonly RazerCommandSpec QueryCommand = new(
        StartupAnimationProtocol.CommandClass,
        StartupAnimationProtocol.CommandId,
        [0x00],
        1,
        static payload => StartupAnimationProtocol.TryDecodeQuery(
            payload.Span,
            out _));

    private readonly IRazerCommandSession session;
    private readonly TimeSpan exchangeTimeout;

    internal StartupAnimationCommandClient(
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
            return new(false, response.Failure)
            {
                Diagnostic = response.Diagnostic,
            };
        }

        return StartupAnimationProtocol.TryDecodeQuery(
            response.Value.Span,
            out bool enabled)
            ? new(enabled, null)
            : new(false, HidSessionFailureKind.ProtocolFailure)
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
