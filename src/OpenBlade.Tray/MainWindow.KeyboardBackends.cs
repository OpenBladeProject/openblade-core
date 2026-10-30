using Microsoft.UI.Xaml;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private KeyboardInputBackend snapTapBackend = KeyboardInputBackend.RazerDriver;
    private KeyboardInputBackend functionRowBackend = KeyboardInputBackend.WindowsHook;
    private bool initializingKeyboardBackends;
    private sealed record KeyboardBackendOption(KeyboardInputBackend Value, string Label);

    private void InitializeKeyboardBackendSelectors()
    {
        initializingKeyboardBackends = true;
        try
        {
            KeyboardBackendOption[] options =
            [
                new(KeyboardInputBackend.WindowsHook, LocalizedResources.Get("KeyboardBackend_WindowsHook", "Windows hook")),
                new(KeyboardInputBackend.RazerDriver, LocalizedResources.Get("KeyboardBackend_RazerDriver", "Razer driver")),
            ];
            SnapTapBackendSelector.ItemsSource = options;
            FunctionRowBackendSelector.ItemsSource = options;
            SnapTapBackendSelector.SelectedItem = options.FirstOrDefault(option => option.Value == snapTapBackend);
            FunctionRowBackendSelector.SelectedItem = options.FirstOrDefault(option => option.Value == functionRowBackend);
        }
        finally
        {
            initializingKeyboardBackends = false;
        }
    }

    private void OnSnapTapBackendChanged(object? sender, ResponsiveChoiceSelectionChangedEventArgs args)
    {
        if (initializingKeyboardBackends || localizationSnapshotMode || Volatile.Read(ref disposed) != 0
            || args.SelectedItem is not KeyboardBackendOption option || option.Value == snapTapBackend)
        {
            return;
        }
        snapTapBackend = option.Value;
        snapTapDriverInstallOutcome = null;
        QueuePreferenceSave();
        RefreshSnapTapStatus();
    }

    private void OnFunctionRowBackendChanged(object? sender, ResponsiveChoiceSelectionChangedEventArgs args)
    {
        if (initializingKeyboardBackends || localizationSnapshotMode || Volatile.Read(ref disposed) != 0
            || args.SelectedItem is not KeyboardBackendOption option || option.Value == functionRowBackend)
        {
            return;
        }
        functionRowBackend = option.Value;
        snapTapDriverInstallOutcome = null;
        QueuePreferenceSave();
        RefreshSnapTapStatus();
    }

#if OPENBLADE_DEVELOPER_HARNESS
    private void SetLocalizationSnapshotKeyboardBackends()
    {
        string? state = Environment.GetEnvironmentVariable("OPENBLADE_LOCALIZATION_SNAPSHOT_INPUT_BACKENDS");
        if (state == "alternate")
        {
            snapTapBackend = KeyboardInputBackend.WindowsHook;
            functionRowBackend = KeyboardInputBackend.RazerDriver;
        }
        else if (state == "unknown")
        {
            snapTapBackend = KeyboardInputBackend.Unknown;
            functionRowBackend = KeyboardInputBackend.Unknown;
        }
        InitializeKeyboardBackendSelectors();
    }
