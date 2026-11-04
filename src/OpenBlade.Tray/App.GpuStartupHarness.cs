#if OPENBLADE_DEVELOPER_HARNESS
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public partial class App
{
    private static async Task RunGpuStartupSmokeAsync(string reportPath)
    {
        var windows = new List<MainWindow>();
        var dashboards = new List<TrayDashboardWindow>();
        Exception? failure = null;
        try
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            foreach (bool delayedRanges in new[] { false, true })
            {
                File.WriteAllText(reportPath, $"GPU startup: delayed ranges={delayedRanges}");
                IServiceClient client = DispatchProxy.Create<IServiceClient, GpuStartupServiceClientProxy>();
                var recorder = (GpuStartupServiceClientProxy)client;
                MainWindow window = MainWindow.CreateLocalizationSnapshot(client);
                windows.Add(window);
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 820));
                ShowNoninteractiveSnapshotWindow(window);
                window.SelectLocalizationSnapshotPage("laptop");
                var dashboard = new TrayDashboardWindow(
                    window.ViewModel,
                WinRT.Interop.WindowNative.GetWindowHandle(window.HostWindow),
                    _ => { });
                dashboards.Add(dashboard);
                TrayDashboardPreference defaults = TrayDashboardPreferenceDefaults.Preference;
                dashboard.ApplyPreferences(defaults with
                {
                    Sliders = defaults.Sliders!
                        .Select(slider => slider with
                        {
                            IsVisible = slider.Kind is TrayDashboardSliderKind.GpuCoreClock
                                or TrayDashboardSliderKind.GpuMemoryClock,
                        })
                        .ToArray(),
                });
                dashboard.AppWindow.Resize(new Windows.Graphics.SizeInt32(430, 760));
                ShowNoninteractiveSnapshotWindow(dashboard);
                await WaitForSnapshotLayoutAsync();

                ServiceStatusSnapshot status = CreateGpuStartupStatus(delayedRanges);
                ApplyGpuStartupStatus(window.ViewModel, status);
                await WaitForSnapshotLayoutAsync();
                window.VerifyGpuStartupControls(expectEditable: !delayedRanges, recorder);
                dashboard.VerifyGpuStartupControls(expectEditable: !delayedRanges, recorder);
                if (delayedRanges)
                {
                    ApplyGpuStartupStatus(window.ViewModel, status with
                    {
                        Revision = status.Revision + 1,
                        ConfirmedState = status.ConfirmedState! with
                        {
                            GpuClockOffsets = GpuStartupRanges,
                        },
                    });
                    await WaitForSnapshotLayoutAsync();
                    window.VerifyGpuStartupControls(expectEditable: true, recorder);
                    dashboard.VerifyGpuStartupControls(expectEditable: true, recorder);
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            foreach (TrayDashboardWindow dashboard in dashboards)
            {
                try
                {
                    dashboard.Dispose();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }
            foreach (MainWindow window in windows)
            {
                try
                {
                    await window.DisposeAsync();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }
        }
        try
        {
            File.WriteAllText(reportPath, failure?.ToString()
                ?? "Known and delayed GPU ranges restore enabled 125/500 MHz sliders in main and quick controls without a toggle or service calls.");
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        // Keep the dispatcher alive until all asynchronous cleanup has completed.
        foreach (MainWindow window in windows)
        {
            try
            {
                window.Close();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }
        Environment.Exit(failure is null ? 0 : 1);
    }

    private static GpuClockOffsetCapabilities GpuStartupRanges { get; } = new(
        "NVIDIA GPU",
        new GpuClockOffsetRange(-1000, 400, 125),
        new GpuClockOffsetRange(-1000, 3000, 500));

    private static ServiceStatusSnapshot CreateGpuStartupStatus(bool delayedRanges)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            1, true, false, [], null, [], "GPU startup snapshot",
            DeviceCapabilities.SafeTargetDefaults() with
            {
                GpuOverclock = new GpuOverclockCapabilities(
                    new DeviceNumericRange(-1000, 400, null),
                    new DeviceNumericRange(-1000, 3000, null),
                    DeviceFeatureAccess.ReadWrite),
            },
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, null, null)
            {
                HyperBoostActive = false,
                GpuClockOffsets = delayedRanges ? null : GpuStartupRanges,
            },
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false))
            {
                GpuClockTuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(125, 500)),
            },
            PowerSource.PluggedIn, null, null, null, null, false);
    }

    private static void ApplyGpuStartupStatus(MainViewModel viewModel, ServiceStatusSnapshot status)
    {
        MethodInfo applyStatus = typeof(MainViewModel).GetMethod(
            "ApplyStatus", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Status presentation hook was not found.");
        applyStatus.Invoke(viewModel, [status, false]);
    }

    // Every attempted call is trapped before IPC. A presentation regression must
    // never turn this synthetic status into a request to the installed service.
    public class GpuStartupServiceClientProxy : DispatchProxy
    {
        public int Calls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IAsyncDisposable.DisposeAsync))
            {
                return ValueTask.CompletedTask;
            }
            Calls++;
            throw new InvalidOperationException($"Disconnected GPU startup attempted service call {targetMethod?.Name}.");
        }
    }
}

