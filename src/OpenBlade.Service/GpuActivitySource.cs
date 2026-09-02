using OpenBlade.Platform;

namespace OpenBlade.Service;

internal interface IGpuActivitySource
{
    bool? IsActive(CancellationToken cancellationToken = default);
}

internal sealed class InactiveGpuActivitySource : IGpuActivitySource
{
    internal static InactiveGpuActivitySource Instance { get; } = new();

    public bool? IsActive(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }
}

/// <summary>
/// Reads Windows' existing WDDM GPU-engine counters and never calls NVIDIA's
/// driver API. This lets callers decide whether NVML is safe to initialize
/// without waking an idle discrete GPU as part of the decision.
/// </summary>
internal sealed class WindowsGpuActivitySource : IGpuActivitySource, IDisposable
{
    private const int SampleDelayMilliseconds = 150;

    // PID 4 is the Windows System process. Its copy-engine entries on the NVIDIA
    // adapter are video-memory paging that accompanies the adapter's own power
    // transitions, not a workload, so they must not open NVML on their own.
    private const int SystemProcessId = 4;

    private readonly object sync = new();
    private readonly WindowsNvidiaGpuActivityReader? reader;
    private readonly Func<bool> refresh;
    private readonly Func<WindowsGpuActivitySnapshot> read;
    private readonly Action<int, CancellationToken> wait;
    private bool disposed;

    internal WindowsGpuActivitySource()
        : this(new WindowsNvidiaGpuActivityReader())
    {
    }

    private WindowsGpuActivitySource(WindowsNvidiaGpuActivityReader reader)
    {
        this.reader = reader;
        refresh = reader.Refresh;
        read = reader.Read;
        wait = WaitForNextSample;
    }

    internal WindowsGpuActivitySource(
        Func<bool> refresh,
        Func<WindowsGpuActivitySnapshot> read,
        Action<int, CancellationToken> wait)
    {
        this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        this.wait = wait ?? throw new ArgumentNullException(nameof(wait));
    }

    public bool? IsActive(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || !refresh())
            {
                return null;
            }

            _ = read();
            wait(SampleDelayMilliseconds, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            WindowsGpuActivitySnapshot snapshot = read();
            return snapshot.IsAvailable
                ? snapshot.Processes.Any(IsWorkloadProcess)
                : null;
        }
    }

    internal static bool IsWorkloadProcess(WindowsGpuProcessActivity process) =>
        process.IsActive && process.ProcessId != SystemProcessId;

    private static void WaitForNextSample(
        int milliseconds,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(milliseconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            reader?.Dispose();
            disposed = true;
        }
    }
}
