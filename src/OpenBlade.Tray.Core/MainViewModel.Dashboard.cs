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
        if (trayDashboardTelemetryPresentationActive)
        {
            NotifyTrayDashboardTelemetryValuesChanged();
        }
        if (dashboardTelemetryPresentationActive)
        {
            NotifyMainDashboardTelemetryValuesChanged();
        }
    }

    private void NotifyTrayDashboardTelemetryValuesChanged()
    {
        NotifyComputed(nameof(TrayDashboardBatteryPowerFlowLabel), TrayDashboardBatteryPowerFlowLabel);
        NotifyComputed(nameof(TrayDashboardTemperatureLabel), TrayDashboardTemperatureLabel);
        NotifyComputed(nameof(TrayDashboardFanSpeedLabel), TrayDashboardFanSpeedLabel);
        NotifyComputed(nameof(TrayDashboardFanSpeedToolTip), TrayDashboardFanSpeedToolTip);
    }

    private void NotifyMainDashboardTelemetryValuesChanged()
    {
        NotifyComputed(nameof(BatteryPowerFlowLabel), BatteryPowerFlowLabel);
        NotifyComputed(nameof(Fan1RpmLabel), Fan1RpmLabel);
        NotifyComputed(nameof(Fan2RpmLabel), Fan2RpmLabel);
        NotifyComputed(nameof(Fan1RpmValue), Fan1RpmValue);
        NotifyComputed(nameof(Fan2RpmValue), Fan2RpmValue);
        NotifyComputed(nameof(CpuTemperatureValue), CpuTemperatureValue);
        NotifyComputed(nameof(GpuTemperatureValue), GpuTemperatureValue);
        NotifyComputed(nameof(VisibleDashboardTelemetryGroups), VisibleDashboardTelemetryGroups);
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