#endif

    private static SnapTapRuntimeStatus? SelectedBackendStatus(SnapTapRuntimeStatus? status, KeyboardInputBackend selected) =>
        selected != KeyboardInputBackend.Unknown && status?.Backend == selected ? status : null;

    private bool CanInstallSelectedKeyboardDriver() =>
        (snapTapEnabled && snapTapBackend == KeyboardInputBackend.RazerDriver
            && CanOfferSnapTapDriverSetup(SelectedBackendStatus(SnapTapStatusStore.Read(), snapTapBackend)?.State))
        || (ViewModel.SupportsFunctionKeyPrimary && functionRowBackend == KeyboardInputBackend.RazerDriver
            && CanOfferSnapTapDriverSetup(SelectedBackendStatus(FunctionRowBackendStatusStore.Read(), functionRowBackend)?.State));

    private async void OnInstallFunctionRowDriver(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(InstallSnapTapDriverAsync, nameof(OnInstallFunctionRowDriver));

    private void RefreshKeyboardBackendStatus()
    {
        SnapTapWindowsHookHelp.Visibility = snapTapBackend == KeyboardInputBackend.WindowsHook
            ? Visibility.Visible : Visibility.Collapsed;
        FunctionRowWindowsHookHelp.Visibility = functionRowBackend == KeyboardInputBackend.WindowsHook
            ? Visibility.Visible : Visibility.Collapsed;
        SnapTapBackendSelector.IsEnabled = !snapTapDriverInstalling;
        FunctionRowBackendSelector.IsEnabled = !snapTapDriverInstalling;
        SnapTapRuntimeStatus? status = localizationSnapshotMode ? null
            : SelectedBackendStatus(FunctionRowBackendStatusStore.Read(), functionRowBackend);
        FunctionRowBackendStatusText.Text = snapTapDriverInstalling && functionRowBackend == KeyboardInputBackend.RazerDriver
            ? LocalizedResources.Get("SnapTap_Driver_Installing", "Installing driver…")
            : snapTapDriverInstallOutcome is { } outcome && functionRowBackend == KeyboardInputBackend.RazerDriver
                ? outcome == RazerDriverInstallOutcome.Installed
                    ? LocalizedResources.Get("KeyboardBackend_DriverInstalled", "Driver installed. Switch input methods to retry.")
                    : DriverSetupResultText(outcome)
            : functionRowBackend == KeyboardInputBackend.Unknown
                ? LocalizedResources.Get("KeyboardBackend_Unknown", "Choose an input method to resume.")
            : status?.State switch
            {
                SnapTapRuntimeState.DriverMissing => LocalizedResources.Get("KeyboardBackend_DriverMissing", "The Razer keyboard driver is missing."),
                SnapTapRuntimeState.UnsupportedModel => LocalizedResources.Get("KeyboardBackend_Unsupported", "This input method is unavailable for this laptop."),
                SnapTapRuntimeState.RecoveryRequired => LocalizedResources.Get("KeyboardBackend_Recovery", "Restart Windows before using this input method again."),
                SnapTapRuntimeState.ManualDriverSetupRequired => LocalizedResources.Get("KeyboardBackend_ManualSetup", "Install this laptop’s Razer keyboard driver, then switch input methods to retry."),
                SnapTapRuntimeState.WaitingForFunctionKey => LocalizedResources.Get("KeyboardBackend_PrimeFn", "Press Fn once to activate multimedia keys."),
                SnapTapRuntimeState.WaitingForRelease => LocalizedResources.Get("SnapTap_Status_WaitingForRelease", "Release keys and mouse buttons to activate."),
                SnapTapRuntimeState.Starting => LocalizedResources.Get("SnapTap_Status_Starting", "Starting…"),
                SnapTapRuntimeState.Active or SnapTapRuntimeState.Armed when status.Backend == KeyboardInputBackend.RazerDriver && status.ExperimentalDriver => LocalizedResources.Get("SnapTap_Status_Experimental", "Experimental driver support"),
                SnapTapRuntimeState.Active or SnapTapRuntimeState.Armed or SnapTapRuntimeState.Off => string.Empty,
                SnapTapRuntimeState.Paused or SnapTapRuntimeState.Failed or SnapTapRuntimeState.Unavailable => LocalizedResources.Get("KeyboardBackend_Paused", "Input handling is paused. Export diagnostics for details."),
                _ => LocalizedResources.Get("KeyboardBackend_Waiting", "Waiting for the selected input method…"),
            };
        FunctionRowBackendStatusText.Visibility = string.IsNullOrEmpty(FunctionRowBackendStatusText.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        FunctionRowInstallDriverButton.Visibility = functionRowBackend == KeyboardInputBackend.RazerDriver
            && CanOfferSnapTapDriverSetup(status?.State)
            && snapTapDriverInstallOutcome is not (RazerDriverInstallOutcome.Installed or RazerDriverInstallOutcome.RestartRequired or RazerDriverInstallOutcome.RecoveryRequired)
            ? Visibility.Visible : Visibility.Collapsed;
        FunctionRowInstallDriverButton.IsEnabled = !snapTapDriverInstalling;
    }
}
