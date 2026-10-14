namespace OpenBlade.Protocol;

/// <summary>
/// Serializes complete feature-report exchanges and bounded matrix writes
/// across the independent PID 02E0 control and matrix interfaces.
/// </summary>
internal static class Rz090581FeatureCommandGate
{
    internal static SemaphoreSlim Shared { get; } = new(1, 1);

    internal static void ReleaseAfterRetirement(SemaphoreSlim gate, Task? retirement)
    {
        if (retirement is null || retirement.IsCompletedSuccessfully)
        {
            gate.Release();
            return;
        }
        _ = ReleaseWhenRetiredAsync(gate, retirement);
    }

    private static async Task ReleaseWhenRetiredAsync(SemaphoreSlim gate, Task retirement)
    {
        try
        {
            await retirement.ConfigureAwait(false);
        }
        catch
        {
            // An unconfirmed native lifetime must keep all other interfaces
            // blocked. Admission remains bounded; no grace period proves safety.
            return;
        }
        gate.Release();
    }
}
