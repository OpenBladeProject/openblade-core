#if OPENBLADE_DEVELOPER_HARNESS
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenBlade.Contracts;
using Button = Microsoft.UI.Xaml.Controls.Button;
using CheckBox = Microsoft.UI.Xaml.Controls.CheckBox;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;

namespace OpenBlade.Tray;

public partial class App
{
    private const string CpuLayoutEvidenceRoot = @"C:\OpenBlade\validation-archive\cpu-tuning-re\ui-validation";

    // This isolated synthetic presentation path never calls InitializeAsync,
    // StartAsync, service-client methods, CPU commands or preference/profile saves.
    // A positive UI state below is a fixture, never physical backend admission.
    private async Task RunCpuTuningLayoutSmokeAsync()
    {
        string directory = Path.Combine(CpuLayoutEvidenceRoot, Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        var observations = new List<object>();
        try
        {
            Directory.CreateDirectory(directory);
            System.Windows.Forms.Application.EnableVisualStyles();
            window = MainWindow.CreateLocalizationSnapshot();
            FrameworkElement root = window.Content as FrameworkElement
                ?? throw new InvalidOperationException("CPU snapshot root unavailable.");
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            root.Loaded += (_, _) => loaded.TrySetResult(true);
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1050));
            ShowNoninteractiveSnapshotWindow(window);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            window.SelectLocalizationSnapshotPage("laptop");
            await WaitForSnapshotLayoutAsync();
            Border card = FindCpuLayoutDescendants(root).OfType<Border>().Single(element => element.Name == "CpuTuningCard");
            // Keep the isolated card capture legible outside the parent surface.
            card.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
            MethodInfo applyStatus = typeof(MainViewModel).GetMethod("ApplyStatus", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Synthetic status presentation hook unavailable.");
            long revision = 0;
            foreach (int width in new[] { 760, 520 })
            {
                card.Width = width;
                card.HorizontalAlignment = HorizontalAlignment.Left;
                foreach (CpuLayoutCase fixture in CpuLayoutCases())
                {
                    applyStatus.Invoke(window.ViewModel, [CpuLayoutStatus(++revision, fixture), false]);
                    CpuTuningViewModel editor = window.ViewModel.CpuTuning;
                    // Editor intent changes raise presentation notifications only.
                    editor.LiveNow = fixture.Live;
                    editor.ManagePower = false;
                    editor.ManageCurve = true;
                    editor.CurveEnabled = true;
                    editor.CurveValue = -12;
                    await WaitForSnapshotLayoutAsync();
                    root.UpdateLayout();
                    RequireCpuLayout(card.Visibility == Visibility.Visible && card.ActualHeight > 0
                        && Math.Abs(card.ActualWidth - width) < 1, "CPU card was not laid out at the requested width.");
                    var elements = FindCpuLayoutDescendants(card).ToArray();
                    var text = elements.OfType<TextBlock>().Where(block => block.Visibility == Visibility.Visible)
                        .Select(block => block.Text).ToArray();
                    RequireCpuLayout(text.Contains(editor.SavedSummary) && text.Contains(editor.LiveSummary)
                        && text.Contains(editor.PendingBootSummary) && text.Contains(editor.AvailabilitySummary),
                        "CPU card did not render the separate saved/live/pending/backend summaries.");
                    RequireCpuLayout(editor.SavedSummary.Contains("65", StringComparison.Ordinal)
                        && (fixture.Measured ? editor.LiveSummary == "Measured live: PPT Unknown; CO -7."
                            : editor.LiveSummary == "Measured live: unknown."),
                        "Saved firmware was substituted for measured live state or unknown PPT was lost.");
                    RequireCpuLayout(editor.CanEditCurve == fixture.Editable && !editor.CanEditPower
                        && editor.CanApply == (fixture.Editable && fixture.Recovery != CpuTuningRecoveryState.Blocked)
                        && editor.CanRestore == fixture.Restorable,
                        "Synthetic backend or recovery admission was presented incorrectly.");
                    var apply = elements.OfType<Button>().Single(button => Equals(button.Content, editor.ApplyLabel));
                    var restore = elements.OfType<Button>().Single(button => Equals(button.Content, "Restore prior values"));
                    RequireCpuLayout(apply.IsEnabled == (fixture.Editable && fixture.Recovery != CpuTuningRecoveryState.Blocked)
                        && (restore.Visibility == Visibility.Visible) == fixture.Restorable,
                        "Rendered apply/restore controls disagree with presentation admission.");
                    var controlGeometry = elements.OfType<FrameworkElement>()
                        .Where(element => element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0
                            && (element is CheckBox or NumberBox or ToggleSwitch
                                || element is Button button && (ReferenceEquals(button.Command, editor.ApplyCommand)
                                    || ReferenceEquals(button.Command, editor.RefreshCommand) || ReferenceEquals(button.Command, editor.RestoreCommand))))
                        .Select(element =>
                        {
                            var origin = element.TransformToVisual(card).TransformPoint(new Windows.Foundation.Point());
                            RequireCpuLayout(origin.X >= -1 && origin.X + element.ActualWidth <= card.ActualWidth + 1,
                                "CPU control extends beyond the card at a tested width.");
                            return new { type = element.GetType().Name, x = origin.X, y = origin.Y, width = element.ActualWidth, height = element.ActualHeight };
                        }).ToArray();
                    string image = $"{fixture.Name}-{width}.png";
                    await SaveSnapshotAsync(card, Path.Combine(directory, image));
                    observations.Add(new
                    {
                        fixture.Name,
                        width,
                        image,
                        cardHeight = card.ActualHeight,
                        editor.SavedSummary,
                        editor.LiveSummary,
                        editor.PendingBootSummary,
                        editor.AvailabilitySummary,
                        editor.CanEditPower,
                        editor.CanEditCurve,
                        editor.CanApply,
                        editor.CanRestore,
                        controls = controlGeometry,
                    });
                }
            }
            WriteCpuLayoutReport(directory, new
            {
                schemaVersion = 1,
                result = "PASS",
                evidence = "SyntheticRenderedUiOnly",
                model = "RZ09-0528",
                sku = "1532:02C6",
                physicalAdmission = false,
                serviceClientStarted = false,
                cpuCommandsInvoked = false,
                preferencesSaved = false,
                observations,
            });
            // Product Close hides the window and starts hidden observation;
            // terminate the isolated harness directly instead of entering it.
            Environment.Exit(0);
        }
        catch (Exception exception)
        {
            // Retain any already captured frames. Failure is bounded and private.
            try
            {
                WriteCpuLayoutReport(directory, new
                {
                    schemaVersion = 1,
                    result = "FAIL",
                    evidence = "SyntheticRenderedUiOnly",
                    failureType = exception.GetType().Name,
                    failure = exception.Message.Length <= 2048 ? exception.Message : exception.Message[..2048],
                    observations,
                });
            }
            finally
            {
                Environment.Exit(1);
            }
        }
    }

