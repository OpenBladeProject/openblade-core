using System.ComponentModel;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    private void NotifyDashboardTelemetryPreferencesChanged()
    {
        if (changingTelemetryPreferences)
        {
            return;
        }
        Notify(nameof(EnabledDashboardTelemetryItems));
        if (dashboardTelemetryPresentationActive)
        {
            Notify(nameof(VisibleDashboardTelemetryGroups));
            Notify(nameof(ShowDashboardLiveReadings));
        }
    }

    private void NotifyDashboardTelemetryValuesChanged()
    {
        Notify(nameof(TrayDashboardBatteryPowerFlowLabel));
        Notify(nameof(TrayDashboardTemperatureLabel));
        Notify(nameof(TrayDashboardFanSpeedLabel));
        Notify(nameof(TrayDashboardFanSpeedToolTip));
        if (!dashboardTelemetryPresentationActive)
        {
            return;
        }

        Notify(nameof(BatteryPowerFlowLabel));
        Notify(nameof(Fan1RpmLabel));
        Notify(nameof(Fan2RpmLabel));
        Notify(nameof(Fan1RpmValue));
        Notify(nameof(Fan2RpmValue));
        Notify(nameof(CpuTemperatureValue));
        Notify(nameof(GpuTemperatureValue));
        Notify(nameof(VisibleDashboardTelemetryGroups));
    }

    private void NotifyTrayHoverPreferencesChanged()
    {
        if (changingTelemetryPreferences)
        {
            return;
        }
        Notify(nameof(TrayHoverLabelPreferences));
        NotifyTrayHoverLabel();
    }
}
