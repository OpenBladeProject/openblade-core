using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using OpenBlade.Contracts;
using ComboBox = Microsoft.UI.Xaml.Controls.ComboBox;
using Button = Microsoft.UI.Xaml.Controls.Button;
using CheckBox = Microsoft.UI.Xaml.Controls.CheckBox;
using ListView = Microsoft.UI.Xaml.Controls.ListView;
using TextBox = Microsoft.UI.Xaml.Controls.TextBox;
using Orientation = Microsoft.UI.Xaml.Controls.Orientation;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource featureLifetime = new();
    private sealed record ApplicationChoice<T>(T? Value, string Label) where T : struct;
    private sealed record ApplicationTextChoice(string? Value, string Label);
    private sealed class ApplicationSourceEditorState
    {
        internal Action? Save { get; set; }
    }

    private static string ApplicationFanModeLabel(FanMode mode) => mode switch
    {
        FanMode.Automatic => LocalizedResources.Get("Applications_FanAutomatic", "Automatic"),
        FanMode.Fixed => LocalizedResources.Get("Applications_FanFixed", "Fixed"),
        FanMode.ManualCurve => LocalizedResources.Get("Applications_FanCurve", "Manual curve (saved points)"),
        _ => LocalizedResources.Get("Applications_FanMaximum", "Maximum"),
    };

    private void RefreshApplicationProfiles()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        ApplicationProfileRows.Children.Clear();
        foreach (ApplicationProfileDefinition profile in ViewModel.ApplicationProfiles)
        {
            var title = new TextBlock { Text = profile.Name, TextWrapping = TextWrapping.Wrap, Style = (Style)Root.Resources["CardTitleStyle"] };
            var path = new TextBlock
            {
                Text = profile.ExecutablePath,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Root.Resources["SecondaryTextStyle"],
            };
            var enabled = new ToggleSwitch
            {
                Header = LocalizedResources.Get("Feature_847ced6ca02b", "Enabled"),
                IsOn = profile.Enabled,
                IsEnabled = ViewModel.CanControlHardware,
            };
            enabled.Toggled += async (_, _) => await RunHandlerAsync(async () =>
            {
                if (localizationSnapshotMode)
                {
                    return;
                }
                _ = await ViewModel.SaveApplicationProfileAsync(profile with { Enabled = enabled.IsOn },
                    featureLifetime.Token);
                RefreshApplicationProfiles();
            }, nameof(RefreshApplicationProfiles));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            AddAction(LocalizedResources.Get("Feature_f7e6729d30cf", "Edit"), async () => await EditApplicationProfileAsync(profile));
            AddAction(LocalizedResources.Get("Feature_b26de372b4b0", "Delete"), async () =>
                _ = await ViewModel.RemoveApplicationProfileAsync(profile.Id, featureLifetime.Token));
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(title);
            body.Children.Add(path);
            body.Children.Add(enabled);
            body.Children.Add(actions);
            ApplicationProfileRows.Children.Add(new Border
            {
                Style = (Style)Root.Resources["SettingsEditorSubcardStyle"],
                Child = body,
            });

            void AddAction(string label, Func<Task> action)
            {
                var button = new Button { Content = label, IsEnabled = ViewModel.CanControlHardware };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{label}: {profile.Name}");
                button.Click += async (_, _) => await RunHandlerAsync(async () =>
                {
                    await action();
                    RefreshApplicationProfiles();
                }, nameof(RefreshApplicationProfiles));
                actions.Children.Add(button);
            }
        }
        ApplicationProfilesEmpty.Visibility = ViewModel.ApplicationProfiles.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        ActiveApplicationProfileText.Text = LocalizedResources.Format("Feature_c83928a24959", "Active profile: {0}",
            ViewModel.ActiveApplicationProfileName);
    }

    private async void OnAddApplicationProfile(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(() => EditApplicationProfileAsync(null), nameof(OnAddApplicationProfile));

    private async void OnDiscoverApplications(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(async () =>
        {
            if (localizationSnapshotMode)
            {
                return;
            }
            IReadOnlyList<InstalledApplicationCandidate> candidates =
                await InstalledApplicationDiscovery.DiscoverAsync(featureLifetime.Token);
            var list = new ListView
            {
                ItemsSource = candidates,
                DisplayMemberPath = "Label",
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = Math.Max(240, Root.ActualHeight - 260),
                MinWidth = 320,
            };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock
            {
                Text = candidates.Count == 0
                    ? LocalizedResources.Get("Feature_c43fb5f4da35", "No installed game executables were found. Use Add application to browse manually.")
                    : LocalizedResources.Get("Feature_5f81f632042a", "Suggestions from Steam, Epic Games, Xbox and Battle.net. Select the game's executable and review its path."),
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(list);
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = LocalizedResources.Get("Feature_a4873a62f9e5", "Discover installed games"),
                Content = panel,
                PrimaryButtonText = LocalizedResources.Get("Feature_51e2604e2aaf", "Choose"),
                CloseButtonText = LocalizedResources.Get("Feature_c0774c185060", "Cancel"),
                IsPrimaryButtonEnabled = false,
            };
            list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
            if (await ShowContentDialogAsync(dialog) == ContentDialogResult.Primary
                && list.SelectedItem is InstalledApplicationCandidate selected)
            {
                await EditApplicationProfileAsync(null, selected.ExecutablePath, selected.Name);
            }
        }, nameof(OnDiscoverApplications));

    private async Task EditApplicationProfileAsync(ApplicationProfileDefinition? existing,
        string? suggestedPath = null, string? suggestedName = null)
    {
        var draft = new ApplicationProfileDraft(existing);
        if (suggestedPath is not null)
        {
            draft.ExecutablePath = suggestedPath;
            draft.Name = suggestedName ?? Path.GetFileNameWithoutExtension(suggestedPath);
        }
        var name = new TextBox { Header = LocalizedResources.Get("Feature_6cc5df35f033", "Name"), Text = draft.Name, MaxLength = 80 };
        var path = new TextBox { Header = LocalizedResources.Get("Feature_af4123249281", "Executable path"), Text = draft.ExecutablePath };
        var browse = new Button { Content = LocalizedResources.Get("Feature_ce6bbac0215e", "Browse for executable") };
        browse.Click += async (_, _) => await RunHandlerAsync(async () =>
        {
            var picker = new FileOpenPicker(AppWindow.Id) { Title = LocalizedResources.Get("Feature_6fce74ac10dd", "Choose an executable") };
            picker.FileTypeFilter.Add(".exe");
            PickFileResult? selected = await picker.PickSingleFileAsync();
            if (selected is not null)
            {
                path.Text = selected.Path;
                if (string.IsNullOrWhiteSpace(name.Text))
                {
                    name.Text = Path.GetFileNameWithoutExtension(selected.Path);
                }
            }
        }, nameof(EditApplicationProfileAsync));
        var activation = new ComboBox
        {
            Header = LocalizedResources.Get("Feature_f8ed8576f5a9", "Activation"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = "Label",
            ItemsSource = new ApplicationChoice<ApplicationActivationMode>[]
            {
                new(ApplicationActivationMode.Foreground, LocalizedResources.Get("Feature_3d621a53b31e", "While in foreground")),
                new(ApplicationActivationMode.WhileRunningAfterActivation,
                    LocalizedResources.Get("Feature_0d94e35e8475", "Until the activated process exits")),
            },
            SelectedIndex = draft.ActivationMode == ApplicationActivationMode.Foreground ? 0 : 1,
        };
        var source = new ComboBox
        {
            Header = LocalizedResources.Get("Feature_061b9b48e177", "Power source"),
            DisplayMemberPath = "Label",
            ItemsSource = new ApplicationChoice<PowerSource>[]
            {
                new(PowerSource.PluggedIn, LocalizedResources.Get("Feature_877cb5a94232", "Plugged in")),
                new(PowerSource.Battery, LocalizedResources.Get("Feature_496960c3c1f5", "Battery")),
                new(PowerSource.UsbC, LocalizedResources.Get("Feature_ea008cd08808", "USB-C")),
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var editor = new StackPanel();
        PowerSource editingSource = PowerSource.PluggedIn;
        Action saveSource = BuildApplicationSourceEditor(editor, draft, editingSource);
        source.SelectionChanged += (_, _) =>
        {
            saveSource();
            editingSource = ((ApplicationChoice<PowerSource>)source.SelectedItem).Value!.Value;
            saveSource = BuildApplicationSourceEditor(editor, draft, editingSource);
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(error,
            Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = LocalizedResources.Get("Feature_7837e2a6dec5", "Match one exact executable. Unset values inherit the current power-source profile. Manual controls keep priority. The most recently activated running application stays active after you switch away."),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Root.Resources["SecondaryTextStyle"],
        });
        panel.Children.Add(name);
        panel.Children.Add(path);
        panel.Children.Add(browse);
        panel.Children.Add(activation);
        panel.Children.Add(source);
        panel.Children.Add(editor);
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = LocalizedResources.Get("Feature_ac3492bfbd41", "Application profile"),
            PrimaryButtonText = LocalizedResources.Get("Feature_c1db83d165ed", "Save"),
            CloseButtonText = LocalizedResources.Get("Feature_c0774c185060", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !localizationSnapshotMode,
            Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(240, Root.ActualHeight - 220) },
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            try
            {
                saveSource();
                draft.Name = name.Text;
                draft.ExecutablePath = path.Text;
                draft.ActivationMode = ((ApplicationChoice<ApplicationActivationMode>)activation.SelectedItem).Value!.Value;
                if (!await ViewModel.SaveApplicationProfileAsync(draft.Build(), featureLifetime.Token))
                {
                    error.Text = LocalizedResources.Get("Feature_a66522394a37", "The profile could not be saved. Review the service status and try again.");
                    args.Cancel = true;
                }
            }
            catch (ArgumentException exception)
            {
                error.Text = exception.Message;
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };
        _ = await ShowContentDialogAsync(dialog);
        RefreshApplicationProfiles();
    }

    private Action BuildApplicationSourceEditor(StackPanel host, ApplicationProfileDraft draft, PowerSource source,
        ApplicationSourceEditorState? editorState = null)
    {
        editorState ??= new();
        host.Children.Clear();
        ApplicationPowerProfileOverride current = draft.ForSource(source);
        PowerProfile basis = source switch
        {
            PowerSource.PluggedIn => ViewModel.Profiles.PluggedIn,
            PowerSource.Battery => ViewModel.Profiles.Battery,
            _ => ViewModel.Profiles.UsbC,
        };
        var copy = new Button
        {
            Content = LocalizedResources.Get("Applications_CopyPowerProfile", "Copy saved controls from this power profile"),
        };
        copy.Click += (_, _) =>
        {
            draft.SetSource(source, new()
            {
                Performance = basis.Performance,
                CustomPerformance = basis.CustomPerformance,
                GpuClockTuning = basis.GpuClockTuning,
                Fans = basis.Fans,
                FanControl = basis.FanControl,
                Lighting = basis.Lighting,
                Display = basis.Display,
                GamingModeEnabled = basis.EffectiveLaptopBehavior.GamingModeEnabled,
                GamingModeKeyPolicy = basis.EffectiveLaptopBehavior.GamingModeKeyPolicy,
            });
            _ = BuildApplicationSourceEditor(host, draft, source, editorState);
        };
        host.Children.Add(copy);
        IReadOnlyList<PerformancePresetOption> performanceOptions = source switch
        {
            PowerSource.PluggedIn => ViewModel.AvailablePluggedInPerformanceOptions,
            PowerSource.Battery => ViewModel.AvailableBatteryPerformanceOptions,
            _ => ViewModel.AvailableUsbCPerformanceOptions,
        };
        ComboBox performance = EnumEditor(LocalizedResources.Get("Feature_fb8bc434078d", "Performance"), current.Performance,
            performanceOptions.Select(option => new ApplicationChoice<PerformancePreset>(option.Value, option.Label)));
        ComboBox fans = EnumEditor(LocalizedResources.Get("Feature_155db46f9362", "Fans"), current.FanControl?.Mode ?? current.Fans,
            (source == PowerSource.UsbC ? ViewModel.AvailableUsbCFanModes : ViewModel.AvailableFanModes
                .Where(mode => FanControlPowerPolicy.IsAvailable(source, mode)))
                .Select(mode => new ApplicationChoice<FanMode>(mode, ApplicationFanModeLabel(mode))));
        var rpm = new NumberBox
        {
            Header = LocalizedResources.Get("Feature_19ad9b4c6b5c", "Fixed fan speed (RPM)"),
            Minimum = FanControlSettings.MinimumSpeedRpm,
            Maximum = FanControlSettings.MaximumSpeedRpm,
            SmallChange = 100,
            LargeChange = 500,
            Value = current.FanControl?.FixedSpeedRpm ?? basis.FanControl?.FixedSpeedRpm ?? FanControlSettings.Default.FixedSpeedRpm,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        ComboBox gaming = EnumEditor(LocalizedResources.Get("Feature_9c7c8919be0e", "Gaming mode"), current.GamingModeEnabled,
            new ApplicationChoice<bool>[] { new(true, LocalizedResources.Get("Feature_9cc01faad79c", "On")), new(false, LocalizedResources.Get("Feature_50accd740fa2", "Off")) });
        ComboBox hdr = EnumEditor(LocalizedResources.Get("Feature_d7c76cf63323", "HDR"), current.Display?.HdrEnabled,
            new ApplicationChoice<bool>[] { new(true, LocalizedResources.Get("Feature_9cc01faad79c", "On")), new(false, LocalizedResources.Get("Feature_50accd740fa2", "Off")) });
        hdr.IsEnabled = DisplayViewModel.IsHdrSupported;
        ComboBox refresh = TextEditor(LocalizedResources.Get("Feature_fb51849e03f2", "Refresh rate"), current.Display?.RefreshRateKey,
            DisplayViewModel.RefreshRateOptions.Select(option => new ApplicationTextChoice(option.Key, option.Label)));
        ComboBox color = TextEditor(LocalizedResources.Get("Feature_3a29f5ccc091", "Color profile"), current.Display?.ColorProfileKey,
            DisplayViewModel.ColorProfileOptions.Select(option => new ApplicationTextChoice(option.Key, option.Label)));
        var brightnessEnabled = new CheckBox
        {
            Content = LocalizedResources.Get("Feature_c88a6043293f", "Override display brightness"),
            IsChecked = current.Display?.BrightnessPercent.HasValue == true,
            IsEnabled = DisplayViewModel.BrightnessPercent.HasValue,
        };
        var brightness = new NumberBox
        {
            Header = LocalizedResources.Get("Feature_9cb7099bc130", "Display brightness (%)"),
            Minimum = 0,
            Maximum = 100,
            SmallChange = 5,
            Value = current.Display?.BrightnessPercent ?? DisplayViewModel.BrightnessPercent ?? 50,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var lighting = new ComboBox
        {
            Header = LocalizedResources.Get("Feature_9d2725011acd", "Keyboard lighting"),
            DisplayMemberPath = "Label",
            ItemsSource = new ApplicationTextChoice[]
            {
                new(null, LocalizedResources.Get("Feature_7a0afacfb221", "Inherit power profile")),
                new("saved", LocalizedResources.Get("Feature_207f5db133a0", "Copy this power profile's lighting")),
                new("off", LocalizedResources.Get("Feature_50accd740fa2", "Off")),
                new("existing", LocalizedResources.Get("Feature_612a36db4fc9", "Keep this application's saved lighting")),
            },
            SelectedIndex = current.Lighting is null ? 0 : 3,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Add(performance); Add(fans); Add(rpm); Add(gaming); Add(hdr); Add(refresh); Add(color);
        Add(brightnessEnabled); Add(brightness); Add(lighting);
        editorState.Save = () =>
        {
            PerformancePreset? selectedPerformance = ((ApplicationChoice<PerformancePreset>)performance.SelectedItem).Value;
            FanMode? selectedFans = ((ApplicationChoice<FanMode>)fans.SelectedItem).Value;
            string? lightingChoice = ((ApplicationTextChoice)lighting.SelectedItem).Value;
            draft.SetSource(source, current with
            {
                Performance = selectedPerformance,
                CustomPerformance = selectedPerformance == PerformancePreset.Custom
                    ? current.CustomPerformance ?? basis.CustomPerformance : null,
                GpuClockTuning = current.GpuClockTuning,
                Fans = selectedFans,
                FanControl = selectedFans is { } mode ? new(mode,
                    FanControlSettings.QuantizeSpeed(double.IsFinite(rpm.Value) ? (int)rpm.Value : 3700),
                    current.FanControl?.ManualCurve ?? basis.FanControl?.ManualCurve ?? ManualFanCurve.Default) : null,
                GamingModeEnabled = ((ApplicationChoice<bool>)gaming.SelectedItem).Value,
                Lighting = lightingChoice switch
                {
                    "saved" => basis.Lighting,
                    "off" => LightingSettingsFactory.Off() with { LogoMode = basis.Lighting.LogoMode },
                    "existing" => current.Lighting,
                    _ => null,
                },
                Display = new DisplayProfileSettings(((ApplicationTextChoice)refresh.SelectedItem).Value,
                    ((ApplicationChoice<bool>)hdr.SelectedItem).Value, ((ApplicationTextChoice)color.SelectedItem).Value)
                {
                    BrightnessPercent = brightnessEnabled.IsChecked == true && double.IsFinite(brightness.Value)
                        ? checked((byte)Math.Clamp(Math.Round(brightness.Value), 0, 100)) : null,
                },
            });
        };
        return () => editorState.Save();

        void Add(UIElement element) => host.Children.Add(new Border
        {
            Style = (Style)Root.Resources["SettingsEditorSubcardStyle"],
            Child = element,
        });
    }

    private static ComboBox EnumEditor<T>(string label, T? selected,
        IEnumerable<ApplicationChoice<T>> values) where T : struct
    {
        List<ApplicationChoice<T>> choices = [new(null, LocalizedResources.Get("Feature_7a0afacfb221", "Inherit power profile")), .. values];
        if (selected.HasValue && choices.All(choice => !EqualityComparer<T?>.Default.Equals(choice.Value, selected)))
        {
            choices.Add(new(selected, LocalizedResources.Format("Applications_UnavailableSetting", "{0} (currently unavailable)", selected)));
        }
        return new ComboBox
        {
            Header = label,
            ItemsSource = choices,
            DisplayMemberPath = "Label",
            SelectedItem = choices.FirstOrDefault(choice => EqualityComparer<T?>.Default.Equals(choice.Value, selected)) ?? choices[0],
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    private static ComboBox TextEditor(string label, string? selected, IEnumerable<ApplicationTextChoice> values)
    {
        List<ApplicationTextChoice> choices = [new(null, LocalizedResources.Get("Feature_7a0afacfb221", "Inherit power profile")), .. values];
        if (selected is not null && choices.All(choice => choice.Value != selected))
        {
            choices.Add(new(selected, selected));
        }
        return new ComboBox
        {
            Header = label,
            ItemsSource = choices,
            DisplayMemberPath = "Label",
            SelectedItem = choices.First(choice => choice.Value == selected),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }
}
