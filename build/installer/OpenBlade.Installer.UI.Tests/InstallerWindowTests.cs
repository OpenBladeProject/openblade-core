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

            window.ShowSameVersionMaintenance("1.2.3");
            Assert.Equal("OpenBlade 1.2.3 is installed", window.ReadyHeading.Text);
            Assert.Contains("overwrites", window.ReadyMessage.Text);
            Assert.Contains("keeping saved profiles", window.ReadyMessage.Text);
            Assert.Equal(Visibility.Collapsed, window.InstallButton.Visibility);
            Assert.Equal(Visibility.Visible, window.RepairButton.Visibility);
            Assert.Equal(Visibility.Visible, window.UninstallButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.PurgeProfilesCheckBox.Visibility);
            Assert.False(window.PurgeProfilesCheckBox.IsChecked);

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
    [InlineData(true, false, false, true, false, RelatedBundlePlanType.Downgrade, RelatedBundlePlanType.Upgrade)]
    [InlineData(false, false, false, true, false, RelatedBundlePlanType.Downgrade, RelatedBundlePlanType.Downgrade)]
    [InlineData(true, false, false, false, false, RelatedBundlePlanType.Downgrade, RelatedBundlePlanType.Downgrade)]
    [InlineData(true, false, false, false, true, RelatedBundlePlanType.Upgrade, RelatedBundlePlanType.None)]
    [InlineData(false, false, false, false, true, RelatedBundlePlanType.Upgrade, RelatedBundlePlanType.None)]
    [InlineData(false, true, false, false, true, RelatedBundlePlanType.Upgrade, RelatedBundlePlanType.Upgrade)]
    [InlineData(false, true, true, false, true, RelatedBundlePlanType.Upgrade, RelatedBundlePlanType.None)]
    public void RelatedBundlePlanProtectsVersionBoundaries(
        bool allowOverwrite,
        bool allowSameVersionMaintenance,
        bool relatedBundleInvocation,
        bool detectedNewerBundle,
        bool detectedSameVersionBundle,
        RelatedBundlePlanType recommendedType,
        RelatedBundlePlanType expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.SelectRelatedBundlePlanType(
                allowOverwrite,
                allowSameVersionMaintenance,
                relatedBundleInvocation,
                detectedNewerBundle,
                detectedSameVersionBundle,
                recommendedType));
    }

    [Theory]
    [InlineData(LaunchAction.Install, true, false, true)]
    [InlineData(LaunchAction.Repair, true, false, false)]
    [InlineData(LaunchAction.Repair, false, false, true)]
    [InlineData(LaunchAction.Uninstall, true, false, false)]
    [InlineData(LaunchAction.Uninstall, false, true, false)]
    [InlineData(LaunchAction.Uninstall, false, false, true)]
    [InlineData(LaunchAction.Layout, false, false, false)]
    [InlineData(LaunchAction.Unknown, false, false, false)]
    public void SameVersionReplacementGateHonorsMaintenanceOwnership(
        LaunchAction action,
        bool canOfferMaintenance,
        bool runningBundleInstalled,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.ShouldRejectSameVersionAction(
                action,
                canOfferMaintenance,
                runningBundleInstalled));
    }

    [Theory]
    [InlineData(false, false, true, true, 1, true)]
    [InlineData(true, false, true, true, 1, false)]
    [InlineData(false, true, true, true, 1, false)]
    [InlineData(false, false, false, true, 1, false)]
    [InlineData(false, false, true, false, 1, false)]
    [InlineData(false, false, true, true, 2, false)]
    public void SameVersionMaintenanceRequiresOneCachedPredecessorWithoutNewerOwnership(
        bool runningBundleInstalled,
        bool newerVersionDetected,
        bool relatedBundleCached,
        bool relatedBundleCodeDetected,
        int relatedBundleCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.CanOfferSameVersionMaintenance(
                runningBundleInstalled,
                newerVersionDetected,
                relatedBundleCached,
                relatedBundleCodeDetected,
                relatedBundleCount));
    }

    [Theory]
    [InlineData(LaunchAction.Unknown, Display.Full, true, true)]
    [InlineData(LaunchAction.Install, Display.Full, true, true)]
    [InlineData(LaunchAction.Repair, Display.Full, true, true)]
    [InlineData(LaunchAction.Uninstall, Display.Full, true, true)]
    [InlineData(LaunchAction.Unknown, Display.Passive, true, false)]
    [InlineData(LaunchAction.Unknown, Display.Full, false, false)]
    public void SameVersionMaintenanceRequiresAnInteractiveInstallEntryPoint(
        LaunchAction action,
        Display display,
        bool canOfferMaintenance,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.ShouldOfferSameVersionMaintenance(
                action,
                display,
                canOfferMaintenance));
    }

    [Theory]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef", true)]
    [InlineData("0123456789abcdef0123456789abcdef", false)]
    [InlineData("{01234567-89ab-cdef-0123-456789abcdef}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void BundleOwnerIdRequiresCanonicalDFormat(string? value, bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.IsValidBundleOwnerId(value));
    }

    [Theory]
    [InlineData(true, true, "choose Repair or Uninstall")]
    [InlineData(false, false, "cached installer is unavailable")]
    [InlineData(false, true, "cannot safely choose one maintenance owner")]
    public void SameVersionConflictGuidanceMatchesTheFullMaintenanceGate(
        bool canOfferMaintenance,
        bool relatedBundleCached,
        string expected)
    {
        Assert.Contains(
            expected,
            OpenBladeBootstrapperApplication.SelectSameVersionConflictGuidance(
                canOfferMaintenance,
                relatedBundleCached));
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
    public void OverwriteAndSameVersionMaintenanceSelectTheSetupPackageState()
    {
        Assert.Equal(
            RequestState.ForcePresent,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                allowOverwrite: true,
                sameVersionMaintenance: false,
                LaunchAction.Install,
                "OpenBladeSetup",
                RequestState.Present));
        Assert.Equal(
            RequestState.Present,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                allowOverwrite: false,
                sameVersionMaintenance: false,
                LaunchAction.Install,
                "OpenBladeSetup",
                RequestState.Present));
        Assert.Equal(
            RequestState.Present,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                allowOverwrite: true,
                sameVersionMaintenance: false,
                LaunchAction.Repair,
                "OpenBladeSetup",
                RequestState.Present));
        Assert.Equal(
            RequestState.Present,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                allowOverwrite: true,
                sameVersionMaintenance: false,
                LaunchAction.Install,
                "OpenBladeVcRuntime",
                RequestState.Present));
        Assert.Equal(
            RequestState.ForcePresent,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                allowOverwrite: false,
                sameVersionMaintenance: true,
                LaunchAction.Repair,
                "OpenBladeSetup",
                RequestState.Repair));
        Assert.Equal(
            RequestState.Absent,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                allowOverwrite: false,
                sameVersionMaintenance: true,
                LaunchAction.Uninstall,
                "OpenBladeSetup",
                RequestState.Absent));
    }

    [Fact]
    public void SameVersionMaintenanceTargetsOnlyTheInstalledRelatedBundle()
    {
        Assert.Equal(
            RequestState.Absent,
            OpenBladeBootstrapperApplication.SelectRelatedBundleRequestState(
                sameVersionMaintenance: true,
                LaunchAction.Uninstall,
                detectedSameVersionBundle: true,
                RequestState.None));
        Assert.Equal(
            RequestState.None,
            OpenBladeBootstrapperApplication.SelectRelatedBundleRequestState(
                sameVersionMaintenance: true,
                LaunchAction.Uninstall,
                detectedSameVersionBundle: false,
                RequestState.None));
    }

    [Theory]
    [InlineData(LaunchAction.Repair, true, LaunchAction.Install)]
    [InlineData(LaunchAction.Repair, false, LaunchAction.Repair)]
    [InlineData(LaunchAction.Uninstall, true, LaunchAction.Uninstall)]
    public void SameVersionRepairUsesAnInstallPlan(
        LaunchAction action,
        bool sameVersionMaintenance,
        LaunchAction expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.SelectEngineAction(
                action,
                sameVersionMaintenance));
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
