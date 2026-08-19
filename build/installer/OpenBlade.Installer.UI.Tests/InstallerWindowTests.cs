using System;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WixToolset.BootstrapperApplicationApi;
using Xunit;

namespace OpenBlade.Installer.UI.Tests;

public sealed class InstallerWindowTests
{
    [Fact]
    public void MissingCultureFallsBackToAuthoritativeEnglishResources()
    {
        RunWithUiCulture("fr-FR", () =>
        {
            InstallerWindow window = new(new OpenBladeBootstrapperApplication(), "1.2.3");
            window.ShowReady(installed: false);

            Assert.Equal("OpenBlade Setup", window.Title);
            Assert.Equal("Install OpenBlade", window.ReadyHeading.Text);
            Assert.Equal("Version 1.2.3", window.VersionText.Text);
        });
    }

    [Fact]
    public void PseudoCultureLoadsExpandedResourcesForEveryInstallerState()
    {
        RunWithUiCulture("qps-ploc", () =>
        {
            InstallerWindow window = new(new OpenBladeBootstrapperApplication(), "1.2.3");
            Assert.StartsWith("[!! ", window.Title, StringComparison.Ordinal);
            Assert.Contains("1.2.3", window.VersionText.Text, StringComparison.Ordinal);

            window.ShowReady(installed: false);
            Assert.StartsWith("[!! ", window.ReadyHeading.Text, StringComparison.Ordinal);
            window.ShowProgress(LaunchAction.Install);
            Assert.StartsWith("[!! ", window.ProgressHeading.Text, StringComparison.Ordinal);
            window.ShowSuccess(LaunchAction.Uninstall, purgeProfiles: true);
            Assert.StartsWith("[!! ", window.SuccessMessage.Text, StringComparison.Ordinal);
            window.ShowFailure(string.Empty, null);
            Assert.StartsWith("[!! ", window.FailureMessage.Text, StringComparison.Ordinal);
            window.ShowHelp();
            Assert.StartsWith("[!! ", window.SuccessMessage.Text, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("zh-CN", "安装 OpenBlade")]
    [InlineData("ru-RU", "Установить OpenBlade")]
    public void SupportedCultureLoadsTranslatedInstallerResources(
        string cultureName,
        string expected)
    {
        RunWithUiCulture(cultureName, () =>
        {
            InstallerWindow window = new(new OpenBladeBootstrapperApplication(), "1.2.3");
            window.ShowReady(installed: false);

            Assert.Equal(expected, window.ReadyHeading.Text);
        });
    }

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
            Assert.Contains("Windows Installed apps", window.ReadyMessage.Text);
            Assert.Equal(Visibility.Collapsed, window.InstallButton.Visibility);
            Assert.Equal(Visibility.Visible, window.RepairButton.Visibility);
            Assert.Equal(Visibility.Visible, window.UninstallButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.PurgeProfilesCheckBox.Visibility);
            Assert.False(window.PurgeProfilesCheckBox.IsChecked);

            window.ShowNewerVersionConfirmation(
                "2.0.0",
                "1.2.3",
                returnToInstalledState: true);
            Assert.Equal("_Overwrite", window.InstallButton.Content);
            Assert.Contains("2.0.0", window.ReadyMessage.Text);
            Assert.Contains("1.2.3", window.ReadyMessage.Text);
            Assert.Equal(Visibility.Collapsed, window.RepairButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.UninstallButton.Visibility);

            window.ReadyCancelButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.Equal("Maintenance", window.HeaderSubtitle.Text);
            Assert.Equal("_Install", window.InstallButton.Content);
            Assert.Equal(Visibility.Visible, window.RepairButton.Visibility);
            Assert.Equal(Visibility.Visible, window.UninstallButton.Visibility);

            window.ShowProgress(LaunchAction.Install);
            window.SetProgress(42, "Copying OpenBlade.Tray.exe");
            string localizedStatus = window.ProgressMessage.Text;
            window.AddDetail("Copying OpenBlade.Tray.exe");
            window.DetailsToggle.IsChecked = true;
            Assert.Equal(42, window.OverallProgress.Value);
            Assert.Contains("Copying OpenBlade.Tray.exe", window.DetailsText.Text);
            Assert.Equal(localizedStatus, window.ProgressMessage.Text);
            Assert.Equal(Visibility.Visible, window.DetailsBorder.Visibility);
            Assert.Equal(560, window.Height);
            Assert.Equal(560, window.MinHeight);
            Assert.Equal(TextWrapping.Wrap, window.DetailsText.TextWrapping);
            Assert.Equal(
                ScrollBarVisibility.Disabled,
                window.DetailsText.HorizontalScrollBarVisibility);
            Assert.Equal(new Thickness(0, 8, 0, 12), window.DetailsBorder.Margin);
            Assert.Equal(double.PositiveInfinity, window.DetailsBorder.MaxHeight);
            Assert.Equal(VerticalAlignment.Stretch, window.DetailsBorder.VerticalAlignment);

            window.ProgressCancelButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.Equal(Visibility.Visible, window.CancelConfirmationPanel.Visibility);
            Assert.Equal(
                ((SolidColorBrush)window.Resources["WindowBrush"]).Color,
                ((SolidColorBrush)window.CancelConfirmationPanel.Background).Color);

            window.ShowSuccess(LaunchAction.Install, purgeProfiles: false);
            Assert.Equal(Visibility.Collapsed, window.CancelConfirmationPanel.Visibility);
            Assert.Equal(Visibility.Visible, window.LaunchButton.Visibility);
            window.ShowSuccess(LaunchAction.Uninstall, purgeProfiles: false);
            Assert.Equal(Visibility.Collapsed, window.LaunchButton.Visibility);
            Assert.Contains("kept", window.SuccessMessage.Text);
            window.ShowSuccess(LaunchAction.Uninstall, purgeProfiles: true);
            Assert.Contains("also removed", window.SuccessMessage.Text);

            window.ShowHelp();
            Assert.Equal("Help", window.HeaderSubtitle.Text);
            Assert.Equal(Visibility.Collapsed, window.LaunchButton.Visibility);

            window.ShowFailure("Expected test failure", null);
            Assert.Equal("Expected test failure", window.FailureMessage.Text);
            Assert.False(window.OpenLogButton.IsEnabled);
        });
    }

