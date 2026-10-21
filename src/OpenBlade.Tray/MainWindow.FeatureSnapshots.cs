#if OPENBLADE_DEVELOPER_HARNESS
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenBlade.Contracts;
using OpenBlade.Session;
using Button = Microsoft.UI.Xaml.Controls.Button;
using ComboBox = Microsoft.UI.Xaml.Controls.ComboBox;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using Orientation = Microsoft.UI.Xaml.Controls.Orientation;
using TextBox = Microsoft.UI.Xaml.Controls.TextBox;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private static readonly DateTimeOffset FeatureSnapshotTime =
        new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    internal void ShowLocalizationSnapshotFeature(string destination)
    {
        if (!localizationSnapshotMode)
        {
            throw new InvalidOperationException(
                "Feature sample data requires disconnected snapshot mode.");
        }

        switch (destination)
        {
            case "profiles-applications":
                ApplyApplicationProfileFixture(populated: true);
                ApplicationProfilesSection.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
                break;
            case "profiles-applications-empty":
                ApplyApplicationProfileFixture(populated: false);
                ApplicationProfilesSection.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
                break;
            case "profiles-application-editor":
                ApplyApplicationProfileFixture(populated: true);
                ApplyDisplayFixture(brightnessAvailable: true);
                ApplicationProfilesSection.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
                break;
            case "telemetry":
                ApplyTelemetryFixture(populated: true);
                break;
            case "telemetry-empty":
                ApplyTelemetryFixture(populated: false);
                break;
            case "laptop-battery-history":
                ApplyBatteryHistoryFixture();
                BatteryHistorySection.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
                break;
            case "display-brightness-known":
                ApplyDisplayFixture(brightnessAvailable: true);
                break;
            case "display-brightness-unavailable":
                ApplyDisplayFixture(brightnessAvailable: false);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(destination), destination, "Unknown feature snapshot route.");
        }
    }

    internal Flyout CreateLocalizationSnapshotApplicationEditor()
    {
        if (!localizationSnapshotMode)
        {
            throw new InvalidOperationException(
                "The application editor fixture requires disconnected snapshot mode.");
        }

        var draft = new ApplicationProfileDraft
        {
            Name = "Creative rendering and competitive game profile with a long localized name",
            ExecutablePath = @"C:\Program Files\OpenBlade Snapshot Fixtures\Very Long Application Folder\Creative Rendering and Competitive Game.exe",
            ActivationMode = ApplicationActivationMode.WhileRunningAfterActivation,
        };
        draft.SetSource(PowerSource.PluggedIn, new ApplicationPowerProfileOverride
        {
            Performance = PerformancePreset.Performance,
            Fans = FanMode.Automatic,
            GamingModeEnabled = true,
            Display = new DisplayProfileSettings("fixed-60", false, "srgb")
            {
                BrightnessPercent = 65,
            },
        });

        var editor = new StackPanel();
        _ = BuildApplicationSourceEditor(editor, draft, PowerSource.PluggedIn);
        var panel = new StackPanel
        {
            Spacing = 12,
            Width = 400,
        };
        panel.Children.Add(new TextBlock
        {
            Text = LocalizedResources.Get("Feature_ac3492bfbd41", "Application profile"),
            Style = (Style)Root.Resources["CardTitleStyle"],
        });
        panel.Children.Add(new TextBlock
        {
            Text = LocalizedResources.Get(
                "Feature_7837e2a6dec5",
                "Match one exact executable. Unset values inherit the current power-source profile. Manual controls keep priority. The most recently activated running application stays active after you switch away."),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Root.Resources["SecondaryTextStyle"],
        });
        panel.Children.Add(new TextBox
        {
            Header = LocalizedResources.Get("Feature_6cc5df35f033", "Name"),
            Text = draft.Name,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBox
        {
            Header = LocalizedResources.Get("Feature_af4123249281", "Executable path"),
            Text = draft.ExecutablePath,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new ComboBox
        {
            Header = LocalizedResources.Get("Feature_f8ed8576f5a9", "Activation"),
            ItemsSource = new[]
            {
                LocalizedResources.Get(
                    "Feature_0d94e35e8475",
                    "Until the activated process exits"),
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });
        panel.Children.Add(new ComboBox
        {
            Header = LocalizedResources.Get("Feature_061b9b48e177", "Power source"),
            ItemsSource = new[]
            {
                LocalizedResources.Get("Feature_877cb5a94232", "Plugged in"),
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });
        panel.Children.Add(editor);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        actions.Children.Add(new Button
        {
            Content = LocalizedResources.Get("Feature_c0774c185060", "Cancel"),
            IsEnabled = false,
        });
        actions.Children.Add(new Button
        {
            Content = LocalizedResources.Get("Feature_c1db83d165ed", "Save"),
            IsEnabled = false,
        });
        panel.Children.Add(actions);

        var presenterStyle = new Style(typeof(FlyoutPresenter))
        {
            BasedOn = (Style)Microsoft.UI.Xaml.Application.Current.Resources["DefaultFlyoutPresenterStyle"],
        };
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 1720d));
        return new Flyout
        {
            FlyoutPresenterStyle = presenterStyle,
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 1640,
            },
        };
    }

    private void ApplyApplicationProfileFixture(bool populated)
    {
        ProfilesComparisonSection.Visibility = Visibility.Collapsed;
        var profile = new ApplicationProfileDefinition(
            new Guid("750BA955-D609-47A3-87B9-3B635F6D86C2"),
            "Creative rendering and competitive game profile with a long localized name",
            @"C:\Program Files\OpenBlade Snapshot Fixtures\Very Long Application Folder\Creative Rendering and Competitive Game.exe",
            ApplicationActivationMode.WhileRunningAfterActivation,
            new ApplicationProfileOverrideSet
            {
                PluggedIn = new ApplicationPowerProfileOverride
                {
                    Performance = PerformancePreset.Performance,
                    Fans = FanMode.Automatic,
                    GamingModeEnabled = true,
                },
                Battery = new ApplicationPowerProfileOverride
                {
                    Performance = PerformancePreset.BatterySaver,
                },
            });
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            ApplicationProfiles = populated ? [profile] : [],
        };
        ApplySnapshotStatus(profiles, populated ? profile.Id : null);
        RefreshApplicationProfiles();
    }

    private void ApplySnapshotStatus(ProfileSet profiles, Guid? activeApplicationProfileId)
    {
        var status = new ServiceStatusSnapshot(
            41,
            DeviceVerified: true,
            WritesBlocked: false,
            PotentialControllers: [],
            ControllerDetectionWarning: null,
            ExternalChanges: [],
            Message: "Disconnected feature snapshot fixture",
            Capabilities: DeviceCapabilities.SafeTargetDefaults(),
            ConfirmedState: new ConfirmedDeviceState(
                PerformancePreset.Balanced,
                FanMode.Automatic,
                new ConfirmedBatteryState(true, 80, false),
                null),
            Profiles: profiles,
            DesiredState: new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Balanced,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(
                    FanMode.Automatic,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(
                    profiles.PluggedIn.Lighting,
                    SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            ActivePowerSource: PowerSource.PluggedIn,
            LiveReadings: null,
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false)
        {
            ActiveApplicationProfileId = activeApplicationProfileId,
        };
        MethodInfo applyStatus = typeof(MainViewModel).GetMethod(
            "ApplyStatus",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Status presentation hook was not found.");
        applyStatus.Invoke(ViewModel, [status, false]);
    }

    private void ApplyTelemetryFixture(bool populated)
    {
        TelemetryRange.SelectedIndex = 3;
        if (populated)
        {
            FieldInfo historyField = typeof(MainViewModel).GetField(
                "telemetryHistory",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Telemetry history fixture hook was not found.");
            var history = (TelemetryHistory)(historyField.GetValue(ViewModel)
                ?? throw new InvalidOperationException("Telemetry history was unavailable."));
            TelemetrySample[] samples =
            [
                Sample(0, PowerSource.Battery, "Battery profile", 42_000, 2_350, 2_280, 67, 59),
                Sample(2, PowerSource.Battery, "Creative rendering profile", 48_500, 2_780, 2_690, 73, 64),
                Sample(20, PowerSource.PluggedIn, "Creative rendering profile", -18_000, 3_120, 3_050, 78, 69),
                Sample(22, PowerSource.PluggedIn, "Plugged-in profile", -24_000, 2_600, 2_520, 70, 62),
            ];
            foreach (TelemetrySample sample in samples)
            {
                history.AddSample(sample);
            }
            FieldInfo latestField = typeof(MainViewModel).GetField(
                "latestLiveTelemetrySample",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Latest telemetry fixture hook was not found.");
            latestField.SetValue(ViewModel, samples[^1]);
        }
        RefreshTelemetryPresentation();

        static TelemetrySample Sample(
            int seconds,
            PowerSource source,
            string profile,
            int power,
            int fan1,
            int fan2,
            int cpu,
            int gpu) => new(
                FeatureSnapshotTime.AddSeconds(seconds),
                seconds + 1,
                TelemetrySource.LiveService,
                source,
                profile,
                power,
                fan1,
                fan2,
                cpu,
                gpu);
    }

    private void ApplyBatteryHistoryFixture()
    {
        DateTimeOffset start = FeatureSnapshotTime.AddHours(-24);
        var report = new WindowsBatteryReport(
            WindowsBatteryReportSource.WindowsPowerCfg,
            FeatureSnapshotTime,
            start,
            FeatureSnapshotTime,
            new WindowsBatteryStatus(95_200, 82_600, 86.8, 184),
            [
                Interval(0, 4, WindowsBatteryPowerSource.Battery,
                    WindowsBatteryActivity.Active, 86, 11_700),
                Interval(4, 6, WindowsBatteryPowerSource.Unknown,
                    WindowsBatteryActivity.Unknown, null, null),
                Interval(6, 12, WindowsBatteryPowerSource.Ac,
                    WindowsBatteryActivity.Active, 61, null),
                Interval(12, 17, WindowsBatteryPowerSource.Battery,
                    WindowsBatteryActivity.Suspended, 96, null),
                Interval(17, 24, WindowsBatteryPowerSource.Battery,
                    WindowsBatteryActivity.Active, 94, 18_900),
            ]);
        FieldInfo reportField = typeof(WindowsBatteryHistoryViewModel).GetField(
            "report",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Battery report fixture hook was not found.");
        reportField.SetValue(BatteryHistoryViewModel, report);
        RefreshBatteryHistoryPresentation();

        WindowsBatteryReportInterval Interval(
            int startHour,
            int endHour,
            WindowsBatteryPowerSource source,
            WindowsBatteryActivity activity,
            double? chargePercent,
            long? discharged) => new(
                start.AddHours(startHour),
                start.AddHours(endHour),
                source,
                activity,
                chargePercent is { } percent ? (long)(82_600 * percent / 100) : null,
                82_600,
                discharged,
                chargePercent);
    }

    private void ApplyDisplayFixture(bool brightnessAvailable)
    {
        DisplayViewModel.ActivePowerSource = PowerSource.PluggedIn;
        DisplayViewModel.ProfilePersistenceAvailable = true;
        var snapshot = new DisplaySettingsSnapshot(
            IsAvailable: true,
            RefreshRateOptions:
            [
                new DisplayRefreshRateOption("fixed-60", "60 Hz"),
                new DisplayRefreshRateOption("dynamic-48-240", "Dynamic (48–240 Hz)", true),
            ],
            SelectedRefreshRateKey: "dynamic-48-240",
            IsHdrSupported: true,
            IsHdrEnabled: false,
            ColorProfileOptions:
            [
                new DisplayColorProfileOption("native", "Native"),
                new DisplayColorProfileOption("srgb", "sRGB"),
            ],
            SelectedColorProfileKey: "srgb",
            DisplayName: "Built-in Razer display",
            Width: 2560,
            Height: 1600,
            BitsPerColorChannel: 10)
        {
            BrightnessPercent = brightnessAvailable ? (byte)65 : null,
            SupportedBrightnessLevels = brightnessAvailable
                ? [0, 20, 35, 50, 65, 80, 100]
                : [],
        };
        MethodInfo apply = typeof(DisplaySettingsViewModel).GetMethod(
            "Apply",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Display presentation hook was not found.");
        apply.Invoke(DisplayViewModel, [snapshot, true]);
    }
}
#endif
