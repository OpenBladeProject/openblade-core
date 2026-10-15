namespace OpenBlade.Protocol;

/// <summary>
/// Shares the PID 02C6 control-interface lock between battery, thermal,
/// RPM, and retained lighting sessions so exchanges cannot consume each
/// other's responses. Distinct models retain independent locks.
/// </summary>
internal static class Rz090528FeatureCommandGate
{
    internal static SemaphoreSlim Shared { get; } = new(1, 1);
}
