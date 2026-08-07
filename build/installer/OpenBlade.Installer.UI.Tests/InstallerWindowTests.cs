using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
            Assert.False(window.Resources.Contains("CardBrush"));
            Assert.Null(window.ContentHost.Background);
            Assert.Equal(new Thickness(20, 0, 20, 0), window.ContentHost.Margin);
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

            window.ShowNewerVersionConfirmation(
                "2.0.0",
                "1.2.3",
                returnToInstalledState: true);
            Assert.Equal("Overwrite", window.InstallButton.Content);
            Assert.Contains("2.0.0", window.ReadyMessage.Text);
            Assert.Contains("1.2.3", window.ReadyMessage.Text);
            Assert.Equal(Visibility.Collapsed, window.RepairButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.UninstallButton.Visibility);

            window.ReadyCancelButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.Equal("Maintenance", window.HeaderSubtitle.Text);
            Assert.Equal("Install", window.InstallButton.Content);
            Assert.Equal(Visibility.Visible, window.RepairButton.Visibility);
            Assert.Equal(Visibility.Visible, window.UninstallButton.Visibility);

            window.ShowProgress(LaunchAction.Install);
            window.SetProgress(42, "Copying OpenBlade.Tray.exe");
            window.AddDetail("Copying OpenBlade.Tray.exe");
            window.DetailsToggle.IsChecked = true;
            Assert.Equal(42, window.OverallProgress.Value);
            Assert.Contains("Copying OpenBlade.Tray.exe", window.DetailsText.Text);
            Assert.Equal(Visibility.Visible, window.DetailsBorder.Visibility);
            Assert.Equal(430, window.Height);
            Assert.Equal(430, window.MinHeight);
            Assert.Equal(TextWrapping.Wrap, window.DetailsText.TextWrapping);
            Assert.Equal(
                ScrollBarVisibility.Disabled,
                window.DetailsText.HorizontalScrollBarVisibility);
            Assert.Equal(new Thickness(0, 8, 0, 12), window.DetailsBorder.Margin);

            window.ProgressCancelButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.Equal(Visibility.Visible, window.CancelConfirmationPanel.Visibility);
            Assert.Equal(
                ((SolidColorBrush)window.Resources["WindowBrush"]).Color,
                ((SolidColorBrush)window.CancelConfirmationPanel.Background).Color);

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

    [Theory]
    [InlineData("1.2.3", "1.2.4", true)]
    [InlineData("1.2.3.4", "1.2.3.5", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("0.1.0", "0.1.0.0", false)]
    [InlineData("0.1.0.0", "0.1.0", false)]
    [InlineData("2.0.0", "1.9.9", false)]
    [InlineData("invalid", "2.0.0", false)]
    public void NewerVersionDetectionUsesBurnComparableVersions(
        string current,
        string related,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.IsNewerVersion(current, related));
    }

    [Fact]
    public void RelatedBundlePlanSkipsEqualVersionCyclesAndProtectsNewerVersions()
    {
        Assert.Equal(
            "Removing OpenBlade 2.0.0",
            OpenBladeBootstrapperApplication.DescribeRelatedBundleExecution("2.0.0"));
        Assert.Equal(
            RelatedBundlePlanType.Upgrade,
            OpenBladeBootstrapperApplication.SelectRelatedBundlePlanType(
                allowOverwrite: true,
                detectedNewerBundle: true,
                detectedSameVersionBundle: false,
                RelatedBundlePlanType.Downgrade));
        Assert.Equal(
            RelatedBundlePlanType.Downgrade,
            OpenBladeBootstrapperApplication.SelectRelatedBundlePlanType(
                allowOverwrite: false,
                detectedNewerBundle: true,
                detectedSameVersionBundle: false,
                RelatedBundlePlanType.Downgrade));
        Assert.Equal(
            RelatedBundlePlanType.Downgrade,
            OpenBladeBootstrapperApplication.SelectRelatedBundlePlanType(
                allowOverwrite: true,
                detectedNewerBundle: false,
                detectedSameVersionBundle: false,
                RelatedBundlePlanType.Downgrade));
        Assert.Equal(
            RelatedBundlePlanType.None,
            OpenBladeBootstrapperApplication.SelectRelatedBundlePlanType(
                allowOverwrite: true,
                detectedNewerBundle: false,
                detectedSameVersionBundle: true,
                RelatedBundlePlanType.Upgrade));
        Assert.Equal(
            RelatedBundlePlanType.None,
            OpenBladeBootstrapperApplication.SelectRelatedBundlePlanType(
                allowOverwrite: false,
                detectedNewerBundle: false,
                detectedSameVersionBundle: true,
                RelatedBundlePlanType.Upgrade));

        Assert.True(
            OpenBladeBootstrapperApplication.ShouldBlockSameVersionReplacement(
                LaunchAction.Install,
                detectedSameVersionBundle: true));
        Assert.True(
            OpenBladeBootstrapperApplication.ShouldBlockSameVersionReplacement(
                LaunchAction.Repair,
                detectedSameVersionBundle: true));
        Assert.False(
            OpenBladeBootstrapperApplication.ShouldBlockSameVersionReplacement(
                LaunchAction.Uninstall,
                detectedSameVersionBundle: true));
        Assert.False(
            OpenBladeBootstrapperApplication.ShouldBlockSameVersionReplacement(
                LaunchAction.Install,
                detectedSameVersionBundle: false));

        Assert.False(
            OpenBladeBootstrapperApplication.ShouldBlockMultipleRelatedBundles(
                LaunchAction.Install,
                relatedBundleCount: 1));
        Assert.True(
            OpenBladeBootstrapperApplication.ShouldBlockMultipleRelatedBundles(
                LaunchAction.Install,
                relatedBundleCount: 2));
        Assert.True(
            OpenBladeBootstrapperApplication.ShouldBlockMultipleRelatedBundles(
                LaunchAction.Repair,
                relatedBundleCount: 5));
        Assert.False(
            OpenBladeBootstrapperApplication.ShouldBlockMultipleRelatedBundles(
                LaunchAction.Uninstall,
                relatedBundleCount: 5));

        Assert.Equal(
            RequestState.ForcePresent,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                allowOverwrite: true,
                LaunchAction.Install,
                "OpenBladeSetup",
                RequestState.Present));
        Assert.Equal(
            RequestState.Present,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                allowOverwrite: false,
                LaunchAction.Install,
                "OpenBladeSetup",
                RequestState.Present));
        Assert.Equal(
            RequestState.Present,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                allowOverwrite: true,
                LaunchAction.Repair,
                "OpenBladeSetup",
                RequestState.Present));
        Assert.Equal(
            RequestState.Present,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                allowOverwrite: true,
                LaunchAction.Install,
                "OpenBladeVcRuntime",
                RequestState.Present));

        const int productVersionError = unchecked((int)0x80070666);
        Assert.Equal(
            0,
            OpenBladeBootstrapperApplication.SelectApplyDowngradeStatus(
                allowOverwrite: true,
                productVersionError));
        Assert.Equal(
            productVersionError,
            OpenBladeBootstrapperApplication.SelectApplyDowngradeStatus(
                allowOverwrite: false,
                productVersionError));
        const int unexpectedFailure = unchecked((int)0x80004005);
        Assert.Equal(
            unexpectedFailure,
            OpenBladeBootstrapperApplication.SelectApplyDowngradeStatus(
                allowOverwrite: true,
                unexpectedFailure));
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

    [Fact]
    public void HiddenApplyOwnerProvidesANativeHandleForNonVisualModes()
    {
        RunInSta(() =>
        {
            using HwndSource owner = OpenBladeBootstrapperApplication.CreateHiddenApplyOwner();

            Assert.NotEqual(IntPtr.Zero, owner.Handle);
            Assert.Null(owner.RootVisual);
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
