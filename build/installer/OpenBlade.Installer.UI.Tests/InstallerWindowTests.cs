using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using WixToolset.BootstrapperApplicationApi;
using Xunit;

namespace OpenBlade.Installer.UI.Tests;

public sealed class InstallerWindowTests
{
    [Fact]
    public void WindowLoadsResourcesAndRendersEveryInteractiveState()
    {
        RunInSta(() =>
        {
            InstallerWindow window = new(new OpenBladeBootstrapperApplication(), "1.2.3");

            Assert.NotNull(window.BrandImage.Source);
            Assert.Equal("Version 1.2.3", window.VersionText.Text);

            InstallerTheme.Apply(window, dark: true);
            Assert.Equal(
                Color.FromRgb(0x20, 0x20, 0x20),
                ((SolidColorBrush)window.Resources["WindowBrush"]).Color);
            Assert.Equal(
                Color.FromRgb(0x15, 0x15, 0x15),
                ((SolidColorBrush)window.Resources["ProgressTrackBrush"]).Color);

            window.ShowReady(installed: false);
            Assert.Equal(Visibility.Visible, window.InstallButton.Visibility);

            window.ShowReady(installed: true);
            Assert.Equal(Visibility.Visible, window.RepairButton.Visibility);
            Assert.Equal(Visibility.Visible, window.UninstallButton.Visibility);
            Assert.Equal(Visibility.Visible, window.PurgeProfilesCheckBox.Visibility);

            window.ShowProgress(LaunchAction.Install);
            window.SetProgress(42, "Copying OpenBlade.Tray.exe");
            window.AddDetail("Copying OpenBlade.Tray.exe");
            window.DetailsToggle.IsChecked = true;
            Assert.Equal(42, window.OverallProgress.Value);
            Assert.Contains("Copying OpenBlade.Tray.exe", window.DetailsText.Text);
            Assert.Equal(Visibility.Visible, window.DetailsBorder.Visibility);

            window.ProgressCancelButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.Equal(Visibility.Visible, window.CancelConfirmationPanel.Visibility);

            window.ShowSuccess(LaunchAction.Install);
            Assert.Equal(Visibility.Collapsed, window.CancelConfirmationPanel.Visibility);
            Assert.Equal(Visibility.Visible, window.LaunchButton.Visibility);
            window.ShowSuccess(LaunchAction.Uninstall);
            Assert.Equal(Visibility.Collapsed, window.LaunchButton.Visibility);

            window.ShowHelp();
            Assert.Equal("Help", window.HeaderSubtitle.Text);
            Assert.Equal(Visibility.Collapsed, window.LaunchButton.Visibility);

            window.ShowFailure("Expected test failure", null);
            Assert.Equal("Expected test failure", window.FailureMessage.Text);
            Assert.False(window.OpenLogButton.IsEnabled);
        });
    }

    [Fact]
    public void PassiveProgressHidesInteractiveControls()
    {
        RunInSta(() =>
        {
            InstallerWindow window = new(
                new OpenBladeBootstrapperApplication(),
                "1.2.3",
                allowInteraction: false);

            window.ShowProgress(LaunchAction.Repair);

            Assert.Equal(Visibility.Collapsed, window.DetailsToggle.Visibility);
            Assert.Equal(Visibility.Collapsed, window.ProgressCancelButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.DetailsBorder.Visibility);
        });
    }

    private static void RunInSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The installer UI test timed out.");
        failure?.Throw();
    }
}
