using OpenBlade.Platform;

namespace OpenBlade.Service;

internal interface IGpuActivitySource
{
    bool IsActive();
}

internal sealed class InactiveGpuActivitySource : IGpuActivitySource
{
    internal static InactiveGpuActivitySource Instance { get; } = new();

    public bool IsActive() => false;
}

/// <summary>
/// Reads Windows' existing WDDM GPU-engine counters and never calls NVIDIA's
/// driver API. This lets callers decide whether NVML is safe to initialize
/// without waking an idle discrete GPU as part of the decision.
/// </summary>
internal sealed class WindowsGpuActivitySource : IGpuActivitySource, IDisposable
{
    private readonly WindowsNvidiaGpuActivityReader reader = new();

    public bool IsActive()
    {
        WindowsGpuActivitySnapshot snapshot = reader.Read();
        return snapshot.Processes.Any(process => process.IsActive);
    }

    public void Dispose()
    {
        reader.Dispose();
    }
}
