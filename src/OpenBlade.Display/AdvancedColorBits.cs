namespace OpenBlade.Display;

internal static class AdvancedColorBits
{
    private const uint HdrSupportedMask = 1u << 4;
    private const uint HdrUserEnabledMask = 1u << 5;

    internal static bool IsHdrSupported(uint value) => (value & HdrSupportedMask) != 0;

    internal static bool IsHdrUserEnabled(uint value) => (value & HdrUserEnabledMask) != 0;
}
