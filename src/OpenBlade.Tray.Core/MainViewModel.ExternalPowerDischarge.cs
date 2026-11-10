using System.Runtime.InteropServices;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    private readonly ExternalPowerDischargeTracker externalPowerDischarge;

    internal Func<int?> ReadPluggedInBatteryPercent { get; init; } = ReadWindowsBatteryPercent;

    public bool IsExternalPowerDischarging => externalPowerDischarge.IsDischarging;

    public long ExternalPowerDischargeEpisode => externalPowerDischarge.Episode;

    public bool RequiresExternalPowerDischargeObservation => LiveReadingsEnabled
        && IsConnected && latestStatus?.DeviceVerified is true
        && ActivePowerSource is PowerSource.PluggedIn or PowerSource.UsbC;

    public Task ObserveExternalPowerDischargeAsync(CancellationToken cancellationToken) =>
        ObserveStateAsync(
            includeMainDashboardReadings: false,
            LiveReadingSensors.BatteryPower,
            trayOnly: true,
            cancellationToken,
            batteryPowerOnly: true);

    private void ResetExternalPowerDischarge()
    {
        bool wasDischarging = IsExternalPowerDischarging;
        externalPowerDischarge.Reset();
        PublishExternalPowerDischarge(wasDischarging, ExternalPowerDischargeEpisode);
    }

    private void ObserveExternalPowerDischargeSource(ServiceStatusSnapshot status)
    {
        bool wasDischarging = IsExternalPowerDischarging;
        if (!LiveReadingsEnabled)
        {
            externalPowerDischarge.Reset();
        }
        else
        {
            externalPowerDischarge.ObserveSource(status.ActivePowerSource, status.DeviceVerified);
        }
        PublishExternalPowerDischarge(wasDischarging, ExternalPowerDischargeEpisode);
    }

    private void ObserveExternalPowerDischarge(ServiceStatusSnapshot status)
    {
        if (!LiveReadingsEnabled || !status.DeviceVerified
            || status.ActivePowerSource is not (PowerSource.PluggedIn or PowerSource.UsbC))
        {
            return;
        }

        bool wasDischarging = IsExternalPowerDischarging;
        long previousEpisode = ExternalPowerDischargeEpisode;
        int? batteryPower = status.LiveReadings?.BatteryPowerFlowMilliwatts;
        if (batteryPower is < 0 && IsAtActiveChargeLimit(status))
        {
            externalPowerDischarge.Reset();
        }
        else
        {
            externalPowerDischarge.Observe(batteryPower);
        }
        PublishExternalPowerDischarge(wasDischarging, previousEpisode);
    }

    private bool IsAtActiveChargeLimit(ServiceStatusSnapshot status) =>
        status.ConfirmedState?.Battery is { ProtectionEnabled: true, ChargeLimitPercent: int limit } battery
        && (battery.TemporaryFullCharge is false
            || battery.TemporaryFullCharge is null && status.Capabilities?.SupportsTemporaryFullCharge is false)
        // Only the exact cap qualifies. A negative sample below the cap can start an episode immediately.
        && ReadPluggedInBatteryPercent() == limit;

    private static int? ReadWindowsBatteryPercent() =>
        OperatingSystem.IsWindows()
        && GetSystemPowerStatus(out SystemPowerStatus power)
        && power.AcLineStatus == 1
        && power.BatteryLifePercent <= 100
            ? power.BatteryLifePercent
            : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        internal byte AcLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal uint BatteryLifeTime;
        internal uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private void PublishExternalPowerDischarge(bool wasDischarging, long previousEpisode)
    {
        if (wasDischarging != IsExternalPowerDischarging)
        {
            Notify(nameof(IsExternalPowerDischarging));
            NotifyTrayHoverLabel();
        }
        if (previousEpisode != ExternalPowerDischargeEpisode)
        {
            Notify(nameof(ExternalPowerDischargeEpisode));
        }
    }
}
