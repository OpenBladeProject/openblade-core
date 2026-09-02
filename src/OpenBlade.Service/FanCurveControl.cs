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

// The exact target firmware exposes a Razer-specific ACPI thermal zone named
// TZRZ. Windows publishes its EC-backed _TMP value through the standard Thermal
// Zone Information counter set. This is a platform/CPU-side thermal input, not a
// claim that Windows exposes the processor's per-core DTS values. NVML remains
// the signed user-mode source for the NVIDIA GPU temperature.
public sealed class SafeWindowsFanTemperatureSource : IFanTemperatureSource, IDisposable
{
    private readonly IGpuActivitySource gpuActivitySource;
    private readonly Func<int?> cpuTemperatureReader;
    private readonly Func<int?> gpuTemperatureReader;

    public SafeWindowsFanTemperatureSource()
        : this(
            new WindowsGpuActivitySource(),
            WindowsThermalZone.TryReadRazerEcTemperature,
            NvidiaNvml.TryReadTemperature)
    {
    }

    internal SafeWindowsFanTemperatureSource(
        IGpuActivitySource gpuActivitySource,
        Func<int?> cpuTemperatureReader,
        Func<int?> gpuTemperatureReader)
    {
        this.gpuActivitySource = gpuActivitySource
            ?? throw new ArgumentNullException(nameof(gpuActivitySource));
        this.cpuTemperatureReader = cpuTemperatureReader
            ?? throw new ArgumentNullException(nameof(cpuTemperatureReader));
        this.gpuTemperatureReader = gpuTemperatureReader
            ?? throw new ArgumentNullException(nameof(gpuTemperatureReader));
    }

    public ValueTask<FanTemperatureReadings> ReadAsync(
        CancellationToken cancellationToken) => ReadAsync(
            includeCpu: true,
            includeGpu: true,
            cancellationToken);

    public ValueTask<FanTemperatureReadings> ReadAsync(
        bool includeCpu,
        bool includeGpu,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool? gpuActive = includeGpu
            ? gpuActivitySource.IsActive(cancellationToken)
            : false;
        return ValueTask.FromResult(new FanTemperatureReadings(
            CpuCelsius: includeCpu
                ? cpuTemperatureReader()
                : null,
            GpuCelsius: gpuActive is true
                ? gpuTemperatureReader()
                : null,
            GpuInactive: includeGpu && gpuActive is false));
    }

    public void Dispose()
    {
        if (gpuActivitySource is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    internal static class WindowsThermalZone
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

        public static int? TryReadRazerEcTemperature()
        {
            foreach (string counterPath in RazerEcCounterPaths)
            {
                if (TryReadCounter(counterPath) is { } temperature)
                {
                    return temperature;
                }
            }

            return null;
        }

        private static int? TryReadCounter(string counterPath)
        {
            nint query = 0;
            try
            {
                if (PdhOpenQueryW(null, 0, out query) != 0
                    || PdhAddEnglishCounterW(
                        query,
                        counterPath,
                        0,
                        out nint counter) != 0
                    || PdhCollectQueryData(query) != 0
                    || PdhGetFormattedCounterValue(
                        counter,
                        PdhFormatDouble,
                        out _,
                        out PdhFormattedCounterValue value) != 0
                    || value.Status is not PdhStatusValidData and not PdhStatusNewData)
                {
                    return null;
                }

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
            finally
            {
                if (query != 0)
                {
                    _ = PdhCloseQuery(query);
                }
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

    private static class NvidiaNvml
    {
        private const uint TemperatureGpu = 0;

        public static int? TryReadTemperature()
        {
            try
            {
                if (nvmlInit_v2() != 0)
                {
                    return null;
                }

                try
                {
                    return nvmlDeviceGetHandleByIndex_v2(0, out nint device) == 0
                        && nvmlDeviceGetTemperature(device, TemperatureGpu, out uint value) == 0
                        && value <= 125
                            ? checked((int)value)
                            : null;
                }
                finally
                {
                    _ = nvmlShutdown();
                }
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
