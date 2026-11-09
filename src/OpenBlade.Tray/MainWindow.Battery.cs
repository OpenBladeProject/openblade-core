using System.ComponentModel;
using Microsoft.UI.Xaml;
using OpenBlade.Session;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private WindowsBatteryHistoryViewModel BatteryHistoryViewModel { get; } =
        new(new SessionBatteryReportAdapter());
    private WindowsBatteryReport? presentedBatteryReport;

    private void OnBatteryHistoryPropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        RefreshBatteryHistoryPresentation();

    private void RefreshBatteryHistoryPresentation()
    {
        BatteryHistoryProgress.IsActive = BatteryHistoryViewModel.IsLoading;
        BatteryHistoryProgress.Visibility = BatteryHistoryViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        BatteryHistoryLoadButton.IsEnabled = !BatteryHistoryViewModel.IsLoading;
        BatteryHistoryError.Text = BatteryHistoryViewModel.Error ?? string.Empty;
        BatteryHistoryError.Visibility = string.IsNullOrWhiteSpace(BatteryHistoryViewModel.Error) ? Visibility.Collapsed : Visibility.Visible;
        WindowsBatteryReport? report = BatteryHistoryViewModel.Report;
        BatteryHistoryReportBody.Visibility = report is null ? Visibility.Collapsed : Visibility.Visible;
        if (ReferenceEquals(report, presentedBatteryReport))
        {
            return;
        }
        IReadOnlyList<WindowsBatteryReportInterval> intervals = report is null
            ? [] : WindowsBatteryReportCoverage.CreateIntervals(report);
        BatteryHistorySummary.Text = report is null
            ? string.Empty
            : LocalizedResources.Format("BatteryHistory_SourceTime", "Windows battery report · Generated {0}", PresentationTimestamp.ForDisplay(report.GeneratedAtUtc).ToString("g"));
        BatteryHealthText.Text = report is null ? string.Empty : LocalizedResources.Format(
            "BatteryHistory_Health", "Full charge {0} · Design {1} · Health {2} · Cycles {3}",
            Capacity(report.Status.FullChargeCapacityMilliwattHours), Capacity(report.Status.DesignCapacityMilliwattHours),
            report.Status.HealthPercent is { } health ? $"{health:0.#}%" : Unavailable(),
            report.Status.CycleCount?.ToString() ?? Unavailable());
        BatteryHistoryRows.ItemsSource = intervals.Select(FormatBatteryInterval).ToArray();
        BatteryHistoryTimeline.Update(report, intervals);
        presentedBatteryReport = report;

        static string Capacity(long? value) => value is { } capacity ? $"{capacity / 1000d:0.##} Wh" : Unavailable();
        static string Unavailable() => LocalizedResources.Get("BatteryHistory_Unknown", "Unknown");
    }

    private static string FormatBatteryInterval(WindowsBatteryReportInterval interval)
    {
        string source = interval.PowerSource switch
        {
            WindowsBatteryPowerSource.Battery => LocalizedResources.Get("BatteryHistory_Battery", "Battery"),
            WindowsBatteryPowerSource.Ac => LocalizedResources.Get("BatteryHistory_AC", "AC power"),
            _ => LocalizedResources.Get("BatteryHistory_Unknown", "Unknown"),
        };
        string activity = interval.Activity switch
        {
            WindowsBatteryActivity.Active => LocalizedResources.Get("BatteryHistory_Active", "Active"),
            WindowsBatteryActivity.ConnectedStandby => LocalizedResources.Get("BatteryHistory_Standby", "Connected standby"),
            WindowsBatteryActivity.Suspended => LocalizedResources.Get("BatteryHistory_Suspended", "Suspended"),
            _ => LocalizedResources.Get("BatteryHistory_Unknown", "Unknown"),
        };
        string charge = interval.StartChargePercent is { } percent ? $"{percent:0.#}%"
            : LocalizedResources.Get("BatteryHistory_Unknown", "Unknown");
        string discharge = interval.DischargeMilliwattHours is { } energy ? $"{energy / 1000d:0.##} Wh"
            : LocalizedResources.Get("BatteryHistory_Unknown", "Unknown");
        DateTimeOffset start = PresentationTimestamp.ForDisplay(interval.StartUtc);
        DateTimeOffset end = PresentationTimestamp.ForDisplay(interval.EndUtc);
        return LocalizedResources.Format("BatteryHistory_Interval", "{0} – {1} · {2} · {3} · Start charge {4} · Discharged {5}",
            start.ToString("g"), PresentationTimestamp.FormatIntervalEnd(start, end),
            source, activity, charge, discharge);
    }

    private async void OnLoadBatteryHistory(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(async () =>
        {
            if (!localizationSnapshotMode)
            {
                await BatteryHistoryViewModel.RefreshAsync();
            }
            RefreshBatteryHistoryPresentation();
        }, nameof(OnLoadBatteryHistory));

}
