namespace OpenBlade.Protocol;

/// <summary>
/// Serializes complete feature-report exchanges and bounded matrix writes
/// across the independent PID 02E0 control and matrix interfaces.
/// </summary>
internal static class Rz090581FeatureCommandGate
{
    internal static SemaphoreSlim Shared { get; } = new(1, 1);
}
