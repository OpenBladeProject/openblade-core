using System.Text;

namespace OpenBlade.Capture;

internal sealed record StockGpuTelemetry(DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    bool ExactDeviceConfirmed, uint? GpuUtilizationPercent, int? GpuCelsius,
    IReadOnlyDictionary<string, int> NativeStatuses, string? Failure)
{
    public string Source => "NVML utilization and temperature on the same exact device handle";
}

internal static class StockGpuTelemetryReader
{
    internal static StockGpuTelemetry Read(INvidiaNvmlGpuClockOffsetValidationApi api,
        Func<nint, (int Status, uint Value)> utilization,
        Func<nint, (int Status, uint Value)> temperature)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var statuses = new Dictionary<string, int>();
        bool initialized = false, exact = false;
        uint? load = null;
        int? celsius = null;
        string? failure = null;
        void ReadCore()
        {
            statuses["Initialize"] = api.Initialize();
            if (statuses["Initialize"] != 0) { failure = "NvmlInitializeFailed"; return; }
            initialized = true;
            statuses["DeviceCount"] = api.GetDeviceCount(out uint count);
            if (statuses["DeviceCount"] != 0 || count != 1) { failure = "ExactGpuCountNotConfirmed"; return; }
            statuses["DeviceHandle"] = api.GetDeviceHandleByIndex(0, out nint handle);
            if (statuses["DeviceHandle"] != 0 || handle == 0) { failure = "ExactGpuHandleUnavailable"; return; }
            var name = new StringBuilder(96);
            statuses["DeviceName"] = api.GetDeviceName(handle, name, 96);
            if (statuses["DeviceName"] != 0 || name.ToString() != GpuClockOffsetValidationPlan.Rz090528UiStep.ExpectedDeviceName)
            { failure = "ExactGpuNameNotConfirmed"; return; }
            exact = true;
            var observedLoad = utilization(handle);
            statuses["Utilization"] = observedLoad.Status;
            var observedTemperature = temperature(handle);
            statuses["Temperature"] = observedTemperature.Status;
            if (observedLoad.Status == 0 && observedLoad.Value <= 100)
            {
                load = observedLoad.Value;
            }
            // Sensor plausibility only, never an experimental thermal threshold.
            if (observedTemperature.Status == 0 && observedTemperature.Value is > 0 and <= 125)
            {
                celsius = checked((int)observedTemperature.Value);
            }

            if (load is null)
            {
                failure = "NvmlUtilizationUnavailableOrInvalid";
            }
            else if (celsius is null)
            {
                failure = "NvmlTemperatureUnavailableOrInvalid";
            }
        }
        try { ReadCore(); }
        catch { failure = "NvmlTelemetryException"; }
        finally
        {
            if (initialized)
            {
                try
                {
                    statuses["Shutdown"] = api.Shutdown();
                    if (statuses["Shutdown"] != 0)
                    {
                        failure ??= "NvmlShutdownFailed";
                    }
                }
                catch { failure ??= "NvmlShutdownException"; }
            }
        }
        return new(started, DateTimeOffset.UtcNow, exact, load, celsius, statuses, failure);
    }
}