    [Fact]
    public void MalformedTranslatedFormatFallsBackToAuthoritativeEnglishTemplate()
    {
        Assert.Equal(
            "Value: 42",
            InstallerStrings.FormatLocalized("Value: {0}", "Broken {", 42));
    }

    [Fact]
    public void LocalizationSnapshotsRenderEveryCriticalInstallerState()
    {
        string? outputDirectory = Environment.GetEnvironmentVariable(
            "OPENBLADE_LOCALIZATION_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        string? snapshotState = Environment.GetEnvironmentVariable(
            "OPENBLADE_LOCALIZATION_SNAPSHOT_STATE");
        Assert.False(
            string.IsNullOrWhiteSpace(snapshotState),
            "Set OPENBLADE_LOCALIZATION_SNAPSHOT_STATE so each state renders in a fresh testhost process.");
        string snapshotCulture = Environment.GetEnvironmentVariable(
                "OPENBLADE_LOCALIZATION_LANGUAGE")
            ?? "qps-ploc";
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        Directory.CreateDirectory(outputDirectory);
        switch (snapshotState)
        {
            case "install":
                Capture(
                    "installer-install",
                    window => window.ShowReady(installed: false),
                    window => [window.InstallButton, window.ReadyCancelButton]);
                break;
            case "maintenance":
                Capture(
                    "installer-maintenance",
                    window => window.ShowReady(installed: true),
                    window =>
                    [
                        window.RepairButton,
                        window.UninstallButton,
                        window.ReadyCancelButton,
                    ]);
                break;
            case "newer-version":
                Capture(
                    "installer-newer-version",
                    window => window.ShowNewerVersionConfirmation("0.11.0", "0.10.0", true),
                    window => [window.InstallButton, window.ReadyCancelButton]);
                break;
            case "progress":
                Capture(
                    "installer-progress",
                    window =>
                    {
                        window.ShowProgress(LaunchAction.Install);
                        window.SetProgress(47, InstallerStrings.Get("Installing OpenBlade"));
                        window.AddDetail("Stable diagnostic detail: OpenBlade.Tray.exe");
                        window.DetailsToggle.IsChecked = true;
                    },
                    window =>
                    [
                        window.DetailsToggle,
                        window.DetailsBorder,
                        window.ProgressCancelButton,
                    ]);
                break;
            case "cancel":
                Capture(
                    "installer-cancel",
                    window =>
                    {
                        window.ShowProgress(LaunchAction.Install);
                        window.ProgressCancelButton.RaiseEvent(
                            new RoutedEventArgs(Button.ClickEvent));
                    },
                    window => [window.KeepWorkingButton, window.ConfirmCancelButton]);
                break;
            case "success":
                Capture(
                    "installer-success",
                    window => window.ShowSuccess(LaunchAction.Install, purgeProfiles: false),
                    window => [window.LaunchButton, window.SuccessCloseButton]);
                break;
            case "failure":
                Capture(
                    "installer-failure",
                    window => window.ShowFailure(string.Empty, null),
                    window => [window.OpenLogButton, window.FailureCloseButton]);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown installer localization snapshot state '{snapshotState}'.");
        }

        void Capture(
            string name,
            Action<InstallerWindow> showState,
            Func<InstallerWindow, FrameworkElement[]> selectActiveActions)
        {
            RunWithUiCulture(snapshotCulture, () =>
            {
                InstallerWindow window = new(
                    new OpenBladeBootstrapperApplication(),
                    "0.10.0")
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 40,
                    Top = 40,
                    ShowInTaskbar = false,
                    Topmost = true,
                };
                window.Show();
                try
                {
                    showState(window);
                    PumpDispatcher(window.Dispatcher);
                    window.UpdateLayout();
                    FrameworkElement visual = (FrameworkElement)window.Content;
                    FrameworkElement[] activeActions = selectActiveActions(window);
                    AssertWithinSnapshot(visual, window.HeaderPanel);
                    AssertWithinSnapshot(visual, window.VersionText);
                    foreach (FrameworkElement action in activeActions)
                    {
                        AssertWithinSnapshot(visual, action);
                    }
                    _ = window.Activate();
                    PumpDispatcher(window.Dispatcher);
                    // Let the focused button template finish drawing before PrintWindow.
                    Thread.Sleep(500);
                    PumpDispatcher(window.Dispatcher);
                    IntPtr handle = new WindowInteropHelper(window).Handle;
                    Assert.True(GetWindowRect(handle, out NativeRect windowBounds));
                    int width = windowBounds.Right - windowBounds.Left;
                    int height = windowBounds.Bottom - windowBounds.Top;
                    Assert.True(width > 0 && height > 0);
                    using var bitmap = new System.Drawing.Bitmap(width, height);
                    using (System.Drawing.Graphics graphics =
                           System.Drawing.Graphics.FromImage(bitmap))
                    {
                        IntPtr deviceContext = graphics.GetHdc();
                        try
                        {
                            Assert.True(PrintWindow(handle, deviceContext, 2));
                        }
                        finally
                        {
                            graphics.ReleaseHdc(deviceContext);
                        }
                    }
                    bitmap.Save(
                        Path.Combine(outputDirectory, name + ".png"),
                        System.Drawing.Imaging.ImageFormat.Png);
                }
                finally
                {
                    window.Close();
                }
            });
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static void AssertWithinSnapshot(
        FrameworkElement snapshot,
        FrameworkElement element)
    {
        Rect bounds = element.TransformToAncestor(snapshot).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        Rect viewport = new(0, 0, snapshot.RenderSize.Width, snapshot.RenderSize.Height);
        Assert.True(
            viewport.Contains(bounds),
            $"{element.Name} bounds {bounds} exceed snapshot viewport {viewport}.");
    }

    [Fact]
    public void InteractiveStatesExposeAutomationNamesAndUniqueAccessKeys()
    {
        RunInSta(() =>
        {
            InstallerWindow window = new(new OpenBladeBootstrapperApplication(), "1.2.3");

            Assert.False(window.BrandImage.Focusable);
            Assert.False(window.BrandImage.IsHitTestVisible);
            window.ShowReady(installed: false);
            AssertUniqueAccessKeys(window.InstallButton, window.ReadyCancelButton);

            window.ShowReady(installed: true);
            AssertUniqueAccessKeys(
                window.PurgeProfilesCheckBox,
                window.RepairButton,
                window.UninstallButton,
                window.ReadyCancelButton);

            window.ShowProgress(LaunchAction.Install);
            Assert.Equal("Setup progress", AutomationProperties.GetName(window.OverallProgress));
            Assert.Equal("Setup details", AutomationProperties.GetName(window.DetailsText));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(window.ProgressMessage));
            AssertUniqueAccessKeys(window.DetailsToggle, window.ProgressCancelButton);

            window.ProgressCancelButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.ProgressPanel.IsEnabled);
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(window.CancelConfirmationPanel));
            AssertUniqueAccessKeys(window.KeepWorkingButton, window.ConfirmCancelButton);

            window.KeepWorkingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.ProgressPanel.IsEnabled);

