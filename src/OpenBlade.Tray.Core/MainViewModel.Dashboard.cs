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
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(EnabledDashboardTelemetryItems)));
        if (dashboardTelemetryPresentationActive)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(VisibleDashboardTelemetryGroups)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(ShowDashboardLiveReadings)));
        }
    }

    private void NotifyDashboardTelemetryValuesChanged()
    {
        if (!dashboardTelemetryPresentationActive)
        {
            return;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BatteryPowerFlowLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fan1RpmLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fan2RpmLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fan1RpmValue)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fan2RpmValue)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CpuTemperatureValue)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GpuTemperatureValue)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(VisibleDashboardTelemetryGroups)));
    }

    private void NotifyTrayHoverPreferencesChanged()
    {
        if (changingTelemetryPreferences)
        {
            return;
        }
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(TrayHoverLabelPreferences)));
        NotifyTrayHoverLabel();
    }
}
