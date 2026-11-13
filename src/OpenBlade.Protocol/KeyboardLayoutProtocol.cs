namespace OpenBlade.Protocol;

/// <summary>Read-only layout metadata shared by the independently reviewed
/// product 736 and 710 rzDevice25 getEdition contracts (module 73932).
/// Request 00/86 has three zero bytes; reply byte 0 selects each MCU's map.
/// Adjacent edition/firmware bytes are opaque metadata, never equality gates.</summary>
internal static class KeyboardLayoutProtocol
{
    internal static RazerCommandSpec CreateQuery() =>
        new(0x00, 0x86, new byte[3], 3, payload => payload.Length == 3);
}