public sealed partial class MainWindow
{
    internal void VerifyGpuStartupControls(bool expectEditable, App.GpuStartupServiceClientProxy recorder)
    {
        if (!localizationSnapshotMode || initializationTask is not null || recorder.Calls != 0
            || gpuClockOffsetCommits.TryPeekPending(out _) || gpuClockOffsetCommitTimer.IsRunning)
        {
            throw new InvalidOperationException("GPU startup fixture must remain disconnected with no scheduled or submitted writes.");
        }
        FrameworkElement root = (FrameworkElement)Content;
        foreach ((string name, int value) in new[]
        {
            ("GpuCoreClockOffsetSlider", 125),
            ("GpuMemoryClockOffsetSlider", 500),
        })
        {
            Slider slider = FindGpuStartupControl<Slider>(root, name)
                ?? throw new InvalidOperationException($"GPU startup slider {name} was not realized.");
            if (slider.IsEnabled != expectEditable || !ViewModel.IsGpuClockTuningEnabled)
            {
                throw new InvalidOperationException($"{name}: expected editable={expectEditable}, actual={slider.IsEnabled}, tuning={ViewModel.IsGpuClockTuningEnabled}.");
            }
            if (expectEditable)
            {
                var parent = (Grid)VisualTreeHelper.GetParent(slider);
                TextBlock label = parent.Children.OfType<TextBlock>().Single();
                string displayed = label.Inlines.OfType<Run>().First().Text;
                if (slider.Value != value || displayed != value.ToString(System.Globalization.CultureInfo.CurrentCulture))
                {
                    throw new InvalidOperationException($"{name}: expected {value} MHz; actual slider={slider.Value}, displayed={displayed}.");
                }
            }
        }
    }

    private static T? FindGpuStartupControl<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && match.Name == name)
            {
                return match;
            }
            if (FindGpuStartupControl<T>(child, name) is { } descendant)
            {
                return descendant;
            }
        }
        return null;
    }
}
public sealed partial class TrayDashboardWindow
{
    internal void VerifyGpuStartupControls(bool expectEditable, App.GpuStartupServiceClientProxy recorder)
    {
        if (recorder.Calls != 0 || gpuClockOffsetCommits.TryPeekPending(out _)
            || gpuClockOffsetCommitTimer is { IsRunning: true })
        {
            throw new InvalidOperationException("GPU quick-controls fixture must have no scheduled or submitted writes.");
        }
        foreach ((Slider slider, Grid row, int value) in new[]
        {
            (GpuCoreClockSlider, GpuCoreClockSliderRow, 125),
            (GpuMemoryClockSlider, GpuMemoryClockSliderRow, 500),
        })
        {
            if (row.Visibility != Visibility.Visible || slider.ActualWidth <= 0 || slider.ActualHeight <= 0
                || slider.IsEnabled != expectEditable || !ViewModel.IsGpuClockTuningEnabled)
            {
                throw new InvalidOperationException(
                    $"{slider.Name}: expected visible editable={expectEditable}; actual row={row.Visibility}, size={slider.ActualWidth}x{slider.ActualHeight}, editable={slider.IsEnabled}, tuning={ViewModel.IsGpuClockTuningEnabled}.");
            }
            if (expectEditable && slider.Value != value)
            {
                throw new InvalidOperationException($"{slider.Name}: expected {value} MHz; actual={slider.Value}.");
            }
        }
    }
}
#endif
