using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenBlade.Localization;
using WixToolset.BootstrapperApplicationApi;
using Xunit;

namespace OpenBlade.Installer.UI.Tests;

public sealed class InstallerWindowTests
{
    // WPF retains text-services workers after a UI test's STA exits. Keep one
    // private desktop for this testhost; Windows releases it at process exit.
    // Do not close it between tests while those workers still use it.
    private static readonly Lazy<OpenBlade.Testing.WindowsVisualTestDesktop> VisualDesktop =
        new(() => new OpenBlade.Testing.WindowsVisualTestDesktop());

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

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("ru-RU")]
    public void SupportedCultureSuccessActionsExposeUniqueAccessKeys(string cultureName)
    {
        RunWithUiCulture(cultureName, () =>
        {
            InstallerWindow window = new(new OpenBladeBootstrapperApplication(), "1.2.3");
            window.ShowSuccess(LaunchAction.Install, purgeProfiles: false);

            AssertUniqueAccessKeys(window.LaunchButton, window.SuccessCloseButton);
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

            window.ShowNewerVersionBlocked("2.0.0", "1.2.3");
            Assert.Equal("Use the installed OpenBlade version", window.ReadyHeading.Text);
            Assert.Equal(Visibility.Collapsed, window.InstallButton.Visibility);
            Assert.Contains("2.0.0", window.ReadyMessage.Text);
            Assert.Contains("1.2.3", window.ReadyMessage.Text);
            Assert.Contains("made no changes", window.ReadyMessage.Text);
            Assert.Equal(Visibility.Collapsed, window.RepairButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.UninstallButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.PurgeProfilesCheckBox.Visibility);
            Assert.Equal("_Close", window.ReadyCancelButton.Content);

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
            window.ShowSuccess(
                LaunchAction.Uninstall,
                purgeProfiles: false,
                UninstallOutcome.RegistrationOnly);
            Assert.Equal("Installer entry removed", window.SuccessHeading.Text);
            Assert.Contains("still owns", window.SuccessMessage.Text);
            window.ShowSuccess(
                LaunchAction.Uninstall,
                purgeProfiles: false,
                UninstallOutcome.AlreadyAbsent);
            Assert.Equal("Installer entry removed", window.SuccessHeading.Text);
            Assert.Contains("already absent", window.SuccessMessage.Text);

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
                    window => window.ShowNewerVersionBlocked("0.11.0", "0.10.0"),
                    window => [window.ReadyCancelButton]);
                break;
            case "progress":
                Capture(
                    "installer-progress",
                    window =>
                    {
                        window.ShowProgress(LaunchAction.Install);
                        window.SetProgress(47, InstallerStrings.Get("Installing OpenBlade"));
                        window.AddDetail("Stable diagnostic detail: OpenBlade.Tray.exe");
                        window.DetailsText.Text =
                            "00:00:00  Stable diagnostic detail: OpenBlade.Tray.exe";
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
                try
                {
                    PrepareLocalizationSnapshotWindow(window);
                    window.Show();
                    InstallerTheme.Apply(window, dark: false);
                    showState(window);
                    PumpDispatcher(window.Dispatcher);
                    window.UpdateLayout();
                    Grid visual = Assert.IsType<Grid>(window.Content);
                    const int width = 560;
                    const int height = 560;
                    visual.Measure(new System.Windows.Size(width, height));
                    visual.Arrange(new Rect(0, 0, width, height));
                    visual.UpdateLayout();
                    FrameworkElement[] activeActions = selectActiveActions(window);
                    AssertWithinSnapshot(visual, window.HeaderPanel);
                    AssertWithinSnapshot(visual, window.VersionText);
                    foreach (FrameworkElement action in activeActions)
                    {
                        AssertWithinSnapshot(visual, action);
                    }
                    var background = Assert.IsType<SolidColorBrush>(
                        window.Resources["WindowBrush"]);
                    int stride = width * 4;
                    byte[] pixels = CaptureStableLocalizationSnapshotPixels(
                        visual,
                        background,
                        width,
                        height,
                        name);
                    BitmapSource canonicalBitmap = BitmapSource.Create(
                        width,
                        height,
                        96,
                        96,
                        PixelFormats.Pbgra32,
                        null,
                        pixels,
                        stride);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(canonicalBitmap));
                    using FileStream stream = File.Create(
                        Path.Combine(outputDirectory, name + ".png"));
                    encoder.Save(stream);
                }
                finally
                {
                    window.Close();
                }
            });
        }
    }

    private static void PrepareLocalizationSnapshotWindow(InstallerWindow window)
    {
        window.ShowActivated = false;
        ((UIElement)window.Content).IsHitTestVisible = false;
        OpenBlade.Tray.LocalizationSnapshotWindowInput.Apply(
            new WindowInteropHelper(window).EnsureHandle());
    }

    [Fact]
    public void LocalizationSnapshotRejectsInputWithoutDisablingControls()
    {
        RunWithUiCulture("en-US", () =>
        {
            InstallerWindow window = new(new OpenBladeBootstrapperApplication(), "1.2.3")
            {
                ShowInTaskbar = false,
            };
            try
            {
                window.ShowReady(installed: false);
                var root = Assert.IsType<Grid>(window.Content);
                PrepareLocalizationSnapshotWindow(window);
                IntPtr handle = new WindowInteropHelper(window).Handle;
                window.Show();
                PumpDispatcher(window.Dispatcher);
                Assert.NotEqual(handle, GetForegroundWindow());
                Assert.False(window.IsActive);
                Assert.False(window.ShowActivated);
                Assert.True(window.IsEnabled);
                Assert.True(window.InstallButton.IsEnabled);
                Assert.False(root.IsHitTestVisible);
                Assert.Null(root.InputHitTest(new Point(100, 100)));
                Assert.NotEqual(0L, GetWindowLongPtr(handle, -20).ToInt64() & 0x08000000);
                // Exercise the real HWND subclass without injecting desktop input.
                Assert.Equal(IntPtr.Zero, SendMessage(handle, 0x0084, IntPtr.Zero, IntPtr.Zero));
                Assert.Equal(new IntPtr(4), SendMessage(handle, 0x0021, IntPtr.Zero, IntPtr.Zero));
                Assert.Equal(new IntPtr(window.Title.Length),
                    SendMessage(handle, 0x000E, IntPtr.Zero, IntPtr.Zero)); // WM_GETTEXTLENGTH still delegates.
            }
            finally
            {
                window.Close();
            }
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static byte[] CaptureStableLocalizationSnapshotPixels(
        FrameworkElement visual,
        SolidColorBrush background,
        int width,
        int height,
        string stage)
    {
        byte[] stable = CaptureCanonicalLocalizationSnapshotPixels(
            visual,
            background,
            width,
            height);
        Stopwatch stabilization = Stopwatch.StartNew();
        while (true)
        {
            Thread.Sleep(100);
            PumpDispatcher(visual.Dispatcher);
            visual.UpdateLayout();
            byte[] candidate = CaptureCanonicalLocalizationSnapshotPixels(
                visual,
                background,
                width,
                height);
            if (stable.SequenceEqual(candidate))
            {
                return candidate;
            }

            if (stabilization.Elapsed >= TimeSpan.FromSeconds(10))
            {
                var stabilizationException = new InvalidOperationException(
                    "The installer localization snapshot did not produce two "
                        + "consecutive canonical frames within 10 seconds.");
                bool retained = LocalizationSnapshotFailureEvidence.TryWrite(
                    Environment.GetEnvironmentVariable(
                        LocalizationSnapshotFailureEvidence.DirectoryEnvironmentVariable),
                    "installer",
                    stage,
                    width,
                    height,
                    stable,
                    width,
                    height,
                    candidate);
                if (!retained)
                {
                    Trace.TraceWarning(
                        "OpenBlade installer snapshot failure evidence could not be retained.");
                }
                throw stabilizationException;
            }

            stable = candidate;
        }
    }

    private static byte[] CaptureCanonicalLocalizationSnapshotPixels(
        FrameworkElement visual,
        SolidColorBrush background,
        int width,
        int height)
    {
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        var backgroundVisual = new DrawingVisual();
        using (DrawingContext drawing = backgroundVisual.RenderOpen())
        {
            drawing.DrawRectangle(
                background,
                null,
                new Rect(0, 0, width, height));
        }
        bitmap.Render(backgroundVisual);
        bitmap.Render(visual);
        byte[] topLeftPixel = new byte[4];
        bitmap.CopyPixels(
            new Int32Rect(0, 0, 1, 1),
            topLeftPixel,
            4,
            0);
        Assert.Equal(new byte[] { 0xF7, 0xF7, 0xF7, 0xFF }, topLeftPixel);
        byte[] pixels = new byte[checked(width * height * 4)];
        bitmap.CopyPixels(pixels, width * 4, 0);
        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            pixels,
            width,
            height);
        return pixels;
    }

    [Fact]
    public void LocalizationSnapshotCanonicalizationRepairsOnlyAdjacentOutliers()
    {
        const int width = 3;
        const int height = 3;
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 231;
            pixels[index + 1] = 231;
            pixels[index + 2] = 231;
            pixels[index + 3] = 255;
        }

        int center = ((1 * width) + 1) * 4;
        pixels[center] = 215;
        pixels[center + 1] = 215;
        pixels[center + 2] = 215;

        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            pixels,
            width,
            height);

        Assert.Equal(231, pixels[center]);
        Assert.Equal(231, pixels[center + 1]);
        Assert.Equal(231, pixels[center + 2]);
        Assert.Equal(255, pixels[center + 3]);

        pixels[center] = 199;
        pixels[center + 1] = 199;
        pixels[center + 2] = 199;
        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            pixels,
            width,
            height);
        Assert.Equal(199, pixels[center]);
        Assert.Equal(199, pixels[center + 1]);
        Assert.Equal(199, pixels[center + 2]);
        Assert.Equal(255, pixels[center + 3]);
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
            string dotNetRuntime = OpenBladeBootstrapperApplication.DescribePackageExecutionStatus(
                "OpenBladeDotNetRuntime",
                relatedVersion: null);
            string setup = OpenBladeBootstrapperApplication.DescribePackageExecutionStatus(
                "OpenBladeSetup",
                relatedVersion: null);
            string related = OpenBladeBootstrapperApplication.DescribePackageExecutionStatus(
                "{RELATED-BUNDLE}",
                "1.2.3");

            Assert.StartsWith("[!! ", prerequisite, StringComparison.Ordinal);
            Assert.StartsWith("[!! ", dotNetRuntime, StringComparison.Ordinal);
            Assert.StartsWith("[!! ", setup, StringComparison.Ordinal);
            Assert.StartsWith("[!! ", related, StringComparison.Ordinal);
            Assert.Contains("1.2.3", related, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(null, "10.0.12", false)]
    [InlineData("", "10.0.12", false)]
    [InlineData("10.0.11", "10.0.12", false)]
    [InlineData("10.0.12", "10.0.12", true)]
    [InlineData("10.0.13", "10.0.12", true)]
    [InlineData("10.1.0", "10.0.12", false)]
    [InlineData("10.0.13-preview.1", "10.0.12", false)]
    [InlineData("11.0.0", "10.0.12", false)]
    [InlineData("invalid", "10.0.12", false)]
    [InlineData("10.0.12", null, false)]
    public void DotNetDesktopRuntimeGateRequiresTheConfiguredMinimum(
        string? installedVersion,
        string? minimumVersion,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.IsDotNetDesktopRuntimeSufficient(
                installedVersion,
                minimumVersion));
    }

    [Theory]
    [InlineData(false, 0L)]
    [InlineData(true, 1L)]
    public void DotNetRuntimeReadinessMapsToBurnNumericVariable(
        bool runtimeSufficient,
        long expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.DotNetRuntimeSufficientVariableValue(
                runtimeSufficient));
    }

    [Theory]
    [InlineData("OpenBladeVcRuntime", false)]
    [InlineData("OpenBladeDotNetRuntime", true)]
    [InlineData("OpenBladeSetup", false)]
    public void DotNetRuntimeReadinessIsInitializedAtItsPackageDetectionBoundary(
        string packageId,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.ShouldInitializeDotNetRuntimeReadiness(
                packageId));
    }

    [Theory]
    [InlineData(LaunchAction.Install, Display.Full, false, true)]
    [InlineData(LaunchAction.Repair, Display.Full, false, true)]
    [InlineData(LaunchAction.Uninstall, Display.Full, false, false)]
    [InlineData(LaunchAction.Install, Display.None, false, false)]
    [InlineData(LaunchAction.Install, Display.Passive, false, false)]
    [InlineData(LaunchAction.Install, Display.Full, true, false)]
    public void DotNetRuntimePromptIsLimitedToInteractiveInstallAndRepair(
        LaunchAction action,
        Display display,
        bool runtimeSufficient,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.ShouldPromptForDotNetRuntime(
                action,
                display,
                runtimeSufficient));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void DeclinedRequiredRuntimeStopsBeforePlanning(
        bool promptRequired,
        bool consentGranted,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.ShouldProceedAfterDotNetRuntimePrompt(
                promptRequired,
                consentGranted));
    }

    [Theory]
    [InlineData("zh-CN", "正在安装 Microsoft .NET 10 桌面运行时…")]
    [InlineData("ru-RU", "Установка среды выполнения Microsoft .NET 10 Desktop…")]
    public void DotNetRuntimeProgressIsLocalized(string cultureName, string expected)
    {
        RunWithUiCulture(cultureName, () =>
        {
            Assert.Equal(
                expected,
                OpenBladeBootstrapperApplication.DescribePackageExecutionStatus(
                    "OpenBladeDotNetRuntime",
                    relatedVersion: null));
            Assert.NotEqual(
                "Microsoft .NET 10 Desktop Runtime required",
                InstallerStrings.Get("Microsoft .NET 10 Desktop Runtime required"));
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
    [InlineData(false, false, false, RelatedBundlePlanType.Downgrade, RelatedBundlePlanType.Downgrade)]
    [InlineData(false, false, true, RelatedBundlePlanType.Upgrade, RelatedBundlePlanType.None)]
    [InlineData(true, false, true, RelatedBundlePlanType.Upgrade, RelatedBundlePlanType.Upgrade)]
    [InlineData(true, true, true, RelatedBundlePlanType.Upgrade, RelatedBundlePlanType.None)]
    public void RelatedBundlePlanProtectsVersionBoundaries(
        bool allowSameVersionMaintenance,
        bool relatedBundleInvocation,
        bool detectedSameVersionBundle,
        RelatedBundlePlanType recommendedType,
        RelatedBundlePlanType expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.SelectRelatedBundlePlanType(
                allowSameVersionMaintenance,
                relatedBundleInvocation,
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
    [InlineData("0.19.22+abcdef|bundle:01234567-89ab-cdef-0123-456789abcdef", true)]
    [InlineData("0.19.22+abcdef", false)]
    [InlineData("0.19.22+abcdef|bundle:{01234567-89ab-cdef-0123-456789abcdef}", false)]
    [InlineData("0.19.22+abcdef|bundle:not-a-guid", false)]
    [InlineData("0.19.22|bundle:01234567-89ab-cdef-0123-456789abcdef|bundle:01234567-89ab-cdef-0123-456789abcdef", false)]
    [InlineData(null, false)]
    public void OwnershipMarkerRequiresOneCanonicalBundleOwnerSuffix(
        string? installedBuild,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsUninstallPostconditionProbe.TryReadBundleOwnerId(
                installedBuild,
                out _));
    }

    [Theory]
    [InlineData("WiX Burn", "0.19.22", "0.19.22+abcdef|bundle:01234567-89ab-cdef-0123-456789abcdef", true)]
    [InlineData(null, "0.19.22", "0.19.22+abcdef|bundle:01234567-89ab-cdef-0123-456789abcdef", false)]
    [InlineData(" ", "0.19.22", "0.19.22+abcdef|bundle:01234567-89ab-cdef-0123-456789abcdef", false)]
    [InlineData("WiX Burn", null, "0.19.22+abcdef|bundle:01234567-89ab-cdef-0123-456789abcdef", false)]
    [InlineData("WiX Burn", " ", "0.19.22+abcdef|bundle:01234567-89ab-cdef-0123-456789abcdef", false)]
    public void OwnershipMarkerRequiresEveryAuthoritativeValue(
        string? owner,
        string? installedVersion,
        string installedBuild,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsUninstallPostconditionProbe.TryReadBundleOwnerId(
                owner,
                installedVersion,
                installedBuild,
                out _));
    }

    [Fact]
    public void OwnershipMarkerRejectsOversizedAuthoritativeValues()
    {
        string oversized = new('x', 513);
        const string installedBuild =
            "0.19.22+abcdef|bundle:01234567-89ab-cdef-0123-456789abcdef";

        Assert.False(WindowsUninstallPostconditionProbe.TryReadBundleOwnerId(
            oversized,
            "0.19.22",
            installedBuild,
            out _));
        Assert.False(WindowsUninstallPostconditionProbe.TryReadBundleOwnerId(
            "WiX Burn",
            oversized,
            installedBuild,
            out _));
    }

    [Theory]
    [InlineData(LaunchAction.Uninstall, true, false, true)]
    [InlineData(LaunchAction.Uninstall, true, true, false)]
    [InlineData(LaunchAction.Uninstall, false, false, false)]
    [InlineData(LaunchAction.Install, true, false, false)]
    public void UninstallPlanRejectsRegisteredNoPackagePlan(
        LaunchAction action,
        bool runningBundleInstalled,
        bool setupPackagePlanned,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.ShouldRejectUninstallPlan(
                action,
                runningBundleInstalled,
                setupPackagePlanned));
    }

    [Fact]
    public void UninstallPostconditionRequiresOwnedPayloadAndRegistrationRemoval()
    {
        const string owner = "01234567-89ab-cdef-0123-456789abcdef";
        var before = new UninstallMachineState(
            BundleOwnershipKind.Current,
            owner,
            installDirectoryExists: true,
            serviceRegistrationExists: true);
        var absent = new UninstallMachineState(
            BundleOwnershipKind.Absent,
            bundleOwnerId: null,
            installDirectoryExists: false,
            serviceRegistrationExists: false);
        var filesRemain = new UninstallMachineState(
            BundleOwnershipKind.Absent,
            bundleOwnerId: null,
            installDirectoryExists: true,
            serviceRegistrationExists: false);
        var serviceRemains = new UninstallMachineState(
            BundleOwnershipKind.Absent,
            bundleOwnerId: null,
            installDirectoryExists: false,
            serviceRegistrationExists: true);
        var ownershipRemains = new UninstallMachineState(
            BundleOwnershipKind.Current,
            owner,
            installDirectoryExists: false,
            serviceRegistrationExists: false);

        Assert.Equal(
            UninstallOutcome.Removed,
            UninstallPostconditionClassifier.Classify(
                before,
                absent,
                runningBundleInstalled: true,
                sameVersionMaintenance: false,
                setupPackagePlanned: true,
                registrationAfterApply: RegistrationType.None));
        Assert.Equal(
            UninstallOutcome.Failed,
            UninstallPostconditionClassifier.Classify(
                before,
                filesRemain,
                runningBundleInstalled: true,
                sameVersionMaintenance: false,
                setupPackagePlanned: true,
                registrationAfterApply: RegistrationType.None));
        Assert.Equal(
            UninstallOutcome.Failed,
            UninstallPostconditionClassifier.Classify(
                before,
                serviceRemains,
                runningBundleInstalled: true,
                sameVersionMaintenance: false,
                setupPackagePlanned: true,
                registrationAfterApply: RegistrationType.None));
        Assert.Equal(
            UninstallOutcome.Failed,
            UninstallPostconditionClassifier.Classify(
                before,
                ownershipRemains,
                runningBundleInstalled: true,
                sameVersionMaintenance: false,
                setupPackagePlanned: true,
                registrationAfterApply: RegistrationType.None));
        Assert.Equal(
            UninstallOutcome.Failed,
            UninstallPostconditionClassifier.Classify(
                before,
                absent,
                runningBundleInstalled: true,
                sameVersionMaintenance: false,
                setupPackagePlanned: true,
                registrationAfterApply: RegistrationType.Full));
    }

    [Fact]
    public void SupersededBundleReportsRegistrationOnlyForTheSameRemainingOwner()
    {
        const string otherOwner = "fedcba98-7654-3210-fedc-ba9876543210";
        var otherBefore = new UninstallMachineState(
            BundleOwnershipKind.Other,
            otherOwner,
            installDirectoryExists: true,
            serviceRegistrationExists: true);
        var sameOwnerAfter = new UninstallMachineState(
            BundleOwnershipKind.Other,
            otherOwner,
            installDirectoryExists: true,
            serviceRegistrationExists: true);
        var changedOwnerAfter = new UninstallMachineState(
            BundleOwnershipKind.Other,
            "01234567-89ab-cdef-0123-456789abcdef",
            installDirectoryExists: true,
            serviceRegistrationExists: true);
        var markerOnlyAfter = new UninstallMachineState(
            BundleOwnershipKind.Other,
            otherOwner,
            installDirectoryExists: false,
            serviceRegistrationExists: false);
        var directoryRemovedAfter = new UninstallMachineState(
            BundleOwnershipKind.Other,
            otherOwner,
            installDirectoryExists: false,
            serviceRegistrationExists: true);
        var serviceRemovedAfter = new UninstallMachineState(
            BundleOwnershipKind.Other,
            otherOwner,
            installDirectoryExists: true,
            serviceRegistrationExists: false);

        Assert.Equal(
            UninstallOutcome.RegistrationOnly,
            UninstallPostconditionClassifier.Classify(
                otherBefore,
                sameOwnerAfter,
                runningBundleInstalled: true,
                sameVersionMaintenance: false,
                setupPackagePlanned: true,
                registrationAfterApply: RegistrationType.None));
        Assert.Equal(
            UninstallOutcome.Failed,
            UninstallPostconditionClassifier.Classify(
                otherBefore,
                directoryRemovedAfter,
                runningBundleInstalled: true,
                sameVersionMaintenance: false,
                setupPackagePlanned: true,
                registrationAfterApply: RegistrationType.None));
        Assert.Equal(
            UninstallOutcome.Failed,
            UninstallPostconditionClassifier.Classify(
                otherBefore,
                serviceRemovedAfter,
                runningBundleInstalled: true,
                sameVersionMaintenance: false,
                setupPackagePlanned: true,
                registrationAfterApply: RegistrationType.None));
        Assert.Equal(
            UninstallOutcome.Failed,
            UninstallPostconditionClassifier.Classify(
                otherBefore,
                markerOnlyAfter,
                runningBundleInstalled: true,
                sameVersionMaintenance: false,
                setupPackagePlanned: true,
                registrationAfterApply: RegistrationType.None));
        Assert.Equal(
            UninstallOutcome.Failed,
            UninstallPostconditionClassifier.Classify(
                otherBefore,
                sameOwnerAfter,
                runningBundleInstalled: true,
                sameVersionMaintenance: false,
                setupPackagePlanned: false,
                registrationAfterApply: null));
        Assert.Equal(
            UninstallOutcome.Failed,
            UninstallPostconditionClassifier.Classify(
                otherBefore,
                changedOwnerAfter,
                runningBundleInstalled: true,
                sameVersionMaintenance: false,
                setupPackagePlanned: true,
                registrationAfterApply: RegistrationType.None));
    }

    [Fact]
    public void AbsentPayloadAndSameVersionMaintenanceHaveDistinctOutcomes()
    {
        const string otherOwner = "fedcba98-7654-3210-fedc-ba9876543210";
        var absent = new UninstallMachineState(
            BundleOwnershipKind.Absent,
            bundleOwnerId: null,
            installDirectoryExists: false,
            serviceRegistrationExists: false);
        var otherOwnerState = new UninstallMachineState(
            BundleOwnershipKind.Other,
            otherOwner,
            installDirectoryExists: true,
            serviceRegistrationExists: true);

        Assert.Equal(
            UninstallOutcome.AlreadyAbsent,
            UninstallPostconditionClassifier.Classify(
                absent,
                absent,
                runningBundleInstalled: true,
                sameVersionMaintenance: false,
                setupPackagePlanned: true,
                registrationAfterApply: RegistrationType.None));
        Assert.Equal(
            UninstallOutcome.Removed,
            UninstallPostconditionClassifier.Classify(
                otherOwnerState,
                absent,
                runningBundleInstalled: false,
                sameVersionMaintenance: true,
                setupPackagePlanned: true,
                registrationAfterApply: null));
        Assert.Equal(
            UninstallOutcome.Failed,
            UninstallPostconditionClassifier.Classify(
                otherOwnerState,
                otherOwnerState,
                runningBundleInstalled: false,
                sameVersionMaintenance: true,
                setupPackagePlanned: true,
                registrationAfterApply: null));
        Assert.Equal(
            UninstallOutcome.Failed,
            UninstallPostconditionClassifier.Classify(
                otherOwnerState,
                absent,
                runningBundleInstalled: false,
                sameVersionMaintenance: true,
                setupPackagePlanned: false,
                registrationAfterApply: null));
    }

    [Fact]
    public void FailedUninstallPostconditionProducesANonzeroQuietExitCode()
    {
        int status = OpenBladeBootstrapperApplication.SelectApplyCompletionStatus(
            burnStatus: 0,
            action: LaunchAction.Uninstall,
            uninstallOutcome: UninstallOutcome.Failed);

        Assert.True(status < 0);
        Assert.Equal(1610, OpenBladeBootstrapperApplication.NormalizeExitCode(status));
        Assert.Equal(
            unchecked((int)0x80004005),
            OpenBladeBootstrapperApplication.SelectApplyCompletionStatus(
                burnStatus: unchecked((int)0x80004005),
                action: LaunchAction.Uninstall,
                uninstallOutcome: UninstallOutcome.Failed));
    }

    [Theory]
    [InlineData(true, true, "choose Repair or Uninstall")]
    [InlineData(false, false, "cached installer is unavailable")]
    [InlineData(false, true, "Windows Installed apps")]
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

    [Fact]
    public void StableToLocalDevUpgradeRetainsTheSingleRegistrationPath()
    {
        DetectedRelatedBundle[] relatedBundles =
        [
            new(
                "{54764E2D-D6B4-47A7-A6D6-A1EA6721640A}",
                RelationType.Upgrade,
                perMachine: true,
                "0.19.18",
                missingFromCache: false),
        ];

        Assert.False(
            OpenBladeBootstrapperApplication.ShouldBlockMultipleRelatedBundles(
                LaunchAction.Install,
                Display.None,
                RelationType.None,
                "65535.416.19310.42763",
                relatedBundles,
                localDevCompatibilityEnabled: true));
    }

    [Fact]
    public void LocalDevToLocalDevUpgradeAllowsOnlyTheObservedCompatibilityPair()
    {
        DetectedRelatedBundle[] relatedBundles =
        [
            new(
                "{54764E2D-D6B4-47A7-A6D6-A1EA6721640A}",
                RelationType.Upgrade,
                perMachine: true,
                "0.19.18",
                missingFromCache: false),
            new(
                "{9364476F-3106-4A3B-83DD-6F5D55ED22C0}",
                RelationType.Upgrade,
                perMachine: true,
                "65535.416.19310.42763",
                missingFromCache: false),
        ];

        Assert.True(
            OpenBladeBootstrapperApplication.IsAllowedLocalDevCompatibilityTransition(
                LaunchAction.Install,
                Display.None,
                RelationType.None,
                "65535.416.19383.5543",
                relatedBundles));
        Assert.False(
            OpenBladeBootstrapperApplication.ShouldBlockMultipleRelatedBundles(
                LaunchAction.Install,
                Display.None,
                RelationType.None,
                "65535.416.19383.5543",
                relatedBundles,
                localDevCompatibilityEnabled: true));
        Assert.True(
            OpenBladeBootstrapperApplication.ShouldBlockMultipleRelatedBundles(
                LaunchAction.Install,
                Display.None,
                RelationType.None,
                "65535.416.19383.5543",
                relatedBundles,
                localDevCompatibilityEnabled: false));
    }

    [Theory]
    [InlineData(false, LaunchAction.Install, Display.None, RelationType.None, "65535.416.19383.5543", true)]
    [InlineData(true, LaunchAction.Repair, Display.None, RelationType.None, "65535.416.19383.5543", true)]
    [InlineData(true, LaunchAction.Install, Display.Full, RelationType.None, "65535.416.19383.5543", true)]
    [InlineData(true, LaunchAction.Install, Display.Passive, RelationType.None, "65535.416.19383.5543", true)]
    [InlineData(true, LaunchAction.Install, Display.None, RelationType.Upgrade, "65535.416.19383.5543", true)]
    [InlineData(true, LaunchAction.Install, Display.None, RelationType.None, "0.19.19", true)]
    [InlineData(true, LaunchAction.Install, Display.None, RelationType.None, "0.19.19.7", true)]
    [InlineData(true, LaunchAction.Install, Display.None, RelationType.None, "65535.416.19383", true)]
    [InlineData(true, LaunchAction.Uninstall, Display.None, RelationType.None, "65535.416.19383.5543", false)]
    public void LocalDevCompatibilityTransitionHonorsBuildAndCommandBoundaries(
        bool localDevCompatibilityEnabled,
        LaunchAction action,
        Display display,
        RelationType commandRelation,
        string currentVersion,
        bool expectedBlocked)
    {
        DetectedRelatedBundle[] relatedBundles =
        [
            new(
                "{54764E2D-D6B4-47A7-A6D6-A1EA6721640A}",
                RelationType.Upgrade,
                perMachine: true,
                "0.19.18",
                missingFromCache: false),
            new(
                "{9364476F-3106-4A3B-83DD-6F5D55ED22C0}",
                RelationType.Upgrade,
                perMachine: true,
                "65535.416.19310.42763",
                missingFromCache: false),
        ];

        Assert.Equal(
            expectedBlocked,
            OpenBladeBootstrapperApplication.ShouldBlockMultipleRelatedBundles(
                action,
                display,
                commandRelation,
                currentVersion,
                relatedBundles,
                localDevCompatibilityEnabled));
    }

    [Fact]
    public void LocalDevCompatibilityTransitionRejectsEveryOtherMultipleRegistrationShape()
    {
        const string stableCode = "{54764E2D-D6B4-47A7-A6D6-A1EA6721640A}";
        const string localDevCode = "{9364476F-3106-4A3B-83DD-6F5D55ED22C0}";
        const string secondLocalDevCode = "{A1641A82-FE1F-4BEE-9D81-1CC55A5ADCF1}";
        const string currentVersion = "65535.416.19383.5543";

        static DetectedRelatedBundle Related(
            string code,
            string version,
            bool perMachine = true,
            bool missingFromCache = false,
            RelationType relationType = RelationType.Upgrade) =>
            new(code, relationType, perMachine, version, missingFromCache);

        (string Name, DetectedRelatedBundle[] Bundles)[] rejectedShapes =
        [
            ("missing Stable cache", [Related(stableCode, "0.19.18", missingFromCache: true), Related(localDevCode, "65535.416.19310.42763")]),
            ("missing LocalDev cache", [Related(stableCode, "0.19.18"), Related(localDevCode, "65535.416.19310.42763", missingFromCache: true)]),
            ("per-user Stable", [Related(stableCode, "0.19.18", perMachine: false), Related(localDevCode, "65535.416.19310.42763")]),
            ("per-user LocalDev", [Related(stableCode, "0.19.18"), Related(localDevCode, "65535.416.19310.42763", perMachine: false)]),
            ("non-upgrade relation", [Related(stableCode, "0.19.18"), Related(localDevCode, "65535.416.19310.42763", relationType: RelationType.Addon)]),
            ("wrong Stable code", [Related("{F60B037A-3E19-419B-AE9C-E3A25C367439}", "0.19.18"), Related(localDevCode, "65535.416.19310.42763")]),
            ("wrong Stable version", [Related(stableCode, "0.19.17"), Related(localDevCode, "65535.416.19310.42763")]),
            ("Stable plus Nightly", [Related(stableCode, "0.19.18"), Related(localDevCode, "0.19.18.7")]),
            ("two Stable versions", [Related(stableCode, "0.19.18"), Related(secondLocalDevCode, "0.19.17")]),
            ("two LocalDev versions", [Related(localDevCode, "65535.416.19310.42763"), Related(secondLocalDevCode, "65535.416.19311.1")]),
            ("equal LocalDev", [Related(stableCode, "0.19.18"), Related(localDevCode, currentVersion)]),
            ("newer LocalDev", [Related(stableCode, "0.19.18"), Related(localDevCode, "65535.416.19384.1")]),
            ("three registrations", [Related(stableCode, "0.19.18"), Related(localDevCode, "65535.416.19310.42763"), Related(secondLocalDevCode, "65535.416.19311.1")]),
            ("duplicate Stable detection", [Related(stableCode, "0.19.18"), Related(stableCode, "0.19.18"), Related(localDevCode, "65535.416.19310.42763")]),
            ("malformed LocalDev", [Related(stableCode, "0.19.18"), Related(localDevCode, "65535.bad.version")]),
            ("three-part LocalDev", [Related(stableCode, "0.19.18"), Related(localDevCode, "65535.416.19310")]),
            ("noncanonical LocalDev", [Related(stableCode, "0.19.18"), Related(localDevCode, "65535.0416.19310.42763")]),
            ("invalid bundle code", [Related(stableCode, "0.19.18"), Related("not-a-guid", "65535.416.19310.42763")]),
            ("duplicate bundle code", [Related(stableCode, "0.19.18"), Related(stableCode, "65535.416.19310.42763")]),
        ];

        foreach ((string name, DetectedRelatedBundle[] bundles) in rejectedShapes)
        {
            Assert.True(
                OpenBladeBootstrapperApplication.ShouldBlockMultipleRelatedBundles(
                    LaunchAction.Install,
                    Display.None,
                    RelationType.None,
                    currentVersion,
                    bundles,
                    localDevCompatibilityEnabled: true),
                name);
        }
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
    [InlineData(false, LaunchAction.Install, false)]
    [InlineData(true, LaunchAction.Install, true)]
    [InlineData(true, LaunchAction.Repair, true)]
    [InlineData(true, LaunchAction.Uninstall, false)]
    [InlineData(true, LaunchAction.Layout, false)]
    [InlineData(true, LaunchAction.Unknown, false)]
    public void NewerVersionGateBlocksOnlyMutatingReplacementActions(
        bool newerVersionDetected,
        LaunchAction action,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenBladeBootstrapperApplication.ShouldRejectNewerVersionAction(
                newerVersionDetected,
                action));
    }

    [Fact]
    public void ProductVersionConflictRemainsAProcessFailure()
    {
        const int productVersionConflict = unchecked((int)0x80070666);
        Assert.Equal(
            1638,
            OpenBladeBootstrapperApplication.NormalizeExitCode(productVersionConflict));
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
    public void SameVersionMaintenanceAloneOverridesTheSetupPackageState()
    {
        Assert.Equal(
            RequestState.Present,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                sameVersionMaintenance: false,
                LaunchAction.Install,
                "OpenBladeSetup",
                RequestState.Present));
        Assert.Equal(
            RequestState.Present,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                sameVersionMaintenance: false,
                LaunchAction.Repair,
                "OpenBladeSetup",
                RequestState.Present));
        Assert.Equal(
            RequestState.Present,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                sameVersionMaintenance: false,
                LaunchAction.Install,
                "OpenBladeVcRuntime",
                RequestState.Present));
        Assert.Equal(
            RequestState.Present,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                sameVersionMaintenance: true,
                LaunchAction.Repair,
                "OpenBladeDotNetRuntime",
                RequestState.Present));
        Assert.Equal(
            RequestState.ForcePresent,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
                sameVersionMaintenance: true,
                LaunchAction.Repair,
                "OpenBladeSetup",
                RequestState.Repair));
        Assert.Equal(
            RequestState.Absent,
            OpenBladeBootstrapperApplication.SelectPackageRequestState(
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

    [Fact]
    public void VisualTestWindowBelongsToThePrivateTesthostDesktop()
    {
        RunInSta(() =>
        {
            InstallerWindow window = new(new OpenBladeBootstrapperApplication(), "1.2.3");
            try
            {
                IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
                uint threadId = GetWindowThreadProcessId(handle, out _);
                Assert.NotEqual(0U, threadId);
                var name = new StringBuilder(256);
                Assert.True(GetUserObjectInformationW(
                    GetThreadDesktop(threadId), 2, name, name.Capacity * sizeof(char), out _));
                Assert.Equal(VisualDesktop.Value.Name, name.ToString());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(
        IntPtr handle, int index, StringBuilder information, int length, out int needed);

    private static void RunInSta(Action action)
    {
        OpenBlade.Testing.WindowsVisualTestDesktop desktop = VisualDesktop.Value;
        ExceptionDispatchInfo? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                desktop.AttachCurrentThread();
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
