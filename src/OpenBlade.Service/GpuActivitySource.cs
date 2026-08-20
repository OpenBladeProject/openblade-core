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
    private readonly WindowsNvidiaGpuActivityReader? reader;
    private readonly Func<WindowsGpuActivitySnapshot> read;

    internal WindowsGpuActivitySource()
        : this(new WindowsNvidiaGpuActivityReader())
    {
    }

    private WindowsGpuActivitySource(WindowsNvidiaGpuActivityReader reader)
    {
        this.reader = reader;
        read = reader.Read;
        try
        {
            _ = read();
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    internal WindowsGpuActivitySource(Func<WindowsGpuActivitySnapshot> read)
    {
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        _ = read();
    }

    public bool IsActive()
    {
        WindowsGpuActivitySnapshot snapshot = read();
        return snapshot.Processes.Any(process => process.IsActive);
    }

    public void Dispose()
    {
        reader?.Dispose();
    }
}
