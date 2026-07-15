using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed record PowerTelemetry(PowerSource PowerSource, int? BatteryPercent);

public interface IPowerTelemetryProvider
{
    TelemetrySnapshot Capture();
}

public sealed class WindowsPowerTelemetryProvider : IPowerTelemetryProvider
{
    private readonly IGpuTemperatureSource gpuTemperatureSource;
    private readonly Func<PowerTelemetry> capturePowerTelemetry;

    internal WindowsPowerTelemetryProvider(IGpuTemperatureSource gpuTemperatureSource)
        : this(gpuTemperatureSource, CaptureWindowsPowerTelemetry)
    {
    }

    internal WindowsPowerTelemetryProvider(
        IGpuTemperatureSource gpuTemperatureSource,
        Func<PowerTelemetry> capturePowerTelemetry)
    {
        this.gpuTemperatureSource = gpuTemperatureSource
            ?? throw new ArgumentNullException(nameof(gpuTemperatureSource));
        this.capturePowerTelemetry = capturePowerTelemetry
            ?? throw new ArgumentNullException(nameof(capturePowerTelemetry));
    }

    public TelemetrySnapshot Capture()
    {
        PowerTelemetry power = capturePowerTelemetry();
        int? gpuTemperatureCelsius;
        try
        {
            gpuTemperatureCelsius = gpuTemperatureSource.CaptureCelsius();
        }
        catch
        {
            gpuTemperatureCelsius = null;
        }

        return new TelemetrySnapshot(
            DateTimeOffset.UtcNow,
            power.PowerSource,
            power.BatteryPercent,
            CpuTemperatureCelsius: null,
            GpuTemperatureCelsius: gpuTemperatureCelsius,
            FanRpm: null);
    }

    private static PowerTelemetry CaptureWindowsPowerTelemetry()
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return Map(status.AcLineStatus, status.BatteryLifePercent);
    }

    public static PowerTelemetry Map(byte acLineStatus, byte batteryPercent)
    {
        PowerSource source = acLineStatus switch
        {
            0 => PowerSource.Battery,
            1 => PowerSource.PluggedIn,
            _ => throw new InvalidDataException("Windows reported an unknown AC line status."),
        };
        int? percent = batteryPercent == byte.MaxValue ? null : batteryPercent;
        return new PowerTelemetry(source, percent);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);
}
