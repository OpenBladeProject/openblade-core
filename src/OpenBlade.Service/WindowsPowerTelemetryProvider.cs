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
    public TelemetrySnapshot Capture()
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        PowerTelemetry power = Map(status.AcLineStatus, status.BatteryLifePercent);
        return new TelemetrySnapshot(
            DateTimeOffset.UtcNow,
            power.PowerSource,
            power.BatteryPercent,
            CpuTemperatureCelsius: null,
            GpuTemperatureCelsius: null,
            FanRpm: null);
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
