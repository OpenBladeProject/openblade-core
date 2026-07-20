using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed class FunctionKeyPrimaryCommandClient
{
    private static readonly RazerCommandSpec QueryCommand = new(
        FunctionKeyPrimaryProtocol.CommandClass,
        FunctionKeyPrimaryProtocol.CommandId,
        [0x01, 0x00],
        2,
        static payload => FunctionKeyPrimaryProtocol.TryDecodeQuery(
            payload.Span,
            out _));

    private readonly IRazerCommandSession session;
    private readonly TimeSpan exchangeTimeout;

    internal FunctionKeyPrimaryCommandClient(
        IRazerCommandSession session,
        TimeSpan exchangeTimeout)
    {
        ArgumentNullException.ThrowIfNull(session);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.session = session;
        this.exchangeTimeout = exchangeTimeout;
    }

    public async Task<HidSessionResult<FunctionKeyPrimaryMode>> QueryAsync(
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

        return FunctionKeyPrimaryProtocol.TryDecodeQuery(
            response.Value.Span,
            out FunctionKeyPrimaryMode mode)
            ? new(mode, null)
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
