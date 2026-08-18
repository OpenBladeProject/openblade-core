namespace OpenBlade.Protocol;

/// <summary>
/// An opaque byte observed in the third response position of the exact
/// PID 0F43 cooling-pad fan query. No unit or state meaning is admitted.
/// </summary>
internal readonly record struct CoolingPadFanObservedRawByte
{
    internal CoolingPadFanObservedRawByte(byte value)
    {
        RawByte = value;
    }

    /// <summary>
    /// Gets the response byte without interpreting it as RPM, a target,
    /// an active mode, or evidence of controller ownership.
    /// </summary>
    internal byte RawByte { get; }
}

/// <summary>
/// Defines the read-only 0D/81 query admitted for the exact PID 0F43
/// report-ID-5 HID collection. The shared HID session owns report-ID framing;
/// this type defines only the validated 90-byte Razer report body contract.
/// </summary>
internal static class CoolingPadFanObservedRawProtocol
{
    internal const byte CommandClass = 0x0D;
    internal const byte QueryCommandId = 0x81;
    internal const byte ThermalClassId = 0x01;
    internal const byte ThermalId = 0x05;

    /// <summary>
    /// Creates the exact read-only query. The returned value remains opaque;
    /// callers must not use it as RPM, target, active-mode, or ownership state.
    /// </summary>
    internal static RazerCommandSpec CreateQuery() => new(
        CommandClass,
        QueryCommandId,
        [ThermalClassId, ThermalId, 0x00],
        expectedPayloadLength: 3,
        payload => TryDecodeResponse(payload.Span, out _));

    internal static bool TryDecodeResponse(
        ReadOnlySpan<byte> payload,
        out CoolingPadFanObservedRawByte observation)
    {
        if (payload is [ThermalClassId, ThermalId, var rawValue])
        {
            observation = new CoolingPadFanObservedRawByte(rawValue);
            return true;
        }

        observation = default;
        return false;
    }
}
