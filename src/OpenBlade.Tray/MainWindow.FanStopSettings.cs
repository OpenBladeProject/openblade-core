using OpenBlade.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    // Expansion is presentation only. Values are committed together after editing;
    // invalid combinations remain visible for correction without a service request.
    private async void OnFanStopSettingsLostFocus(object sender, RoutedEventArgs args) =>
        await CommitFanStopSettingsAsync();

    private async void OnFanStopEndModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ViewModel is null || !ViewModel.CanEditFanStopSettings || sender is not RadioButtons buttons)
        {
            return;
        }
        ViewModel.FanStopEndModeIndex = buttons.SelectedIndex;
        await CommitFanStopSettingsAsync();
    }

    private async void OnAutomaticFanStopToggled(object sender, RoutedEventArgs args)
    {
        if (ViewModel is null || !ViewModel.CanEditFanStopSettings || sender is not ToggleSwitch toggle)
        {
            return;
        }
        ViewModel.AutomaticFanStopEnabled = toggle.IsOn;
        await CommitFanStopSettingsAsync();
    }

    private async Task CommitFanStopSettingsAsync()
    {
        if (ViewModel is null || localizationSnapshotMode || Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        try
        {
            await ViewModel.ApplyFanStopSettingsAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException("OpenBlade.Tray.MainWindow", "SetFanStopSettings", exception);
        }
    }

    private async void OnResumeAutomaticFanStop(object sender, RoutedEventArgs args)
    {
        if (localizationSnapshotMode || Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        try
        {
            await ViewModel.ResumeAutomaticFanStopAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException("OpenBlade.Tray.MainWindow", "ResumeAutomaticFanStop", exception);
        }
    }
}
