using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public readonly record struct FanTemperatureReadings(
    int? CpuCelsius,
    int? GpuCelsius,
    bool GpuInactive = false);

public readonly record struct FanTargetSpeeds(
    int Fan1Rpm,
    int Fan2Rpm,
    bool FailSafe);

public static class FanCurveCalculator
{
    public static FanTargetSpeeds Calculate(
        ManualFanCurve curve,
        FanTemperatureReadings temperatures,
        int maximumRpm = FanControlSettings.MaximumSpeedRpm)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (maximumRpm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRpm));
        }
        if (temperatures.CpuCelsius is not int cpu)
        {
            return new FanTargetSpeeds(
                maximumRpm,
                maximumRpm,
                FailSafe: true);
        }

        int gpu;
        if (temperatures.GpuCelsius is int gpuTemperature)
        {
            gpu = gpuTemperature;
        }
        else if (temperatures.GpuInactive)
        {
            // A powered-down discrete GPU contributes no heat of its own. Use
            // the CPU sensor for both curves so manual control remains safe
            // without waking the GPU merely to obtain a temperature.
            gpu = cpu;
        }
        else
        {
            return new FanTargetSpeeds(
                maximumRpm,
                maximumRpm,
                FailSafe: true);
        }

        // These limits are independent of the user curve. They ensure a bad
        // or overly quiet curve cannot suppress emergency cooling.
        if (cpu >= 95 || gpu >= 87)
        {
            return new FanTargetSpeeds(
                maximumRpm,
                maximumRpm,
                FailSafe: true);
        }

        if (curve.UseHigherTemperatureForBoth)
        {
            int hotter = Math.Max(cpu, gpu);
            return new FanTargetSpeeds(
                curve.InterpolateCpu(hotter),
                curve.InterpolateGpu(hotter),
                FailSafe: false);
        }

        return new FanTargetSpeeds(
            curve.InterpolateCpu(cpu),
            curve.InterpolateGpu(gpu),
            FailSafe: false);
    }
}

public interface IFanTemperatureSource
{
    ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken cancellationToken);

    async ValueTask<FanTemperatureReadings> ReadAsync(
        bool includeCpu,
        bool includeGpu,
        CancellationToken cancellationToken)
    {
        FanTemperatureReadings readings = await ReadAsync(cancellationToken);
        return new FanTemperatureReadings(
            includeCpu ? readings.CpuCelsius : null,
            includeGpu ? readings.GpuCelsius : null,
            includeGpu && readings.GpuInactive);
    }
}

/// <summary>
/// A discrete-GPU temperature reader whose driver session outlives one read.
/// <see cref="TryRead"/> opens the session when needed and keeps it;
/// <see cref="Release"/> closes it. The owner decides when to release so the
/// session is held only while the GPU is known to be awake.
/// </summary>
internal interface IGpuTemperatureSession
{
    int? TryRead();

    void Release();
}

internal sealed class NativeStatusChangeObserver(
    string component,
    string operation,
    Action<string>? traceWarning = null)
{
    private readonly object gate = new();
    private string? failureKey;
    private readonly Action<string> traceWarning = traceWarning ?? (message => Trace.TraceWarning(message));

    internal void ObserveFailure(string reason, uint status) => ObserveFailure(
        reason,
        status.ToString(CultureInfo.InvariantCulture),
        status);

    internal void ObserveFailure(string reason, int status) => ObserveFailure(
        reason,
        status.ToString(CultureInfo.InvariantCulture),
        unchecked((uint)status));

    private void ObserveFailure(string reason, string numericStatus, uint hexadecimalStatus)
    {
        string key = $"{reason}:{numericStatus}";
        lock (gate)
        {
            if (string.Equals(failureKey, key, StringComparison.Ordinal))
            {
                return;
            }

            failureKey = key;
        }

        TryTrace(
            $"component={component} operation={operation} state=failed reason={reason} "
            + $"status={numericStatus} statusHex=0x{hexadecimalStatus:X8}");
    }

    internal void ObserveRecovery()
    {
        lock (gate)
        {
            if (failureKey is null)
            {
                return;
            }

            failureKey = null;
        }

        TryTrace($"component={component} operation={operation} state=recovered status=0 statusHex=0x00000000");
    }

    private void TryTrace(string message)
    {
        try
        {
            traceWarning(message);
        }
        catch
        {
            // Diagnostics must not change sensor availability or fan fail-safe behavior.
        }
    }
}

