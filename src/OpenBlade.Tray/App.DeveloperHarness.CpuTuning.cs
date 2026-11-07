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
        var wrappedCommands = new List<object>();
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
                    editor.CurveEnabled = true;
                    editor.CurveValue = -12;
                    await WaitForSnapshotLayoutAsync();
                    root.UpdateLayout();
                    RequireCpuLayout(card.Visibility == Visibility.Visible && card.ActualHeight > 0
                        && Math.Abs(card.ActualWidth - width) < 1, "CPU card was not laid out at the requested width.");
                    var elements = FindCpuLayoutDescendants(card).ToArray();
                    var summaries = new[]
                    {
                        (Path: nameof(editor.LiveSummary), Text: editor.LiveSummary),
                        (Path: nameof(editor.RecoverySummary), Text: editor.RecoverySummary),
                        (Path: nameof(editor.AvailabilitySummary), Text: editor.AvailabilitySummary),
                    };
                    var summaryRows = summaries.Select(summary =>
                    {
                        TextBlock row = elements.OfType<TextBlock>().Single(block =>
                            block.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding?.Path?.Path == summary.Path);
                        RequireCpuLayout(row.Text == summary.Text
                            && row.Visibility == (string.IsNullOrEmpty(summary.Text) ? Visibility.Collapsed : Visibility.Visible),
                            "CPU summary text or optional-row visibility disagrees with presentation state.");
                        return row;
                    }).ToArray();
                    var summaryPanel = (StackPanel)summaryRows[0].Parent;
                    TextBlock[] visibleSummaries = summaryRows.Where(row => row.Visibility == Visibility.Visible).ToArray();
                    double expectedSummaryHeight = visibleSummaries.Sum(row => row.ActualHeight)
                        + summaryPanel.Spacing * Math.Max(0, visibleSummaries.Length - 1);
                    RequireCpuLayout(Math.Abs(summaryPanel.ActualHeight - expectedSummaryHeight) < 2,
                        "Collapsed optional CPU summaries retained phantom row spacing.");
                    RequireCpuLayout(fixture.Measured ? editor.LiveSummary == "Measured running optimizer: -7."
                            : editor.LiveSummary == "Measured running optimizer: unknown.",
                        "Saved optimizer was substituted for measured running state.");
                    RequireCpuLayout(editor.CanEditCurve == fixture.Editable
                        && editor.CanApply == (fixture.Editable && fixture.Recovery != CpuTuningRecoveryState.Blocked && !fixture.OwnedPowerRestore)
                        && editor.CanRestore == fixture.Restorable,
                        "Synthetic backend or recovery admission was presented incorrectly.");
                    var apply = elements.OfType<Button>().Single(button => ReferenceEquals(button.Command, editor.ApplyCommand));
                    var restore = elements.OfType<Button>().Single(button => ReferenceEquals(button.Command, editor.RestoreCommand));
                    RequireCpuLayout(apply.IsEnabled == (fixture.Editable && fixture.Recovery != CpuTuningRecoveryState.Blocked && !fixture.OwnedPowerRestore)
                        && (restore.Visibility == Visibility.Visible) == fixture.Restorable,
                        "Rendered apply/restore controls disagree with presentation admission.");
                    var controlGeometry = elements.OfType<FrameworkElement>()
                        .Where(element => element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0
                            && (element is CheckBox or Slider or ToggleSwitch
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
                    if (width == 520 && fixture.Restorable)
                    {
                        Button[] commands = [elements.OfType<Button>().Single(button => ReferenceEquals(button.Command, editor.RefreshCommand)), apply, restore];
                        string[] labels = ["Обновить состояние оптимизатора", "Применить оптимизатор сейчас", "Восстановить прежние значения оптимизатора"];
                        object[] content = commands.Select(button => button.Content).ToArray();
                        var bindings = commands.Select(button => button.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding).ToArray();
                        for (int index = 0; index < commands.Length; index++) { commands[index].Content = labels[index]; }
                        await WaitForSnapshotLayoutAsync();
                        root.UpdateLayout();
                        for (int index = 0; index < commands.Length; index++)
                        {
                            Button button = commands[index];
                            TextBlock label = FindCpuLayoutDescendants(button).OfType<TextBlock>().Single(block => block.Text == labels[index]);
                            var origin = button.TransformToVisual(card).TransformPoint(new Windows.Foundation.Point());
                            RequireCpuLayout(label.TextWrapping == TextWrapping.Wrap && label.ActualHeight > 30 && !label.IsTextTrimmed
                                && origin.X >= -1 && origin.X + button.ActualWidth <= card.ActualWidth + 1,
                                "Long CPU command labels did not wrap within their native buttons.");
                            wrappedCommands.Add(new { text = labels[index], width = button.ActualWidth, height = button.ActualHeight, labelHeight = label.ActualHeight });
                        }
                        await SaveSnapshotAsync(card, Path.Combine(directory, "live-curve-recoverable-wrapped-520.png"));
                        for (int index = 0; index < commands.Length; index++)
                        {
                            if (bindings[index] is { } binding) { commands[index].SetBinding(ContentControl.ContentProperty, binding); }
                            else { commands[index].Content = content[index]; }
                        }
                        await WaitForSnapshotLayoutAsync();
                    }
                    observations.Add(new
                    {
                        fixture.Name,
                        width,
                        image,
                        cardHeight = card.ActualHeight,
                        editor.LiveSummary,
                        editor.RecoverySummary,
                        editor.AvailabilitySummary,
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
                wrappedCommandImage = "live-curve-recoverable-wrapped-520.png",
                wrappedCommands,
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
        yield return new("live-missing-backend", false, false, false, false, CpuTuningRecoveryState.None);
        yield return new("live-available", true, true, true, false, CpuTuningRecoveryState.None);
        yield return new("live-unknown-measurement", true, false, false, false, CpuTuningRecoveryState.None);
        yield return new("live-recovery-blocked", true, true, true, false, CpuTuningRecoveryState.Blocked);
        yield return new("live-curve-recoverable", true, true, true, true, CpuTuningRecoveryState.RestoreReady);
        yield return new("live-mixed-recovery", true, true, true, false, CpuTuningRecoveryState.RestoreReady, true);
    }
    private sealed record CpuLayoutCase(string Name, bool BackendAvailable, bool Editable,
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
                unavailable, fixture.BackendAvailable ? available : unavailable,
                CpuTuningPendingBoot.Unknown,
                fixture.Recovery is CpuTuningRecoveryState.Blocked or CpuTuningRecoveryState.RestoreReady)
            { LiveRecovery = recovery },
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
