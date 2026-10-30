using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    private readonly ExternalPowerDischargeTracker externalPowerDischarge = new();

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
        externalPowerDischarge.Observe(
            timeProvider.GetUtcNow(),
            status.LiveReadings?.BatteryPowerFlowMilliwatts,
            TimeSpan.FromSeconds(Math.Max(10, LiveReadingsObservationInterval.TotalSeconds * 3)));
        PublishExternalPowerDischarge(wasDischarging, previousEpisode);
    }

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
