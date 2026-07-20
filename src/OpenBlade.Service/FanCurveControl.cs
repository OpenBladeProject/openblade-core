using System.Runtime.InteropServices;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public readonly record struct FanTemperatureReadings(
    int? CpuCelsius,
    int? GpuCelsius);

public readonly record struct FanTargetSpeeds(
    int Fan1Rpm,
    int Fan2Rpm,
    bool FailSafe);

public static class FanCurveCalculator
{
    public static FanTargetSpeeds Calculate(
        ManualFanCurve curve,
        FanTemperatureReadings temperatures)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (temperatures.CpuCelsius is not int cpu
            || temperatures.GpuCelsius is not int gpu)
        {
            return new FanTargetSpeeds(
                FanControlSettings.MaximumSpeedRpm,
                FanControlSettings.MaximumSpeedRpm,
                FailSafe: true);
        }

        // These limits are independent of the user curve. They ensure a bad
        // or overly quiet curve cannot suppress emergency cooling.
        if (cpu >= 95 || gpu >= 87)
        {
            return new FanTargetSpeeds(
                FanControlSettings.MaximumSpeedRpm,
                FanControlSettings.MaximumSpeedRpm,
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
}

// NVML is a signed, user-mode NVIDIA API. Windows does not expose a dependable
// CPU package-temperature API on this model, so CpuCelsius deliberately remains
// unavailable and the curve controller stays at its fail-safe target unless a
// safe CPU provider is added later.
public sealed class SafeWindowsFanTemperatureSource : IFanTemperatureSource
{
    public ValueTask<FanTemperatureReadings> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new FanTemperatureReadings(
            CpuCelsius: null,
            GpuCelsius: NvidiaNvml.TryReadTemperature()));
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
