using LibreHardwareMonitor;
using LibreHardwareMonitor.Hardware;

namespace OpenBlade.CpuTemperature;

internal sealed class LhmCpuTemperatureSession : ICpuTemperatureSession
{
    private readonly CpuTemperatureComputer computer = new();
    private bool disposed;

    internal LhmCpuTemperatureSession()
    {
        try { computer.Open(); }
        catch { computer.Dispose(); throw; }
    }

    public int? ReadMilliCelsius()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // The pinned facade clears every channel before acquisition and after
        // failures. Native transport errors throw instead of becoming zero.
        computer.Update();
        if (computer.Hardware.Count == 0) { return null; }
        int? hottest = null;
        foreach (IHardware hardware in computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Cpu) { return null; }
            var channels = new List<CpuThermalChannel>();
            foreach (ISensor sensor in hardware.Sensors.Where(sensor => sensor.SensorType == SensorType.Temperature))
            {
                CpuThermalChannelKind kind = Classify(hardware.Identifier.ToString(), sensor);
                channels.Add(new(kind, sensor.Value,
                    sensor.Parameters.FirstOrDefault(parameter => parameter.Name == "TjMax [°C]")?.Value,
                    sensor.Parameters.FirstOrDefault(parameter => parameter.Name == "TSlope [°C]")?.Value));
            }
            int? selected = CpuTemperatureSelection.Select(channels);
            if (selected is null) { return null; }
            hottest = Math.Max(hottest ?? selected.Value, selected.Value);
        }
        return hottest;
    }

    private static CpuThermalChannelKind Classify(string hardwareId, ISensor sensor)
    {
        if (hardwareId.StartsWith("/intelcpu/", StringComparison.Ordinal))
        {
            // The actual core/package channels have calibration parameters.
            // Distance to TjMax, averages and aggregates are not safety inputs.
            if (!sensor.Parameters.Any(parameter => parameter.Name == "TjMax [°C]")) { return CpuThermalChannelKind.Other; }
            return sensor.Name == "CPU Package" ? CpuThermalChannelKind.IntelPackage : CpuThermalChannelKind.IntelCore;
        }
        if (hardwareId.StartsWith("/amdcpu/", StringComparison.Ordinal))
        {
            return sensor.Name switch
            {
                "Core (Tdie)" or "Core (Tctl/Tdie)" => CpuThermalChannelKind.AmdDie,
                "Core (Tctl)" => CpuThermalChannelKind.AmdControl,
                _ => CpuThermalChannelKind.Other,
            };
        }
        return CpuThermalChannelKind.Other;
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        computer.Dispose();
    }
}
