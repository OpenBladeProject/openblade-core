using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed class AdapterPowerCommandClient
{
    private static readonly RazerCommandSpec QueryCommand = new(
        AdapterPowerProtocol.CommandClass,
        AdapterPowerProtocol.CommandId,
        [0x00, 0x00],
        2,
        static payload => AdapterPowerProtocol.TryDecodeQuery(payload.Span, out _));

    private readonly IRazerCommandSession session;
    private readonly TimeSpan exchangeTimeout;

    internal AdapterPowerCommandClient(
        IRazerCommandSession session,
        TimeSpan exchangeTimeout)
    {
        ArgumentNullException.ThrowIfNull(session);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.session = session;
        this.exchangeTimeout = exchangeTimeout;
    }

    public async Task<HidSessionResult<AdapterPowerState>> QueryAsync(
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
                NativeRetirement = response.NativeRetirement,
            };
        }

        if (AdapterPowerProtocol.TryDecodeQuery(response.Value.Span, out AdapterPowerState observed)
            && observed == AdapterPowerState.Unknown)
        {
            session.RetainQueryFailure(AdapterPowerProtocol.CommandClass,
                AdapterPowerProtocol.CommandId, FirmwareQueryOutcome.UnrecognizedValue);
        }

        return AdapterPowerProtocol.TryDecodeQuery(
            response.Value.Span,
            out AdapterPowerState state)
            ? new(state, null)
            : new(AdapterPowerState.Unknown, HidSessionFailureKind.ProtocolFailure)
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
