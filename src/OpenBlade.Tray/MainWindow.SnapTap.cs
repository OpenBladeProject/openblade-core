using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private bool snapTapEnabled;
    private bool snapTapDriverInstalling;
    private RazerDriverInstallOutcome? snapTapDriverInstallOutcome;
    private SnapTapRuntimeState localizationSnapshotSnapTapState = SnapTapRuntimeState.Off;

    internal void ShowLocalizationSnapshotSnapTap(bool showFailure = false, bool showEnabled = false, bool showDriverMissing = false, string? driverSetupState = null)
    {
        if (localizationSnapshotMode)
        {
            localizationSnapshotSnapTapState = showDriverMissing
                ? SnapTapRuntimeState.DriverMissing
                : showFailure ? SnapTapRuntimeState.Failed
                : showEnabled ? SnapTapRuntimeState.Armed : SnapTapRuntimeState.Off;
            snapTapDriverInstalling = driverSetupState == "installing";
            snapTapDriverInstallOutcome = driverSetupState switch
            {
                "installed" => RazerDriverInstallOutcome.Installed,
                "restart" => RazerDriverInstallOutcome.RestartRequired,
                "failed" => RazerDriverInstallOutcome.RecoveryRequired,
                _ => null,
            };
            // Set the backing state first so the preview toggle cannot queue a preference save.
            snapTapEnabled = showFailure || showEnabled || showDriverMissing;
            SnapTapToggle.IsOn = snapTapEnabled;
            RefreshSnapTapStatus();
            KeyboardSection.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = false,
                VerticalAlignmentRatio = 0,
            });
        }
    }

    internal UIElement ShowLocalizationSnapshotSnapTapInformation()
    {
        if (!localizationSnapshotMode || SnapTapInfoButton.Flyout is not Flyout flyout)
        {
            throw new InvalidOperationException("SnapTap information preview requires snapshot mode.");
        }

        flyout.ShowAt(SnapTapInfoButton);
        return flyout.Content;
    }

    private void OnSnapTapToggled(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleSwitch toggle
            || Volatile.Read(ref disposed) != 0
            || toggle.IsOn == snapTapEnabled)
        {
            return;
        }

        snapTapEnabled = toggle.IsOn;
        snapTapDriverInstallOutcome = null;
        QueuePreferenceSave();
        RefreshSnapTapStatus();
    }

    private async void OnInstallSnapTapDriver(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(InstallSnapTapDriverAsync, nameof(OnInstallSnapTapDriver));

    private async Task InstallSnapTapDriverAsync()
    {
        if (localizationSnapshotMode || Volatile.Read(ref disposed) != 0 || snapTapDriverInstalling
            || !snapTapEnabled || SnapTapStatusStore.Read()?.State != SnapTapRuntimeState.DriverMissing)
        {
            return;
        }
        snapTapDriverInstalling = true;
        snapTapDriverInstallOutcome = null;
        RefreshSnapTapStatus();
        try
        {
            snapTapDriverInstallOutcome = await new RazerDriverInstallLauncher().RequestAsync(
                Environment.ProcessPath!, CancellationToken.None);
        }
        finally
        {
            snapTapDriverInstalling = false;
            if (Volatile.Read(ref disposed) == 0)
            {
                RefreshSnapTapStatus();
            }
        }
    }

    private void RefreshSnapTapStatus()
    {
        SnapTapRuntimeStatus? status = localizationSnapshotMode
            ? new SnapTapRuntimeStatus(localizationSnapshotSnapTapState, DateTimeOffset.UnixEpoch)
            : SnapTapStatusStore.Read();
        SnapTapStatusText.Text = snapTapDriverInstalling
            ? LocalizedResources.Get("SnapTap_Driver_Installing", "Installing driver…")
            : snapTapDriverInstallOutcome is { } outcome ? DriverSetupResultText(outcome)
            : status?.State switch
            {
                SnapTapRuntimeState.DriverMissing => LocalizedResources.Get(
                    "SnapTap_Status_DriverMissing", "SnapTap needs the Razer keyboard driver."),
                SnapTapRuntimeState.RecoveryRequired => LocalizedResources.Get(
                    "SnapTap_Status_RecoveryRequired", "Restart Windows before enabling SnapTap again."),
                SnapTapRuntimeState.Unavailable => LocalizedResources.Get(
                    "SnapTap_Status_DriverUnavailable", "The supported Razer keyboard driver is unavailable."),
                SnapTapRuntimeState.Starting => LocalizedResources.Get(
                    "SnapTap_Status_Starting", "Starting…"),
                SnapTapRuntimeState.WaitingForRelease => LocalizedResources.Get(
                    "SnapTap_Status_WaitingForRelease", "Release keys and mouse buttons to activate."),
                SnapTapRuntimeState.Armed or SnapTapRuntimeState.Active => string.Empty,
                SnapTapRuntimeState.Paused => LocalizedResources.Get(
                    "SnapTap_Status_Paused", "Paused — input handling is unavailable."),
                SnapTapRuntimeState.Failed => LocalizedResources.Get(
                    "SnapTap_Status_Failed", "Paused after an input error. Release A and D, then turn SnapTap off and on."),
                SnapTapRuntimeState.Off when !snapTapEnabled => string.Empty,
                _ when snapTapEnabled => LocalizedResources.Get(
                    "SnapTap_Status_Unavailable", "Waiting for the input helper…"),
                _ => string.Empty,
            };
        SnapTapDriverActions.Visibility = snapTapEnabled && status?.State == SnapTapRuntimeState.DriverMissing
            && snapTapDriverInstallOutcome != RazerDriverInstallOutcome.Installed
            ? Visibility.Visible
            : Visibility.Collapsed;
        SnapTapToggle.IsEnabled = !snapTapDriverInstalling;
        SnapTapInstallDriverButton.IsEnabled = !snapTapDriverInstalling;
        SnapTapInstallDriverButton.Visibility = snapTapDriverInstallOutcome is
            RazerDriverInstallOutcome.Installed or RazerDriverInstallOutcome.RestartRequired or RazerDriverInstallOutcome.RecoveryRequired
            ? Visibility.Collapsed
            : Visibility.Visible;
        SnapTapStatusText.Visibility = string.IsNullOrEmpty(SnapTapStatusText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static string DriverSetupResultText(RazerDriverInstallOutcome outcome) => outcome switch
    {
        RazerDriverInstallOutcome.Installed => LocalizedResources.Get(
            "SnapTap_Driver_Installed", "Driver installed. Turn SnapTap off and on."),
        RazerDriverInstallOutcome.RestartRequired => LocalizedResources.Get(
            "SnapTap_Driver_Restart", "Restart Windows, then follow the setup guide."),
        RazerDriverInstallOutcome.Canceled => LocalizedResources.Get(
            "SnapTap_Driver_Canceled", "Driver setup canceled."),
        RazerDriverInstallOutcome.AcPowerRequired => LocalizedResources.Get(
            "SnapTap_Driver_AcPower", "Connect AC power to install the driver."),
        RazerDriverInstallOutcome.RazerSoftwareRunning => LocalizedResources.Get(
            "SnapTap_Driver_CloseSynapse", "Close Razer Synapse, then try again."),
        _ => LocalizedResources.Get(
            "SnapTap_Driver_Failed", "Driver setup couldn't finish. Open the setup guide."),
    };

}