// The exact target firmware exposes a Razer-specific ACPI thermal zone named
// TZRZ. Windows publishes its EC-backed _TMP value through the standard Thermal
// Zone Information counter set. This is a platform/CPU-side thermal input, not a
// claim that Windows exposes the processor's per-core DTS values. NVML remains
// the signed user-mode source for the NVIDIA GPU temperature.
public sealed class SafeWindowsFanTemperatureSource : IFanTemperatureSource, IDisposable
{
    // A GPU workload that ends between samples leaves the NVML session open
    // until the next sample reports the GPU inactive. If no sample arrives at
    // all (manual curve off, Tray hidden), this bound releases it so the
    // service never holds the driver session across an idle GPU.
    internal static readonly TimeSpan GpuSessionIdleTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan GpuSessionIdleRetry = TimeSpan.FromMilliseconds(100);

    private readonly IGpuActivitySource gpuActivitySource;
    private readonly Func<int?> cpuTemperatureReader;
    private readonly IDisposable? cpuTemperatureOwner;
    private readonly IGpuTemperatureSession gpuTemperature;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ITimer gpuSessionIdleRelease;
    private readonly TimeProvider timeProvider;
    private long lastGpuReadTimestamp;
    private bool gpuIdleReleasePending;
    private bool disposed;

    public SafeWindowsFanTemperatureSource()
        : this(
            new WindowsGpuActivitySource(),
            new WindowsThermalZone(),
            new NvidiaNvmlTemperatureSession(),
            TimeProvider.System)
    {
    }

    private SafeWindowsFanTemperatureSource(
        IGpuActivitySource gpuActivitySource,
        WindowsThermalZone thermalZone,
        IGpuTemperatureSession gpuTemperature,
        TimeProvider timeProvider)
        : this(
            gpuActivitySource,
            thermalZone.TryRead,
            gpuTemperature,
            timeProvider,
            cpuTemperatureOwner: thermalZone)
    {
    }

    internal SafeWindowsFanTemperatureSource(
        IGpuActivitySource gpuActivitySource,
        Func<int?> cpuTemperatureReader,
        Func<int?> gpuTemperatureReader)
        : this(
            gpuActivitySource,
            cpuTemperatureReader,
            new DelegateGpuTemperatureSession(gpuTemperatureReader),
            TimeProvider.System)
    {
    }