    private static IEnumerable<CpuLayoutCase> CpuLayoutCases()
    {
        yield return new("saved-unavailable", false, false, false, false, false, CpuTuningRecoveryState.None);
        yield return new("saved-editable-pending-boot", false, true, true, false, false, CpuTuningRecoveryState.None);
        yield return new("saved-editable-restore-ready", false, true, true, false, true, CpuTuningRecoveryState.RestoreReady);
        yield return new("live-observed-unknown-ppt", true, false, false, true, false, CpuTuningRecoveryState.None);
        yield return new("live-editable-restricted-restore", true, true, true, true, false, CpuTuningRecoveryState.Blocked);
        yield return new("live-editable-restore-ready", true, true, true, true, true, CpuTuningRecoveryState.RestoreReady);
        yield return new("live-owned-ppt-restore-restricted", true, true, true, true, false, CpuTuningRecoveryState.RestoreReady, true);
    }
    private sealed record CpuLayoutCase(string Name, bool Live, bool BackendAvailable, bool Editable,
        bool Measured, bool Restorable, CpuTuningRecoveryState Recovery, bool OwnedPowerRestore = false);

    private static ServiceStatusSnapshot CpuLayoutStatus(long revision, CpuLayoutCase fixture)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        var available = new CpuTuningBackendStatus(DeviceFeatureAccess.ReadWrite, CpuTuningBackendAvailability.Available);
        var unavailable = new CpuTuningBackendStatus(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Unavailable);
        var recovery = new CpuTuningRecoveryStatus(fixture.Recovery,
            fixture.Recovery is CpuTuningRecoveryState.None ? CpuTuningFeatures.None
                : fixture.OwnedPowerRestore ? CpuTuningFeatures.PowerLimit | CpuTuningFeatures.CurveOptimizer : CpuTuningFeatures.CurveOptimizer);
        return new(revision, true, false, [], null, [], "Synthetic CPU layout fixture",
            DeviceCapabilities.SafeTargetDefaults() with
            {
                CpuPowerLimit = new(new(45, 95, 1), DeviceFeatureAccess.ReadOnly),
                CpuVoltageOptimizer = new(new(-30, -1, 1), DeviceFeatureAccess.ReadWrite) { SupportsEnableDisable = true },
            },
            new(PerformancePreset.Balanced, FanMode.Automatic, null, null), profiles,
            new(new(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new(FanMode.Automatic, SettingValueSource.PowerProfile),
                new(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile), new(profiles.BatteryPolicy, true, false)),
            PowerSource.PluggedIn, null, null, null, null, false,
            Firmware: new("Synthetic", "Synthetic", "Synthetic", "Synthetic", "RZ09-0528"))
        {
            CpuTuning = new(new(65000, 2, 2, new(CpuCurveOptimizerMode.AllCore, -10, [])),
                fixture.Measured ? new(null, new[] { 0, 2, 4, 6, 8, 9, 10, 11, 12, 13, 14, 15 }
                    .Select(core => new CpuCoreCurveOptimizerValue(core, -7)).ToArray()) : null,
                fixture.Live ? unavailable : fixture.BackendAvailable ? available : unavailable,
                fixture.Live && fixture.BackendAvailable ? available : unavailable,
                fixture.Live ? CpuTuningPendingBoot.Unknown : CpuTuningPendingBoot.Requested,
                fixture.Recovery is CpuTuningRecoveryState.Blocked or CpuTuningRecoveryState.RestoreReady)
            { LiveRecovery = fixture.Live ? recovery : null, FirmwareRecovery = fixture.Live ? null : recovery },
        };
    }
    private static IEnumerable<DependencyObject> FindCpuLayoutDescendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { foreach (DependencyObject child in FindCpuLayoutDescendants(VisualTreeHelper.GetChild(root, index))) { yield return child; } }
    }
    private static void RequireCpuLayout(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
    private static void WriteCpuLayoutReport(string directory, object report)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report);
        if (bytes.Length > 32768) { throw new InvalidOperationException("CPU UI report exceeds its bound."); }
        using var stream = new FileStream(Path.Combine(directory, "report.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes); stream.Flush(true);
    }
}
#endif
