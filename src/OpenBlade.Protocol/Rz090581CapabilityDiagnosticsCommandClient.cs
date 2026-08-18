using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed class Rz090581CapabilityDiagnosticsCommandClient
{
    private static readonly RazerCommandSpec SkuHardwareConfigurationQuery = new(
        Rz090581CapabilityDiagnosticsProtocol.CommandClass,
        Rz090581CapabilityDiagnosticsProtocol.SkuHardwareConfigurationCommandId,
        [0x00],
        Rz090581CapabilityDiagnosticsProtocol.SingleBytePayloadLength,
        static payload => Rz090581CapabilityDiagnosticsProtocol.TryDecodeRawByte(
            payload.Span,
            out _));

    private static readonly RazerCommandSpec MiniLedPanelResolutionQuery = new(
        Rz090581CapabilityDiagnosticsProtocol.CommandClass,
        Rz090581CapabilityDiagnosticsProtocol.MiniLedPanelResolutionCommandId,
        [0x00],
        Rz090581CapabilityDiagnosticsProtocol.SingleBytePayloadLength,
        static payload => Rz090581CapabilityDiagnosticsProtocol.TryDecodeRawByte(
            payload.Span,
            out _));

    private static readonly RazerCommandSpec ThermalFanIdListQuery = new(
        Rz090581CapabilityDiagnosticsProtocol.CommandClass,
        Rz090581CapabilityDiagnosticsProtocol.ThermalFanIdListCommandId,
        new byte[Rz090581CapabilityDiagnosticsProtocol.ThermalFanIdListPayloadLength],
        Rz090581CapabilityDiagnosticsProtocol.ThermalFanIdListPayloadLength,
        static payload => Rz090581CapabilityDiagnosticsProtocol.TryDecodeThermalFanIds(
            payload.Span,
            out _));

    private readonly IRazerCommandSession session;
    private readonly TimeSpan exchangeTimeout;

    internal Rz090581CapabilityDiagnosticsCommandClient(
        IRazerCommandSession session,
        TimeSpan exchangeTimeout)
    {
        ArgumentNullException.ThrowIfNull(session);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.session = session;
        this.exchangeTimeout = exchangeTimeout;
    }

    public async Task<HidSessionResult<Rz090581CapabilityDiagnostics?>> QueryAsync(
        Func<TransactionIdAssignment> assignTransactionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignTransactionId);
        HidSessionResult<ReadOnlyMemory<byte>> sku = await session.ExecuteAsync(
            SkuHardwareConfigurationQuery,
            assignTransactionId,
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (sku.Failure is not null)
        {
            return Failure(sku);
        }

        HidSessionResult<ReadOnlyMemory<byte>> miniLed = await session.ExecuteAsync(
            MiniLedPanelResolutionQuery,
            assignTransactionId,
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (miniLed.Failure is not null)
        {
            return Failure(miniLed);
        }

        HidSessionResult<ReadOnlyMemory<byte>> fans = await session.ExecuteAsync(
            ThermalFanIdListQuery,
            assignTransactionId,
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (fans.Failure is not null)
        {
            return Failure(fans);
        }

        return Rz090581CapabilityDiagnosticsProtocol.TryDecode(
            sku.Value.Span,
            miniLed.Value.Span,
            fans.Value.Span,
            out Rz090581CapabilityDiagnostics? diagnostics)
            ? new(diagnostics, null)
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

    private static HidSessionResult<Rz090581CapabilityDiagnostics?> Failure(
        HidSessionResult<ReadOnlyMemory<byte>> result) =>
        new(null, result.Failure)
        {
            Diagnostic = result.Diagnostic,
        };
}
