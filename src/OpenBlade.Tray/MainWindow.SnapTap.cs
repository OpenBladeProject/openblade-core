using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private bool snapTapEnabled;
    private SnapTapRuntimeState localizationSnapshotSnapTapState = SnapTapRuntimeState.Off;

    internal void ShowLocalizationSnapshotSnapTap(bool showFailure = false, bool showEnabled = false)
    {
        if (localizationSnapshotMode)
        {
            localizationSnapshotSnapTapState = showFailure
                ? SnapTapRuntimeState.Failed
                : showEnabled ? SnapTapRuntimeState.Armed : SnapTapRuntimeState.Off;
            // Set the backing state first so the preview toggle cannot queue a preference save.
            snapTapEnabled = showFailure || showEnabled;
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
        QueuePreferenceSave();
        RefreshSnapTapStatus();
    }

    private void RefreshSnapTapStatus()
    {
        SnapTapRuntimeStatus? status = localizationSnapshotMode
            ? new SnapTapRuntimeStatus(localizationSnapshotSnapTapState, DateTimeOffset.UnixEpoch)
            : SnapTapStatusStore.Read();
        SnapTapStatusText.Text = status?.State switch
        {
            SnapTapRuntimeState.Starting => LocalizedResources.Get(
                "SnapTap_Status_Starting", "Starting…"),
            SnapTapRuntimeState.WaitingForRelease => LocalizedResources.Get(
                "SnapTap_Status_WaitingForRelease", "Release A and D to activate."),
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
        SnapTapStatusText.Visibility = string.IsNullOrEmpty(SnapTapStatusText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
