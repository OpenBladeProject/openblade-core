using OpenBlade.Platform;

namespace OpenBlade.Service;

internal interface IGpuActivitySource
{
    ValueTask<bool?> IsActiveAsync(CancellationToken cancellationToken = default);
}

internal sealed class InactiveGpuActivitySource : IGpuActivitySource
{
    internal static InactiveGpuActivitySource Instance { get; } = new();

    public ValueTask<bool?> IsActiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(false);
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

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly WindowsNvidiaGpuActivityReader? reader;
    private readonly Func<bool> refresh;
    private readonly Func<WindowsGpuActivitySnapshot> read;
    private readonly Func<int, CancellationToken, Task> wait;
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
        wait = static (milliseconds, cancellationToken) =>
            Task.Delay(milliseconds, cancellationToken);
    }

    internal WindowsGpuActivitySource(
        Func<bool> refresh,
        Func<WindowsGpuActivitySnapshot> read,
        Func<int, CancellationToken, Task> wait)
    {
        this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        this.wait = wait ?? throw new ArgumentNullException(nameof(wait));
    }

    /// <summary>
    /// Samples the counters twice across the sample window without blocking a
    /// thread-pool thread for the wait, so a caller holding a serialized
    /// operation gate does not also pin a thread for the duration.
    /// </summary>
    public async ValueTask<bool?> IsActiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed || !refresh())
            {
                return null;
            }

            _ = read();
            await wait(SampleDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            WindowsGpuActivitySnapshot snapshot = read();
            return snapshot.IsAvailable
                ? snapshot.Processes.Any(IsWorkloadProcess)
                : null;
        }
        finally
        {
            gate.Release();
        }
    }

    internal static bool IsWorkloadProcess(WindowsGpuProcessActivity process) =>
        process.IsActive && process.ProcessId != SystemProcessId;

    public void Dispose()
    {
        gate.Wait();
        try
        {
            if (disposed)
            {
                return;
            }

            reader?.Dispose();
            disposed = true;
        }
        finally
        {
            gate.Release();
        }
    }
}