    internal SafeWindowsFanTemperatureSource(
        IGpuActivitySource gpuActivitySource,
        Func<int?> cpuTemperatureReader,
        IGpuTemperatureSession gpuTemperature,
        TimeProvider timeProvider,
        IDisposable? cpuTemperatureOwner = null)
    {
        this.gpuActivitySource = gpuActivitySource
            ?? throw new ArgumentNullException(nameof(gpuActivitySource));
        this.cpuTemperatureReader = cpuTemperatureReader
            ?? throw new ArgumentNullException(nameof(cpuTemperatureReader));
        this.gpuTemperature = gpuTemperature
            ?? throw new ArgumentNullException(nameof(gpuTemperature));
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;
        this.cpuTemperatureOwner = cpuTemperatureOwner;
        gpuSessionIdleRelease = timeProvider.CreateTimer(
            static state => ((SafeWindowsFanTemperatureSource)state!).ReleaseIdleGpuSession(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public ValueTask<FanTemperatureReadings> ReadAsync(
        CancellationToken cancellationToken) => ReadAsync(
            includeCpu: true,
            includeGpu: true,
            cancellationToken);

    public async ValueTask<FanTemperatureReadings> ReadAsync(
        bool includeCpu,
        bool includeGpu,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool? gpuActive = includeGpu
            ? await gpuActivitySource.IsActiveAsync(cancellationToken).ConfigureAwait(false)
            : false;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int? cpuCelsius = includeCpu ? cpuTemperatureReader() : null;
            int? gpuCelsius = null;
            if (gpuActive is true)
            {
                gpuCelsius = gpuTemperature.TryRead();
                lastGpuReadTimestamp = timeProvider.GetTimestamp();
                gpuIdleReleasePending = true;
                gpuSessionIdleRelease.Change(GpuSessionIdleTimeout, Timeout.InfiniteTimeSpan);
            }
            else if (includeGpu)
            {
                // Inactive or unknown: never hold the driver session across a
                // GPU that may be asleep.
                gpuSessionIdleRelease.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                gpuIdleReleasePending = false;
                gpuTemperature.Release();
            }

            return new FanTemperatureReadings(
                cpuCelsius,
                gpuCelsius,
                GpuInactive: includeGpu && gpuActive is false);
        }
        finally
        {
            gate.Release();
        }
    }

    private void ReleaseIdleGpuSession()
    {
        // CPU-only reads do not touch the GPU session. Retain the expiry when
        // they hold the gate, without blocking a timer thread on native reads.
        if (!gate.Wait(0))
        {
            try
            {
                gpuSessionIdleRelease.Change(GpuSessionIdleRetry, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Disposal may own the gate and has already released the session.
            }
            return;
        }

        try
        {
            if (!disposed && gpuIdleReleasePending)
            {
                TimeSpan remaining = GpuSessionIdleTimeout
                    - timeProvider.GetElapsedTime(lastGpuReadTimestamp);
                if (remaining > TimeSpan.Zero)
                {
                    // A queued callback from an earlier timer generation must
                    // not close a session refreshed by a newer GPU sample.
                    gpuSessionIdleRelease.Change(remaining, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    gpuIdleReleasePending = false;
                    gpuTemperature.Release();
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        gate.Wait();
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            gpuSessionIdleRelease.Dispose();
            gpuTemperature.Release();
            cpuTemperatureOwner?.Dispose();
        }
        finally
        {
            gate.Release();
        }

        if (gpuActivitySource is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private sealed class DelegateGpuTemperatureSession(Func<int?> read) : IGpuTemperatureSession
    {
        public int? TryRead() => read();

        public void Release()
        {
        }
    }

    /// <summary>
    /// Holds one PDH query on the resolved Razer EC counter path across reads
    /// instead of opening, adding, collecting, and closing a query per sample.
    /// A collection failure closes the query so the next read re-resolves it.
    /// </summary>
    internal sealed class WindowsThermalZone : IDisposable
    {
        internal static IReadOnlyList<string> RazerEcCounterPaths { get; } =
        [
            // RZ09-0528 BIOS 2.02 / EC 1.09.
            @"\Thermal Zone Information(\_SB.PCI0.SBRG.EC0.TZRZ)\High Precision Temperature",
            // RZ09-0581 validated baseline.
            @"\Thermal Zone Information(\_SB.PC00.LPCB.EC0.TZRZ)\High Precision Temperature",
        ];
        private const uint PdhFormatDouble = 0x00000200;
        private const uint PdhStatusValidData = 0x00000000;
        private const uint PdhStatusNewData = 0x00000001;

        private nint query;
        private nint counter;
        private bool disposed;
        private readonly NativeStatusChangeObserver statusObserver = new(
            nameof(WindowsThermalZone),
            nameof(TryRead));

        public int? TryRead()
        {
            if (disposed)
            {
                return null;
            }

            try
            {
                if (query == 0 && !TryOpenAnyCounter())
                {
                    return null;
                }

                uint collectStatus = PdhCollectQueryData(query);
                if (collectStatus != 0)
                {
                    statusObserver.ObserveFailure("CollectQueryData", collectStatus);
                    CloseQuery();
                    return null;
                }

                uint formatStatus = PdhGetFormattedCounterValue(
                    counter,
                    PdhFormatDouble,
                    out _,
                    out PdhFormattedCounterValue value);
                if (formatStatus != 0)
                {
                    statusObserver.ObserveFailure("GetFormattedCounterValue", formatStatus);
                    CloseQuery();
                    return null;
                }

                if (value.Status is not PdhStatusValidData and not PdhStatusNewData)
                {
                    statusObserver.ObserveFailure("FormattedValue", value.Status);
                    CloseQuery();
                    return null;
                }

                statusObserver.ObserveRecovery();
                return TryConvertHighPrecisionKelvin(value.DoubleValue);
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }

        private bool TryOpenAnyCounter()
        {
            string? lastReason = null;
            uint lastStatus = 0;
            foreach (string counterPath in RazerEcCounterPaths)
            {
                uint openStatus = PdhOpenQueryW(null, 0, out nint candidate);
                if (openStatus != 0)
                {
                    lastReason = "OpenQuery";
                    lastStatus = openStatus;
                    continue;
                }

                uint addStatus = PdhAddEnglishCounterW(candidate, counterPath, 0, out nint candidateCounter);
                uint collectStatus = addStatus == 0 ? PdhCollectQueryData(candidate) : 0;
                PdhFormattedCounterValue value = default;
                uint formatStatus = addStatus == 0 && collectStatus == 0
                    ? PdhGetFormattedCounterValue(
                        candidateCounter,
                        PdhFormatDouble,
                        out _,
                        out value)
                    : 0;
                if (addStatus != 0
                    || collectStatus != 0
                    || formatStatus != 0
                    || value.Status is not PdhStatusValidData and not PdhStatusNewData)
                {
                    (lastReason, lastStatus) = addStatus != 0
                        ? ("AddEnglishCounter", addStatus)
                        : collectStatus != 0
                            ? ("PrimeCollectQueryData", collectStatus)
                            : formatStatus != 0
                                ? ("PrimeGetFormattedCounterValue", formatStatus)
                                : ("PrimeFormattedValue", value.Status);
                    uint closeStatus = PdhCloseQuery(candidate);
                    if (closeStatus != 0)
                    {
                        statusObserver.ObserveFailure("CloseRejectedCounter", closeStatus);
                    }
                    continue;
                }

                query = candidate;
                counter = candidateCounter;
                statusObserver.ObserveRecovery();
                return true;
            }

            if (lastReason is not null)
            {
                statusObserver.ObserveFailure(lastReason, lastStatus);
            }
            return false;
        }

        private void CloseQuery()
        {
            if (query != 0)
            {
                uint closeStatus = PdhCloseQuery(query);
                if (closeStatus != 0)
                {
                    statusObserver.ObserveFailure("CloseQuery", closeStatus);
                }
                query = 0;
                counter = 0;
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                CloseQuery();
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        internal static int? TryConvertHighPrecisionKelvin(double value)
        {
            if (!double.IsFinite(value))
            {
                return null;
            }

            double celsius = (value / 10.0) - 273.15;
            return celsius is >= 0 and <= 115
                ? checked((int)Math.Round(celsius, MidpointRounding.AwayFromZero))
                : null;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PdhFormattedCounterValue
        {
            [FieldOffset(0)]
            public uint Status;

            [FieldOffset(8)]
            public double DoubleValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(
            string? dataSource,
            nuint userData,
            out nint query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(
            nint query,
            string fullCounterPath,
            nuint userData,
            out nint counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(nint query);

        [DllImport("pdh.dll")]
        private static extern uint PdhGetFormattedCounterValue(
            nint counter,
            uint format,
            out uint counterType,
            out PdhFormattedCounterValue value);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(nint query);
    }

    /// <summary>
    /// Keeps <c>nvmlInit</c> open across consecutive active samples. The
    /// caller releases it as soon as a sample reports the GPU inactive or
    /// unknown, so the session never outlives a known-awake GPU by more than
    /// one sample interval or the idle bound.
    /// </summary>
    private sealed class NvidiaNvmlTemperatureSession : IGpuTemperatureSession
    {
        private const uint TemperatureGpu = 0;

        private bool initialized;
        private readonly NativeStatusChangeObserver statusObserver = new(
            nameof(NvidiaNvmlTemperatureSession),
            nameof(TryRead));

        public int? TryRead()
        {
            try
            {
                if (!initialized)
                {
                    int initStatus = nvmlInit_v2();
                    if (initStatus != 0)
                    {
                        statusObserver.ObserveFailure("Initialize", initStatus);
                        return null;
                    }

                    initialized = true;
                }

                int handleStatus = nvmlDeviceGetHandleByIndex_v2(0, out nint device);
                if (handleStatus != 0)
                {
                    statusObserver.ObserveFailure("GetDeviceHandle", handleStatus);
                    Release();
                    return null;
                }

                int temperatureStatus = nvmlDeviceGetTemperature(device, TemperatureGpu, out uint value);
                if (temperatureStatus != 0)
                {
                    statusObserver.ObserveFailure("GetTemperature", temperatureStatus);
                    Release();
                    return null;
                }

                if (value > 125)
                {
                    statusObserver.ObserveFailure("TemperatureOutOfRange", value);
                    return null;
                }

                statusObserver.ObserveRecovery();
                return checked((int)value);
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }

        public void Release()
        {
            if (!initialized)
            {
                return;
            }

            initialized = false;
            try
            {
                int shutdownStatus = nvmlShutdown();
                if (shutdownStatus != 0)
                {
                    statusObserver.ObserveFailure("Shutdown", shutdownStatus);
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlInit_v2();

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlShutdown();

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetHandleByIndex_v2(
            uint index,
            out nint device);

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetTemperature(
            nint device,
            uint sensorType,
            out uint temperature);
    }
}

public sealed class ServiceFanCurveLoop(
    IServiceControlRuntime runtime,
    ILogger logger)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await runtime.MaintainFanCurveAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Manual fan-curve maintenance failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
