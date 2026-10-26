using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using OpenBlade.Contracts;
using Button = Microsoft.UI.Xaml.Controls.Button;
using CheckBox = Microsoft.UI.Xaml.Controls.CheckBox;
using ComboBox = Microsoft.UI.Xaml.Controls.ComboBox;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using Image = Microsoft.UI.Xaml.Controls.Image;
using Orientation = Microsoft.UI.Xaml.Controls.Orientation;
using Slider = Microsoft.UI.Xaml.Controls.Slider;
using TextBox = Microsoft.UI.Xaml.Controls.TextBox;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource featureLifetime = new();
    private CancellationTokenSource? discoveryIconLifetime;
    private CancellationTokenSource? applicationDiscoveryLifetime;
    private IReadOnlyList<InstalledApplicationCandidate> discoveredApplications = [];
    private ApplicationProfileDraft? applicationProfileDraft;
    private Action? saveApplicationSourceEditor;
    private TextBox? applicationProfileNameEditor;
    private TextBox? applicationProfilePathEditor;
    private ComboBox? applicationProfileActivationEditor;
    private TextBlock? applicationProfileValidationText;
    private Border? applicationProfileValidationRow;
    private int applicationProfileEditorGeneration;
    private int? applicationProfileSaveGeneration;
    private int applicationDiscoveryGeneration;
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

    private void InitializeApplicationProfilePages()
    {
        ApplicationProfileEditorBackButton.Content = new SymbolIcon(Symbol.Back);
        ApplicationDiscoveryBackButton.Content = new SymbolIcon(Symbol.Back);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ApplicationProfileEditorBackButton,
            LocalizedResources.Get("ApplicationsUI_BackToProfiles", "Back to profiles"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ApplicationDiscoveryBackButton,
            LocalizedResources.Get("ApplicationsUI_BackToProfiles", "Back to profiles"));
        ToolTipService.SetToolTip(ApplicationProfileEditorBackButton,
            LocalizedResources.Get("ApplicationsUI_BackToProfiles", "Back to profiles"));
        ToolTipService.SetToolTip(ApplicationDiscoveryBackButton,
            LocalizedResources.Get("ApplicationsUI_BackToProfiles", "Back to profiles"));
        ApplicationProfileCancelButton.Content =
            LocalizedResources.Get("ApplicationsUI_Cancel", "Cancel");
        ApplicationDiscoveryCancelButton.Content =
            LocalizedResources.Get("ApplicationsUI_Cancel", "Cancel");
        ApplicationProfileSaveButton.Content =
            LocalizedResources.Get("ApplicationsUI_SaveProfile", "Save profile");
        ApplicationDiscoveryTitle.Text =
            LocalizedResources.Get("ApplicationsUI_DiscoveryTitle", "Discover installed games");
        ApplicationDiscoveryDescription.Text = LocalizedResources.Get(
            "ApplicationsUI_DiscoveryDescription",
            "Choose an executable from a detected game. Review the exact path before saving the profile.");
        ApplicationDiscoverySearchBox.Header =
            LocalizedResources.Get("ApplicationsUI_SearchLabel", "Search installed games");
        ApplicationDiscoverySearchBox.PlaceholderText =
            LocalizedResources.Get("ApplicationsUI_SearchPlaceholder", "Name, launcher, or executable");
    }

    private void RefreshApplicationProfiles()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        ApplicationProfileRows.Children.Clear();
        for (int profileIndex = 0; profileIndex < ViewModel.ApplicationProfiles.Count; profileIndex++)
        {
            ApplicationProfileDefinition profile = ViewModel.ApplicationProfiles[profileIndex];
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
                HorizontalAlignment = HorizontalAlignment.Right,
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
            AddAction(LocalizedResources.Get("Feature_f7e6729d30cf", "Edit"), () =>
            {
                ShowApplicationProfileEditor(profile);
                return Task.CompletedTask;
            });
            AddAction(LocalizedResources.Get("Feature_b26de372b4b0", "Delete"), async () =>
                _ = await ViewModel.RemoveApplicationProfileAsync(profile.Id, featureLifetime.Token));
            var profileText = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            profileText.Children.Add(title);
            profileText.Children.Add(path);
            var profileActions = new StackPanel
            {
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            profileActions.Children.Add(enabled);
            profileActions.Children.Add(actions);
            var body = new Grid { ColumnSpacing = 20 };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(profileActions, 1);
            body.Children.Add(profileText);
            body.Children.Add(profileActions);
            ApplicationProfileRows.Children.Add(new Border
            {
                Style = (Style)Root.Resources[profileIndex == ViewModel.ApplicationProfiles.Count - 1
                    ? "SettingsLastNeutralSubcardStyle" : "SettingsNeutralSubcardStyle"],
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
        if (ApplicationProfileEditorPage.Visibility == Visibility.Visible)
        {
            ApplicationProfileSaveButton.IsEnabled = !localizationSnapshotMode
                && ViewModel.CanControlHardware
                && applicationProfileSaveGeneration != applicationProfileEditorGeneration;
        }
    }

    private void OnAddApplicationProfile(object sender, RoutedEventArgs args) =>
        ShowApplicationProfileEditor(existing: null);

    private async void OnDiscoverApplications(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(async () =>
        {
            ShowApplicationDiscoveryPage();
            if (localizationSnapshotMode)
            {
                discoveredApplications = [];
                RenderApplicationDiscoveryResults();
                return;
            }
            ApplicationDiscoveryProgress.IsActive = true;
            ApplicationDiscoveryProgress.Visibility = Visibility.Visible;
            ApplicationDiscoveryStatus.Text = LocalizedResources.Get(
                "ApplicationsUI_DiscoveryLoading", "Looking for installed games…");
            CancellationTokenSource discoveryLifetime = applicationDiscoveryLifetime!;
            CancellationToken discoveryToken = discoveryLifetime.Token;
            int discoveryGeneration = applicationDiscoveryGeneration;
            IReadOnlyList<InstalledApplicationCandidate> results;
            try
            {
                results = await InstalledApplicationDiscovery.DiscoverAsync(discoveryToken);
                discoveryToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (discoveryToken.IsCancellationRequested)
            {
                return;
            }
            if (discoveryGeneration != applicationDiscoveryGeneration
                || !ReferenceEquals(applicationDiscoveryLifetime, discoveryLifetime)
                || ApplicationDiscoveryPage.Visibility != Visibility.Visible)
            {
                return;
            }
            discoveredApplications = results;
            RenderApplicationDiscoveryResults();
        }, nameof(OnDiscoverApplications));

    private void ShowApplicationProfileEditor(ApplicationProfileDefinition? existing,
        string? suggestedPath = null, string? suggestedName = null)
    {
        var draft = new ApplicationProfileDraft(existing);
        if (suggestedPath is not null)
        {
            draft.ExecutablePath = suggestedPath;
            draft.Name = suggestedName ?? Path.GetFileNameWithoutExtension(suggestedPath);
        }
        ShowApplicationProfileEditor(draft, isExisting: existing is not null);
    }

    private void ShowApplicationProfileEditor(ApplicationProfileDraft draft, bool isExisting)
    {
        CancelApplicationDiscovery();
        applicationProfileEditorGeneration = unchecked(applicationProfileEditorGeneration + 1);
        applicationProfileSaveGeneration = null;
        applicationProfileDraft = draft;
        ApplicationProfileEditorForm.IsEnabled = true;
        ApplicationProfileEditorTitle.Text = isExisting
            ? LocalizedResources.Get("ApplicationsUI_EditProfileTitle", "Edit application profile")
            : LocalizedResources.Get("ApplicationsUI_NewProfileTitle", "Add application profile");
        ApplicationProfileEditorDescription.Text = LocalizedResources.Get(
            "ApplicationsUI_EditorDescription",
            "Match one exact executable. Unset settings inherit the current power-source profile, and manual controls keep priority.");
        var name = new TextBox { Header = LocalizedResources.Get("Feature_6cc5df35f033", "Name"), Text = draft.Name, MaxLength = 80 };
        var path = new TextBox { Header = LocalizedResources.Get("Feature_af4123249281", "Executable path"), Text = draft.ExecutablePath };
        applicationProfileNameEditor = name;
        applicationProfilePathEditor = path;
        var browse = new Button
        {
            Content = LocalizedResources.Get("Feature_ce6bbac0215e", "Browse for executable"),
            IsEnabled = !localizationSnapshotMode,
        };
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
        }, nameof(ShowApplicationProfileEditor));
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
        applicationProfileActivationEditor = activation;
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
        saveApplicationSourceEditor = saveSource;
        source.SelectionChanged += (_, _) =>
        {
            saveSource();
            editingSource = ((ApplicationChoice<PowerSource>)source.SelectedItem).Value!.Value;
            saveSource = BuildApplicationSourceEditor(editor, draft, editingSource);
            saveApplicationSourceEditor = saveSource;
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        applicationProfileValidationText = error;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(error,
            Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        ApplicationProfileEditorFields.Children.Clear();
        AddEditorRow(name);
        AddEditorRow(new StackPanel { Spacing = 8, Children = { path, browse } });
        AddEditorRow(activation);
        AddEditorRow(source);
        ApplicationProfileEditorFields.Children.Add(editor);
        applicationProfileValidationRow = AddEditorRow(error);
        applicationProfileValidationRow.Visibility = Visibility.Collapsed;
        ApplicationProfileSaveButton.IsEnabled = !localizationSnapshotMode
            && ViewModel.CanControlHardware;
        ProfilesPage.Visibility = Visibility.Collapsed;
        ApplicationDiscoveryPage.Visibility = Visibility.Collapsed;
        ApplicationProfileEditorPage.Visibility = Visibility.Visible;
        ApplicationProfileEditorPage.ChangeView(null, 0, null, disableAnimation: true);
        name.Focus(FocusState.Programmatic);

        Border AddEditorRow(UIElement child)
        {
            var row = new Border
            {
                Style = (Style)Root.Resources["SettingsEditorSubcardStyle"],
                Child = child,
            };
            ApplicationProfileEditorFields.Children.Add(row);
            return row;
        }
    }

    private async void OnSaveApplicationProfile(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(SaveApplicationProfileEditorAsync, nameof(OnSaveApplicationProfile));

    private async Task SaveApplicationProfileEditorAsync()
    {
        if (applicationProfileDraft is null || applicationProfileNameEditor is null
            || applicationProfilePathEditor is null || applicationProfileActivationEditor is null
            || applicationProfileValidationText is null)
        {
            return;
        }

        int editorGeneration = applicationProfileEditorGeneration;
        ApplicationProfileDraft submittedDraft = applicationProfileDraft;
        if (applicationProfileSaveGeneration == editorGeneration)
        {
            return;
        }
        applicationProfileSaveGeneration = editorGeneration;
        ApplicationProfileSaveButton.IsEnabled = false;
        ApplicationProfileEditorForm.IsEnabled = false;
        SetApplicationProfileValidation(string.Empty);
        try
        {
            saveApplicationSourceEditor?.Invoke();
            submittedDraft.Name = applicationProfileNameEditor.Text;
            submittedDraft.ExecutablePath = applicationProfilePathEditor.Text;
            submittedDraft.ActivationMode =
                ((ApplicationChoice<ApplicationActivationMode>)applicationProfileActivationEditor.SelectedItem).Value!.Value;
            if (!await ViewModel.SaveApplicationProfileAsync(
                    submittedDraft.Build(), featureLifetime.Token))
            {
                if (IsCurrentApplicationProfileEditor(submittedDraft, editorGeneration))
                {
                    SetApplicationProfileValidation(LocalizedResources.Get(
                        "Feature_a66522394a37",
                        "The profile could not be saved. Review the service status and try again."));
                }
                return;
            }

            if (IsCurrentApplicationProfileEditor(submittedDraft, editorGeneration))
            {
                ShowApplicationProfilesOverview();
            }
        }
        catch (ArgumentException exception)
        {
            if (IsCurrentApplicationProfileEditor(submittedDraft, editorGeneration))
            {
                SetApplicationProfileValidation(exception.Message);
            }
        }
        finally
        {
            if (IsCurrentApplicationProfileEditor(submittedDraft, editorGeneration))
            {
                applicationProfileSaveGeneration = null;
                ApplicationProfileEditorForm.IsEnabled = true;
                ApplicationProfileSaveButton.IsEnabled = !localizationSnapshotMode
                    && ViewModel.CanControlHardware;
            }
        }
    }

    private bool IsCurrentApplicationProfileEditor(
        ApplicationProfileDraft draft,
        int generation) =>
        generation == applicationProfileEditorGeneration
        && ReferenceEquals(draft, applicationProfileDraft)
        && ApplicationProfileEditorPage.Visibility == Visibility.Visible;

    private void OnExitApplicationSubpage(object sender, RoutedEventArgs args) =>
        ShowApplicationProfilesOverview();

    private void SetApplicationProfileValidation(string message)
    {
        if (applicationProfileValidationText is null || applicationProfileValidationRow is null)
        {
            return;
        }
        applicationProfileValidationText.Text = message;
        applicationProfileValidationRow.Visibility = string.IsNullOrEmpty(message)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnApplicationDiscoverySearchChanged(object sender, TextChangedEventArgs args) =>
        RenderApplicationDiscoveryResults();

    private void ShowApplicationDiscoveryPage()
    {
        CancelApplicationDiscovery();
        applicationDiscoveryLifetime = CancellationTokenSource.CreateLinkedTokenSource(featureLifetime.Token);
        discoveredApplications = [];
        ApplicationDiscoverySearchBox.Text = string.Empty;
        ApplicationDiscoveryResults.Children.Clear();
        ApplicationDiscoveryProgress.IsActive = false;
        ApplicationDiscoveryProgress.Visibility = Visibility.Collapsed;
        ProfilesPage.Visibility = Visibility.Collapsed;
        ApplicationProfileEditorPage.Visibility = Visibility.Collapsed;
        ApplicationDiscoveryPage.Visibility = Visibility.Visible;
        ApplicationDiscoveryPage.ChangeView(null, 0, null, disableAnimation: true);
        ApplicationDiscoverySearchBox.Focus(FocusState.Programmatic);
    }

    private void RenderApplicationDiscoveryResults()
    {
        if (ApplicationDiscoveryPage.Visibility != Visibility.Visible)
        {
            return;
        }

        CancelDiscoveryIconLoading();
        discoveryIconLifetime = CancellationTokenSource.CreateLinkedTokenSource(featureLifetime.Token);
        CancellationToken iconToken = discoveryIconLifetime.Token;
        ApplicationDiscoveryProgress.IsActive = false;
        ApplicationDiscoveryProgress.Visibility = Visibility.Collapsed;
        ApplicationDiscoveryResults.Children.Clear();
        IReadOnlyList<InstalledApplicationGroup> groups = ApplicationDiscoveryCatalog.Group(
            discoveredApplications, ApplicationDiscoverySearchBox.Text);
        int totalExecutables = groups.Sum(group => group.Executables.Count);
        if (totalExecutables == 0)
        {
            ApplicationDiscoveryStatus.Text = discoveredApplications.Count == 0
                ? LocalizedResources.Get("ApplicationsUI_DiscoveryEmpty",
                    "No installed games were found. Use Add application to choose an executable manually.")
                : LocalizedResources.Get("ApplicationsUI_DiscoveryNoMatches",
                    "No installed games match this search.");
            return;
        }

        const int maximumRenderedExecutables = 80;
        int rendered = 0;
        foreach (InstalledApplicationGroup group in groups)
        {
            InstalledApplicationCandidate[] visible = group.Executables
                .Take(maximumRenderedExecutables - rendered)
                .ToArray();
            if (visible.Length == 0)
            {
                break;
            }
            rendered += visible.Length;
            ApplicationDiscoveryResults.Children.Add(BuildApplicationDiscoveryGroup(group, visible, iconToken));
        }
        ApplicationDiscoveryStatus.Text = totalExecutables > maximumRenderedExecutables
            ? LocalizedResources.Format("ApplicationsUI_DiscoveryLimitedCount",
                "Showing the first {0} executable matches. Narrow the search to see more.", maximumRenderedExecutables)
            : LocalizedResources.Format("ApplicationsUI_DiscoveryCount",
                "Matching executables: {0} · Installed games: {1}", totalExecutables, groups.Count);
    }

    private Border BuildApplicationDiscoveryGroup(
        InstalledApplicationGroup group,
        IReadOnlyList<InstalledApplicationCandidate> candidates,
        CancellationToken iconToken)
    {
        var stack = new StackPanel();
        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(new TextBlock
        {
            Text = group.Name,
            Style = (Style)Root.Resources["CardTitleStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        heading.Children.Add(new TextBlock
        {
            Text = $"{group.Launcher} · {group.InstallRoot}",
            Style = (Style)Root.Resources["SecondaryTextStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new Border
        {
            Style = (Style)Root.Resources["SettingsEditorSubcardStyle"],
            Child = heading,
        });
        for (int index = 0; index < candidates.Count; index++)
        {
            InstalledApplicationCandidate candidate = candidates[index];
            var image = new Image
            {
                Width = 40,
                Height = 40,
                Stretch = Stretch.Uniform,
                Visibility = Visibility.Collapsed,
            };
            var fallback = new FontIcon { Glyph = "\uE7C3", FontSize = 24 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                image, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                fallback, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            var iconHost = new Grid { Width = 40, Height = 40 };
            iconHost.Children.Add(fallback);
            iconHost.Children.Add(image);
            var label = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock
            {
                Text = candidate.ExecutableName,
                Style = (Style)Root.Resources["CardTitleStyle"],
                TextWrapping = TextWrapping.Wrap,
            });
            label.Children.Add(new TextBlock
            {
                Text = candidate.ExecutablePath,
                Style = (Style)Root.Resources["SecondaryTextStyle"],
                TextWrapping = TextWrapping.Wrap,
            });
            var choose = new Button
            {
                Content = LocalizedResources.Get("ApplicationsUI_UseExecutable", "Use"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(choose,
                LocalizedResources.Format("ApplicationsUI_UseExecutableNamed", "Use {0}", candidate.ExecutableName));
            choose.Click += (_, _) => ShowApplicationProfileEditor(
                existing: null, candidate.ExecutablePath, candidate.Name);
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 1);
            Grid.SetColumn(choose, 2);
            row.Children.Add(iconHost);
            row.Children.Add(label);
            row.Children.Add(choose);
            stack.Children.Add(new Border
            {
                Style = (Style)Root.Resources[index == candidates.Count - 1
                    ? "SettingsLastNeutralSubcardStyle" : "SettingsNeutralSubcardStyle"],
                Child = row,
            });
            if (!localizationSnapshotMode)
            {
                _ = LoadApplicationDiscoveryIconAsync(image, fallback, candidate.ExecutablePath, iconToken);
            }
        }

        return new Border
        {
            Style = (Style)Root.Resources["SettingsGroupStyle"],
            Child = stack,
        };
    }

    private static async Task LoadApplicationDiscoveryIconAsync(
        Image image,
        FontIcon fallback,
        string executablePath,
        CancellationToken token)
    {
        try
        {
            if (await ApplicationExecutableIconLoader.LoadAsync(executablePath, token) is { } source)
            {
                image.Source = source;
                image.Visibility = Visibility.Visible;
                fallback.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void ShowApplicationProfilesOverview()
    {
        ResetApplicationSubpageNavigation();
        ProfilesPage.Visibility = Visibility.Visible;
        RefreshApplicationProfiles();
    }

    private void ResetApplicationSubpageNavigation()
    {
        CancelApplicationDiscovery();
        applicationProfileEditorGeneration = unchecked(applicationProfileEditorGeneration + 1);
        applicationProfileSaveGeneration = null;
        ApplicationProfileEditorPage.Visibility = Visibility.Collapsed;
        ApplicationDiscoveryPage.Visibility = Visibility.Collapsed;
        applicationProfileDraft = null;
        saveApplicationSourceEditor = null;
        applicationProfileNameEditor = null;
        applicationProfilePathEditor = null;
        applicationProfileActivationEditor = null;
        applicationProfileValidationText = null;
        applicationProfileValidationRow = null;
    }

    private void CancelDiscoveryIconLoading()
    {
        discoveryIconLifetime?.Cancel();
        discoveryIconLifetime?.Dispose();
        discoveryIconLifetime = null;
    }

    private void CancelApplicationDiscovery()
    {
        applicationDiscoveryGeneration = unchecked(applicationDiscoveryGeneration + 1);
        CancelDiscoveryIconLoading();
        applicationDiscoveryLifetime?.Cancel();
        applicationDiscoveryLifetime?.Dispose();
        applicationDiscoveryLifetime = null;
    }

    internal void ShowLocalizationSnapshotApplicationEditor(ApplicationProfileDraft draft) =>
        ShowApplicationProfileEditor(draft, isExisting: false);

    internal void ShowLocalizationSnapshotApplicationDiscovery(
        IReadOnlyList<InstalledApplicationCandidate> candidates)
    {
        ShowApplicationDiscoveryPage();
        discoveredApplications = candidates;
        RenderApplicationDiscoveryResults();
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
        var brightness = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            StepFrequency = 5,
            Value = current.Display?.BrightnessPercent ?? DisplayViewModel.BrightnessPercent ?? 50,
            IsEnabled = brightnessEnabled.IsChecked == true && DisplayViewModel.BrightnessPercent.HasValue,
            Style = (Style)Root.Resources["InlineCardSliderStyle"],
        };
        var brightnessValue = new TextBlock
        {
            MinWidth = 44,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(brightness,
            LocalizedResources.Get("Feature_9cb7099bc130", "Display brightness (%)"));
        void UpdateBrightnessValue() => brightnessValue.Text =
            LocalizedResources.Format("ApplicationsUI_BrightnessPercent", "{0}%", Math.Round(brightness.Value));
        brightness.ValueChanged += (_, _) => UpdateBrightnessValue();
        brightnessEnabled.Checked += (_, _) => brightness.IsEnabled = DisplayViewModel.BrightnessPercent.HasValue;
        brightnessEnabled.Unchecked += (_, _) => brightness.IsEnabled = false;
        UpdateBrightnessValue();
        var brightnessEditor = new StackPanel { Spacing = 8 };
        brightnessEditor.Children.Add(brightnessEnabled);
        var brightnessRow = new Grid { ColumnSpacing = 12 };
        brightnessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        brightnessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(brightnessValue, 1);
        brightnessRow.Children.Add(brightness);
        brightnessRow.Children.Add(brightnessValue);
        brightnessEditor.Children.Add(brightnessRow);
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
        Add(brightnessEditor); Add(lighting);
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
