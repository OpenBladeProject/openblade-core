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
            case "profiles-application-editor-controls":
                ApplyApplicationProfileFixture(populated: true);
                ApplyDisplayFixture(brightnessAvailable: true);
                ShowLocalizationSnapshotApplicationEditorFixture();
                break;
            case "profiles-application-discovery":
            case "profiles-application-discovery-empty":
                ApplyApplicationProfileFixture(populated: false);
                ShowLocalizationSnapshotApplicationDiscoveryFixture(destination != "profiles-application-discovery-empty");
                break;
            case "telemetry":
                ApplyTelemetryFixture(populated: true);
                TelemetryPlot.ShowLocalizationSnapshotInspection(0.4, focusNextSampleButton: true);
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

    private void ShowLocalizationSnapshotApplicationEditorFixture()
    {
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
            Display = new DisplayProfileSettings("fixed-60", false, "srgb") { BrightnessPercent = 65 },
        });
        ShowLocalizationSnapshotApplicationEditor(draft);
    }

    internal void ShowLocalizationSnapshotApplicationEditorControls() =>
        ApplicationProfileEditorPage.ChangeView(
            null, ApplicationProfileEditorPage.ScrollableHeight, null, disableAnimation: true);

    internal void VerifyLocalizationSnapshotApplicationEditorLock()
    {
        ApplicationProfileEditorForm.IsEnabled = false;
        if (applicationProfileNameEditor is null || applicationProfileNameEditor.IsEnabled
            || !ApplicationProfileEditorBackButton.IsEnabled || !ApplicationProfileCancelButton.IsEnabled)
        {
            throw new InvalidOperationException("The saving form must lock its fields while keeping Back and Cancel available.");
        }
        ApplicationProfileEditorForm.IsEnabled = true;
        if (!applicationProfileNameEditor.IsEnabled)
        {
            throw new InvalidOperationException("The application form did not unlock.");
        }
    }

    private void ShowLocalizationSnapshotApplicationDiscoveryFixture(bool populated)
    {
        IReadOnlyList<InstalledApplicationCandidate> candidates = populated
            ? [
                new("Elden Ring", @"C:\Games\Elden Ring\Game\eldenring.exe", "Steam", @"C:\Games\Elden Ring"),
                new("Elden Ring", @"C:\Games\Elden Ring\Game\eldenring.exe", "Steam", @"C:\Games\Elden Ring"),
                new("Baldur's Gate 3", @"C:\Games\Baldurs Gate 3\bin\bg3.exe", "Steam", @"C:\Games\Baldurs Gate 3"),
                new("Baldur's Gate 3", @"C:\Games\Baldurs Gate 3\bin\bg3_dx11.exe", "Steam", @"C:\Games\Baldurs Gate 3"),
                new("Control Ultimate Edition", @"C:\Games\Control\Control_DX12.exe", "Epic Games", @"C:\Games\Control"),
                new("Creative rendering and competitive game with a long installed title", @"C:\XboxGames\Creative Rendering\Content\Game.exe", "Xbox", @"C:\XboxGames\Creative Rendering"),
            ] : [];
        ShowLocalizationSnapshotApplicationDiscovery(candidates);
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
                Sample(14, PowerSource.Battery, "Creative rendering profile", 45_000, 2_920, 2_840, 75, 66)
                    with { CpuTemperatureCelsius = null, Fan2Rpm = null },
                Sample(20, PowerSource.PluggedIn, "Creative rendering profile", -18_000, 3_120, 3_050, 78, 69),
                Sample(22, PowerSource.PluggedIn, "Plugged-in profile", -24_000, 2_600, 2_520, 70, 62),
                Sample(28, PowerSource.PluggedIn, "Plugged-in profile", -22_000, 2_450, 2_400, 68, 61),
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
        RefreshTelemetryPresentation(rangeEndOverride: FeatureSnapshotTime.AddSeconds(35));

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
