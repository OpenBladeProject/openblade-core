using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public interface IPowerStateProvider
{
    PowerSource CapturePowerSource();

    int? CaptureBatteryPowerFlowMilliwatts();
}

public sealed class WindowsPowerStateProvider : IPowerStateProvider
{
    private const int SystemBatteryStateInformationLevel = 5;
    private const int MaximumReasonableBatteryPowerMilliwatts = 1_000_000;

    private readonly Func<PowerSource> capturePowerSource;
    private readonly Func<int?> captureBatteryPowerFlow;

    internal WindowsPowerStateProvider()
        : this(CaptureWindowsPowerSource, CaptureWindowsBatteryPowerFlow)
    {
    }

    internal WindowsPowerStateProvider(
        Func<PowerSource> capturePowerSource,
        Func<int?> captureBatteryPowerFlow)
    {
        this.capturePowerSource = capturePowerSource
            ?? throw new ArgumentNullException(nameof(capturePowerSource));
        this.captureBatteryPowerFlow = captureBatteryPowerFlow
            ?? throw new ArgumentNullException(nameof(captureBatteryPowerFlow));
    }

    public PowerSource CapturePowerSource() => capturePowerSource();

    public int? CaptureBatteryPowerFlowMilliwatts()
    {
        try
        {
            return captureBatteryPowerFlow();
        }
        catch
        {
            return null;
        }
    }

    internal static PowerSource MapPowerSource(byte acLineStatus) => acLineStatus switch
    {
        0 => PowerSource.Battery,
        1 => PowerSource.PluggedIn,
        _ => throw new InvalidDataException("Windows reported an unknown AC line status."),
    };

    internal static int? MapBatteryPowerFlow(bool batteryPresent, uint rawRate)
    {
        if (!batteryPresent)
        {
            return null;
        }

        int rate = unchecked((int)rawRate);
        if (rate == int.MinValue
            || rate is < -MaximumReasonableBatteryPowerMilliwatts
                or > MaximumReasonableBatteryPowerMilliwatts)
        {
            return null;
        }

        return rate;
    }

    private static PowerSource CaptureWindowsPowerSource()
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return MapPowerSource(status.AcLineStatus);
    }

    private static int? CaptureWindowsBatteryPowerFlow()
    {
        int status = CallNtPowerInformation(
            SystemBatteryStateInformationLevel,
            IntPtr.Zero,
            0,
            out SystemBatteryState battery,
            (uint)Marshal.SizeOf<SystemBatteryState>());
        if (status != 0)
        {
            return null;
        }

        return MapBatteryPowerFlow(battery.BatteryPresent != 0, battery.Rate);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemBatteryState
    {
        public byte AcOnLine;
        public byte BatteryPresent;
        public byte Charging;
        public byte Discharging;
        public byte Spare1;
        public byte Spare2;
        public byte Spare3;
        public byte Spare4;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        public uint Rate;
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [DllImport("powrprof.dll")]
    private static extern int CallNtPowerInformation(
        int informationLevel,
        IntPtr inputBuffer,
        uint inputBufferLength,
        out SystemBatteryState outputBuffer,
        uint outputBufferLength);
}