            window.ShowSuccess(LaunchAction.Install, purgeProfiles: false);
            AssertUniqueAccessKeys(window.LaunchButton, window.SuccessCloseButton);

            window.ShowFailure("Expected", null);
            AssertUniqueAccessKeys(window.OpenLogButton, window.FailureCloseButton);
        });
    }

    [Fact]
    public void InteractiveStateChangesMoveKeyboardFocusToThePrimaryAction()
    {
        RunInSta(() =>
        {
            InstallerWindow window = new(new OpenBladeBootstrapperApplication(), "1.2.3")
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
            };
            window.Show();
            _ = window.Activate();

            window.ShowReady(installed: false);
            DrainFocus(window);
            Assert.Same(window.InstallButton, FocusManager.GetFocusedElement(window));

            window.ShowProgress(LaunchAction.Install);
            DrainFocus(window);
            Assert.Same(window.ProgressCancelButton, FocusManager.GetFocusedElement(window));

            window.ProgressCancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DrainFocus(window);
            Assert.Same(window.KeepWorkingButton, FocusManager.GetFocusedElement(window));

            window.KeepWorkingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DrainFocus(window);
            Assert.Same(window.ProgressCancelButton, FocusManager.GetFocusedElement(window));

            window.ShowSuccess(LaunchAction.Install, purgeProfiles: false);
            DrainFocus(window);
            Assert.Same(window.LaunchButton, FocusManager.GetFocusedElement(window));
            window.Close();
        });
    }

    [Fact]
    public void EveryProgressCodeHasPseudoLocalizedVisibleStatus()
    {
        RunWithUiCulture("qps-ploc", () =>
        {
            foreach (InstallerSetupProgressCode code in Enum.GetValues(
                         typeof(InstallerSetupProgressCode)))
            {
                string wireValue = code == InstallerSetupProgressCode.CopyFile
                    ? "v1|CopyFile|T3BlbkJsYWRlLlRyYXkuZXhl"
                    : $"v1|{code}";
                Assert.True(InstallerSetupProgress.TryParse(wireValue, out InstallerSetupProgress? progress));

                string status = OpenBladeBootstrapperApplication.LocalizeProgress(progress!);

                Assert.StartsWith("[!! ", status, StringComparison.Ordinal);
                Assert.False(status.StartsWith("v1|", StringComparison.Ordinal));
            }
        });
    }

    [Fact]
    public void PipeProgressKeepsWireEnvelopeOutOfVisibleDetails()
    {
        RunWithUiCulture("qps-ploc", () =>
        {
            const string wireValue = "v1|CopyFile|T3BlbkJsYWRlLlRyYXkuZXhl";
            Assert.True(InstallerSetupProgress.TryParse(
                wireValue,
                out InstallerSetupProgress? progress));
            var window = new InstallerWindow(new OpenBladeBootstrapperApplication(), "1.2.3");
            window.ShowProgress(LaunchAction.Install);

            OpenBladeBootstrapperApplication.PresentPipeProgress(window, progress!);

            Assert.StartsWith("[!! ", window.ProgressMessage.Text, StringComparison.Ordinal);
            Assert.Contains("OpenBlade.Tray.exe", window.ProgressMessage.Text, StringComparison.Ordinal);
            Assert.Contains("OpenBlade.Tray.exe", window.DetailsText.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(wireValue, window.DetailsText.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "T3BlbkJsYWRlLlRyYXkuZXhl",
                window.DetailsText.Text,
                StringComparison.Ordinal);
            window.Close();
        });
    }

    [Fact]
    public void PackageExecutionStatusIsLocalizedWithoutUsingBurnDisplayNames()
    {
        RunWithUiCulture("qps-ploc", () =>
        {
            string prerequisite = OpenBladeBootstrapperApplication.DescribePackageExecutionStatus(
                "OpenBladeVcRuntime",
                relatedVersion: null);
            string setup = OpenBladeBootstrapperApplication.DescribePackageExecutionStatus(
                "OpenBladeSetup",
                relatedVersion: null);
            string related = OpenBladeBootstrapperApplication.DescribePackageExecutionStatus(
                "{RELATED-BUNDLE}",
                "1.2.3");

            Assert.StartsWith("[!! ", prerequisite, StringComparison.Ordinal);
            Assert.StartsWith("[!! ", setup, StringComparison.Ordinal);
            Assert.StartsWith("[!! ", related, StringComparison.Ordinal);
            Assert.Contains("1.2.3", related, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(LaunchAction.Unknown, true)]
    [InlineData(LaunchAction.Install, true)]
    [InlineData(LaunchAction.Repair, true)]
    [InlineData(LaunchAction.Uninstall, true)]
    [InlineData(LaunchAction.Layout, false)]
    public void BundleOwnerIdIsRequiredOnlyForActionsThatCanExecuteSetup(
        LaunchAction action,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.RequiresBundleOwnerId(action));
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

    private static void DrainFocus(DispatcherObject owner) =>
        owner.Dispatcher.Invoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => { }));

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void AssertUniqueAccessKeys(params FrameworkElement[] elements)
    {
        char[] keys = elements
            .Where(element => element.Visibility == Visibility.Visible)
            .Select(element => element switch
            {
                ContentControl control => control.Content?.ToString(),
                _ => null,
            })
            .Select(GetAccessKey)
            .ToArray();

        Assert.DoesNotContain('\0', keys);
        Assert.Equal(keys.Length, keys.Select(char.ToUpperInvariant).Distinct().Count());
    }

    private static char GetAccessKey(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return '\0';
        }

        for (int index = 0; index < value!.Length - 1; index++)
        {
            if (value[index] == '_' && value[index + 1] != '_')
            {
                return value[index + 1];
            }
        }

        return '\0';
    }

    private static void RunWithUiCulture(string cultureName, Action action)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            RunInSta(action);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
