using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

// Exact Synapse source admits these existing ordinary commands independently
// of adapter-query availability. This never promotes Unknown to FullPower.
internal static class OrdinaryThermalPowerAdmission
{
    internal const string EvidenceSource = "evidence/source-reviewed-blades/2026-11-14-unclassified-external-thermal.json";

    internal static bool SupportsProduct(ushort productId) => productId is
        0x0234 or 0x024C or 0x0245 or 0x0253 or 0x0246 or 0x0268 or 0x0270
        or 0x0276 or 0x028A or 0x028B or 0x028C or 0x0255 or 0x02C5
        or 0x02B6 or 0x02B7 or 0x02B8 or 0x02C7 or 0x029D or 0x029F
        or 0x02A0 or 0x02C6 or 0x02E0;

    internal static bool ExternalPresence(PowerSource source) =>
        source is PowerSource.ExternalUnknown or PowerSource.PluggedIn;

    // HyperBoost/restoration shares 0D/02 but never inherits ordinary admission.
    // These ordinary Custom levels do not admit native OC, voltage or offsets.
    internal static bool IsOrdinaryExperimentalSet(string operation, byte commandClass,
        byte commandId, ReadOnlySpan<byte> arguments) => (operation, commandClass, commandId) switch
        {
            ("Performance", 0x0D, 0x02) or ("Fans", 0x0D, 0x02) => arguments.Length == 4
                && arguments[0] == 1 && arguments[1] is 1 or 2
                && arguments[2] is 0 or 1 or 2 or 4 or 5 && arguments[3] <= 1,
            ("FanTargets", 0x0D, 0x01) => arguments.Length == 3
                && arguments[0] == 1 && arguments[1] is 1 or 2,
            ("CustomPerformance", 0x0D, 0x02) => arguments.Length == 4
                && arguments[0] == 1 && arguments[1] is 1 or 2
                && arguments[2] == 4 && arguments[3] <= 1,
            ("CustomPerformance", 0x0D, 0x07) => arguments.Length == 3
                && arguments[0] == 1 && arguments[1] is 1 or 2
                && (arguments[1] == 1 ? arguments[2] <= 3 : arguments[2] <= 2),
            ("MaximumFans", 0x07, 0x0F) => arguments.Length == 1 && arguments[0] is 0 or 2,
            _ => false,
        };

    internal static bool IsAdapterQuery(RazerCommandSpec command) =>
        command.ClassId == 0x07 && command.CommandId == 0x8C
        && command.Arguments.Length == 2 && command.Arguments.Span[0] == 0
        && command.Arguments.Span[1] == 0 && command.ExpectedPayloadLength == 2;

    // Raw report c[0], not an outer USB/API result: 3=FAILED, 5=NOT_SUPPORTED.
    // New/Busy/Timeout remain failures. Error bodies are never wattage evidence.
    internal static bool IsCompletedAdapterRejection(RazerCommandSpec command,
        ReadOnlySpan<byte> response, HidProtocolValidationFailure failure, Task? retirement) =>
        failure == HidProtocolValidationFailure.Status && IsAdapterQuery(command)
        && HasSingleReportGeometry(response) && response[0] is 3 or 5
        && (retirement is null || retirement.IsCompletedSuccessfully);

    internal static bool HasSingleReportGeometry(ReadOnlySpan<byte> response) =>
        response.Length == RazerReport.ReportLength && response[2] == 0 && response[3] == 0
        && response[4] == 0 && response[5] <= RazerReport.MaximumPayloadLength && response[89] == 0;
}
