using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private WindowsBatteryHistoryViewModel BatteryHistoryViewModel { get; } =
        new(new SessionBatteryReportAdapter());

    private void OnBatteryHistoryPropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        RefreshBatteryHistoryPresentation();

    private void RefreshBatteryHistoryPresentation()
    {
        BatteryHistoryProgress.IsActive = BatteryHistoryViewModel.IsLoading;
        BatteryHistoryLoadButton.IsEnabled = !BatteryHistoryViewModel.IsLoading;
        BatteryHistoryError.Text = BatteryHistoryViewModel.Error ?? string.Empty;
        WindowsBatteryReport? report = BatteryHistoryViewModel.Report;
        BatteryHistorySummary.Text = report is null
            ? LocalizedResources.Get("BatteryHistory_NotLoaded", "Load Windows' battery report to inspect the last 24 hours.")
            : LocalizedResources.Format("BatteryHistory_SourceTime", "Windows battery report · Generated {0}", PresentationTimestamp.ForDisplay(report.GeneratedAtUtc).ToString("g"));
        BatteryHealthText.Text = report is null ? string.Empty : LocalizedResources.Format(
            "BatteryHistory_Health", "Full charge {0} · Design {1} · Health {2} · Cycles {3}",
            Capacity(report.Status.FullChargeCapacityMilliwattHours), Capacity(report.Status.DesignCapacityMilliwattHours),
            report.Status.HealthPercent is { } health ? $"{health:0.#}%" : Unavailable(),
            report.Status.CycleCount?.ToString() ?? Unavailable());
        BatteryHistoryRows.ItemsSource = report?.Intervals.Select(interval => FormatBatteryInterval(interval)).ToArray() ?? [];
        BatteryHistoryTimeline.Update(report);

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
        return LocalizedResources.Format("BatteryHistory_Interval", "{0} – {1} · {2} · {3} · Charge {4} · Discharged {5}",
            PresentationTimestamp.ForDisplay(interval.StartUtc).ToString("g"), PresentationTimestamp.ForDisplay(interval.EndUtc).ToString("t"),
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

    private async void OnPreviewLongBatteryLife(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(async () =>
        {
            DisplaySettingsSnapshot display = await displaySettingsAdapter.GetStateAsync(CancellationToken.None);
            LongBatteryLifePresetPlan plan = LongBatteryLifePreset.Preview(ViewModel.Profiles.Battery,
                ViewModel.Capabilities, display);
            ProfilePresentation before = Present(plan.Original);
            ProfilePresentation after = Present(plan.Candidate);
            var preview = new StackPanel { Spacing = 12 };
            preview.Children.Add(new TextBlock
            {
                Text = LocalizedResources.Get("BatteryPreset_Description", "Save these changes to the battery profile. Manual controls and application overrides keep their priority."),
                Style = (Style)Root.Resources["SecondaryTextStyle"],
                TextWrapping = TextWrapping.Wrap,
            });
            Add(before.PowerMode, after.PowerMode);
            Add(before.Fans, after.Fans);
            Add(before.LightingEffect, after.LightingEffect);
            Add(before.DisplayRefreshRate, after.DisplayRefreshRate);
            Add(before.DisplayBrightness, after.DisplayBrightness);
            if (!plan.HasChanges)
            {
                preview.Children.Add(new TextBlock
                {
                    Text = LocalizedResources.Get("BatteryPreset_NoChanges", "The supported battery settings already match this preset."),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = LocalizedResources.Get("BatteryPreset_Title", "Long battery life"),
                PrimaryButtonText = LocalizedResources.Get("BatteryPreset_Save", "Save to battery profile"),
                CloseButtonText = LocalizedResources.Get("BatteryPreset_Cancel", "Cancel"),
                Content = new ScrollViewer { Content = preview, MaxHeight = Math.Max(240, Root.ActualHeight - 220) },
                IsPrimaryButtonEnabled = plan.HasChanges && ViewModel.CanControlHardware,
            };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            preview.Children.Add(error);
            dialog.PrimaryButtonClick += async (_, click) =>
            {
                ContentDialogButtonClickDeferral deferral = click.GetDeferral();
                try
                {
                    if (!await ViewModel.SaveLongBatteryLifePresetAsync(plan, CancellationToken.None))
                    {
                        click.Cancel = true;
                        error.Text = LocalizedResources.Get("BatteryPreset_SaveFailed", "The battery profile changed or could not be saved. Close this preview and try again.");
                    }
                }
                finally { deferral.Complete(); }
            };
            _ = await ShowContentDialogAsync(dialog);

            ProfilePresentation Present(PowerProfile profile) => ProfilePresentationFactory.Create(profile,
                ViewModel.Profiles.WindowsPowerModes, LocalizedResources.Get("BatteryHistory_Battery", "Battery"),
                LaptopBehaviorProfileSettings.Unconfigured());
            void Add(ProfileValuePresentation original, ProfileValuePresentation candidate)
            {
                if (original.Text != candidate.Text)
                {
                    preview.Children.Add(new TextBlock
                    {
                        Text = LocalizedResources.Format("BatteryPreset_Change", "{0}: {1} → {2}",
                            candidate.SettingName, original.Text, candidate.Text),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
            }
        }, nameof(OnPreviewLongBatteryLife));
}
