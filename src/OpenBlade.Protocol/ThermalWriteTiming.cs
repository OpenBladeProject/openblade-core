namespace OpenBlade.Protocol;

internal static class ThermalWriteTiming
{
    // Rounded up from the Synapse PCAP: 9.56 ms between fixed-state channels,
    // 205.51 ms before the first speed write, and 61.53 ms between speed channels.
    internal static TimeSpan MaximumInterCommandDelay(int completedCommandIndex) =>
        completedCommandIndex switch
        {
            0 => TimeSpan.FromMilliseconds(10),
            1 => TimeSpan.FromMilliseconds(210),
            2 => TimeSpan.FromMilliseconds(65),
            _ => TimeSpan.Zero,
        };
}
