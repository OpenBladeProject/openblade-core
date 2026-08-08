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

    [Theory]
    [InlineData("0.1.0", "0.1.0", true)]
    [InlineData("0.1.0", "0.1.0.0", true)]
    [InlineData("0.1.0.0", "0.1.0", true)]
    [InlineData("0.1.0", "0.1.0.1", false)]
    [InlineData("unknown", "0.1.0", false)]
    [InlineData("0.1.0", "invalid", false)]
    public void SameVersionDetectionUsesBurnComparableVersions(
        string current,
        string related,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.IsSameVersion(current, related));
    }

    [Fact]
    public void RelatedBundleExecutionDescribesRemoval()
    {
        Assert.Equal(
            "Removing OpenBlade 2.0.0",
            OpenBladeBootstrapperApplication.DescribeRelatedBundleExecution("2.0.0"));
    }

    [Theory]
    [InlineData(true, true, false, RelatedBundlePlanType.Downgrade, RelatedBundlePlanType.Upgrade)]
    [InlineData(false, true, false, RelatedBundlePlanType.Downgrade, RelatedBundlePlanType.Downgrade)]
    [InlineData(true, false, false, RelatedBundlePlanType.Downgrade, RelatedBundlePlanType.Downgrade)]
    [InlineData(true, false, true, RelatedBundlePlanType.Upgrade, RelatedBundlePlanType.None)]
    [InlineData(false, false, true, RelatedBundlePlanType.Upgrade, RelatedBundlePlanType.None)]
    public void RelatedBundlePlanProtectsVersionBoundaries(
        bool allowOverwrite,
        bool detectedNewerBundle,
        bool detectedSameVersionBundle,
        RelatedBundlePlanType recommendedType,
        RelatedBundlePlanType expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.SelectRelatedBundlePlanType(
                allowOverwrite,
                detectedNewerBundle,
                detectedSameVersionBundle,
                recommendedType));
    }

    [Theory]
    [InlineData(LaunchAction.Install, true, true)]
    [InlineData(LaunchAction.Repair, true, true)]
    [InlineData(LaunchAction.Uninstall, true, false)]
    [InlineData(LaunchAction.Layout, true, false)]
    [InlineData(LaunchAction.Unknown, true, false)]
    [InlineData(LaunchAction.Install, false, false)]
    public void SameVersionReplacementGateHonorsAction(
        LaunchAction action,
        bool detectedSameVersionBundle,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.ShouldBlockSameVersionReplacement(
                action,
                detectedSameVersionBundle));
    }

    [Theory]
    [InlineData(LaunchAction.Install, 0, false)]
    [InlineData(LaunchAction.Install, 1, false)]
    [InlineData(LaunchAction.Install, 2, true)]
    [InlineData(LaunchAction.Repair, 5, true)]
    [InlineData(LaunchAction.Uninstall, 5, false)]
    [InlineData(LaunchAction.Layout, 5, false)]
    [InlineData(LaunchAction.Unknown, 5, false)]
    public void MultipleRelatedBundleGateHonorsActionAndCount(
        LaunchAction action,
        int relatedBundleCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.ShouldBlockMultipleRelatedBundles(
                action,
                relatedBundleCount));
    }

    [Theory]
    [InlineData(LaunchAction.Unknown, Display.Full, LaunchAction.Install)]
    [InlineData(LaunchAction.Unknown, Display.Passive, LaunchAction.Unknown)]
    [InlineData(LaunchAction.Layout, Display.Full, LaunchAction.Layout)]
    [InlineData(LaunchAction.Uninstall, Display.Full, LaunchAction.Uninstall)]
    public void DetectionGateMapsTheInteractiveReadyPageToInstall(
        LaunchAction action,
        Display display,
        LaunchAction expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.SelectDetectionGateAction(action, display));
    }

    [Fact]
    public void MultipleRegistrationsUseAConfigurationError()
    {
        Assert.Equal(
            unchecked((int)0x80070666),
            OpenBladeBootstrapperApplication.SelectRelatedBundleConflictStatus(
                multipleRegistrations: false));
        Assert.Equal(
            unchecked((int)0x8007064A),
            OpenBladeBootstrapperApplication.SelectRelatedBundleConflictStatus(
                multipleRegistrations: true));
    }

    [Fact]
    public void ConfirmedOverwriteOnlyForcesTheSetupPackageDuringInstall()
    {
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
    }

    [Fact]
    public void ConfirmedOverwriteOnlySuppressesTheProductVersionConflict()
    {
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegistrationConflictHidesUnsafeActionsAndRetainsAvailableUninstall(
        bool canUninstall)
    {
        RunInSta(() =>
        {
            InstallerWindow window = new(new OpenBladeBootstrapperApplication(), "1.2.3");

            window.ShowRegistrationConflict("Resolve duplicate registrations.", canUninstall);

            Assert.Equal("Installer cleanup required", window.HeaderSubtitle.Text);
            Assert.Equal("Resolve existing OpenBlade installers", window.ReadyHeading.Text);
            Assert.Equal("Resolve duplicate registrations.", window.ReadyMessage.Text);
            Assert.Equal(Visibility.Collapsed, window.InstallButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.RepairButton.Visibility);
            Assert.Equal(
                canUninstall ? Visibility.Visible : Visibility.Collapsed,
                window.UninstallButton.Visibility);
            Assert.Equal(
                canUninstall ? Visibility.Visible : Visibility.Collapsed,
                window.PurgeProfilesCheckBox.Visibility);
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
