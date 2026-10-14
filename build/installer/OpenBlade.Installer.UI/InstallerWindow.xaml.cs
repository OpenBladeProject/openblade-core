using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using WixToolset.BootstrapperApplicationApi;

namespace OpenBlade.Installer.UI;

internal partial class InstallerWindow : Window
{
    private const int MaximumDetailLines = 500;
    private readonly OpenBladeBootstrapperApplication bootstrapper;
    private readonly bool allowInteraction;
    private readonly Queue<string> detailLines = new();
    private IInputElement? focusBeforeCancel;
    private string? logPath;

    internal InstallerWindow(
        OpenBladeBootstrapperApplication bootstrapper,
        string version,
        bool allowInteraction = true)
    {
        this.bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
        this.allowInteraction = allowInteraction;
        InitializeComponent();
        InstallerStrings.Apply(this);
        VersionText.Text = InstallerStrings.Format("Version {0}", version);
        SourceInitialized += Window_SourceInitialized;
        Closing += Window_Closing;
        Closed += Window_Closed;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
    }

    internal IntPtr WindowHandle { get; private set; }

    public void ShowReady(bool installed)
    {
        ShowOnly(ReadyPanel);
        HeaderSubtitle.Text = InstallerStrings.Get(installed ? "Maintenance" : "Setup");
        ReadyHeading.Text = InstallerStrings.Get(installed ? "OpenBlade is installed" : "Install OpenBlade");
        ReadyMessage.Text = installed
            ? InstallerStrings.Get("Repair the installed files, or remove OpenBlade from this PC.")
            : InstallerStrings.Get("Install the OpenBlade service, Session Agent, and tray application for this PC.");
        InstallButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        InstallButton.Content = InstallerStrings.Get("_Install");
        RepairButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        PurgeProfilesCheckBox.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        ReadyCancelButton.Content = InstallerStrings.Get("_Cancel");
        QueueFocus(installed ? RepairButton : InstallButton);
    }

    public void ShowSameVersionMaintenance(string installedVersion)
    {
        ShowOnly(ReadyPanel);
        HeaderSubtitle.Text = InstallerStrings.Get("Maintenance");
        ReadyHeading.Text = InstallerStrings.Format("OpenBlade {0} is installed", installedVersion);
        ReadyMessage.Text = InstallerStrings.Get(
            "Repair overwrites the installed files with this installer, or uninstall OpenBlade while keeping saved profiles. To remove saved profiles too, close this installer and uninstall the registered OpenBlade entry from Windows Installed apps.");
        InstallButton.Visibility = Visibility.Collapsed;
        InstallButton.Content = InstallerStrings.Get("_Install");
        RepairButton.Visibility = Visibility.Visible;
        UninstallButton.Visibility = Visibility.Visible;
        PurgeProfilesCheckBox.IsChecked = false;
        PurgeProfilesCheckBox.Visibility = Visibility.Collapsed;
        ReadyCancelButton.Content = InstallerStrings.Get("_Cancel");
        QueueFocus(RepairButton);
    }

    public void ShowNewerVersionBlocked(
        string installedVersion,
        string installerVersion)
    {
        ShowOnly(ReadyPanel);
        HeaderSubtitle.Text = InstallerStrings.Get("Newer version installed");
        ReadyHeading.Text = InstallerStrings.Get("Use the installed OpenBlade version");
        ReadyMessage.Text = InstallerStrings.Format(
            "OpenBlade {0} is installed. This installer contains older version {1}. Setup made no changes. Use version {0} or a newer installer.",
            installedVersion,
            installerVersion);
        InstallButton.Content = InstallerStrings.Get("_Install");
        InstallButton.Visibility = Visibility.Collapsed;
        RepairButton.Visibility = Visibility.Collapsed;
        UninstallButton.Visibility = Visibility.Collapsed;
        PurgeProfilesCheckBox.Visibility = Visibility.Collapsed;
        ReadyCancelButton.Content = InstallerStrings.Get("_Close");
        QueueFocus(ReadyCancelButton);
    }

    public void ShowRegistrationConflict(string message, bool canUninstall)
    {
        ShowOnly(ReadyPanel);
        HeaderSubtitle.Text = InstallerStrings.Get("Installer cleanup required");
        ReadyHeading.Text = InstallerStrings.Get("Resolve existing OpenBlade installers");
        ReadyMessage.Text = message;
        InstallButton.Visibility = Visibility.Collapsed;
        InstallButton.Content = InstallerStrings.Get("_Install");
        RepairButton.Visibility = Visibility.Collapsed;
        UninstallButton.Visibility = canUninstall
            ? Visibility.Visible
            : Visibility.Collapsed;
        PurgeProfilesCheckBox.Visibility = canUninstall
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReadyCancelButton.Content = InstallerStrings.Get("_Cancel");
        QueueFocus(canUninstall ? UninstallButton : ReadyCancelButton);
    }

    public void ShowProgress(LaunchAction action)
    {
        ShowOnly(ProgressPanel);
        HeaderSubtitle.Text = InstallerStrings.Get("Working");
        ProgressHeading.Text = action switch
        {
            LaunchAction.Uninstall => InstallerStrings.Get("Removing OpenBlade"),
            LaunchAction.Repair => InstallerStrings.Get("Repairing OpenBlade"),
            _ => InstallerStrings.Get("Installing OpenBlade"),
        };
        ProgressMessage.Text = InstallerStrings.Get("Preparing…");
        OverallProgress.Value = 0;
        ProgressCancelButton.IsEnabled = true;
        ProgressCancelButton.Visibility = allowInteraction
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailsToggle.Visibility = allowInteraction
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailsBorder.Visibility = Visibility.Collapsed;
        DetailsToggle.IsChecked = false;
        detailLines.Clear();
        DetailsText.Clear();
        QueueFocus(ProgressCancelButton);
    }

    public bool ConfirmDotNetRuntimeInstall() =>
        MessageBox.Show(
            this,
            InstallerStrings.Get(
                "OpenBlade needs Microsoft .NET 10 Desktop Runtime. Setup will install it before installing OpenBlade. Continue?"),
            InstallerStrings.Get("Microsoft .NET 10 Desktop Runtime required"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes) == MessageBoxResult.Yes;

    public void SetProgress(int percentage, string? message = null)
    {
        OverallProgress.Value = Math.Max(0, Math.Min(100, percentage));
        if (!string.IsNullOrWhiteSpace(message))
        {
            ProgressMessage.Text = message;
        }
    }

    public void SetStatus(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            ProgressMessage.Text = message.Trim();
        }
    }

    public void AddDetail(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string line = $"{DateTime.Now:HH:mm:ss}  {message.Trim()}";
        detailLines.Enqueue(line);
        while (detailLines.Count > MaximumDetailLines)
        {
            _ = detailLines.Dequeue();
        }

        DetailsText.Text = string.Join(Environment.NewLine, detailLines);
        DetailsText.ScrollToEnd();
    }

    public void ShowSuccess(
        LaunchAction action,
        bool purgeProfiles,
        UninstallOutcome uninstallOutcome = UninstallOutcome.Removed)
    {
        ShowOnly(SuccessPanel);
        HeaderSubtitle.Text = InstallerStrings.Get("Complete");
        bool canLaunch = action is LaunchAction.Install or LaunchAction.Repair;
        LaunchButton.Visibility = canLaunch ? Visibility.Visible : Visibility.Collapsed;
        if (action == LaunchAction.Uninstall)
        {
            SuccessHeading.Text = uninstallOutcome == UninstallOutcome.Removed
                ? InstallerStrings.Get("OpenBlade was removed")
                : InstallerStrings.Get("Installer entry removed");
            SuccessMessage.Text = uninstallOutcome switch
            {
                UninstallOutcome.Removed when purgeProfiles =>
                    InstallerStrings.Get("Saved profiles and settings were also removed."),
                UninstallOutcome.Removed =>
                    InstallerStrings.Get("Your saved profiles and settings were kept."),
                UninstallOutcome.RegistrationOnly =>
                    InstallerStrings.Get(
                        "This installer entry was removed. Another OpenBlade installer still owns the installation."),
                UninstallOutcome.AlreadyAbsent =>
                    InstallerStrings.Get(
                        "This installer entry was removed. OpenBlade was already absent."),
                _ => throw new InvalidOperationException(),
            };
        }
        else
        {
            SuccessHeading.Text = InstallerStrings.Get("OpenBlade is ready");
            SuccessMessage.Text = InstallerStrings.Get("Setup completed successfully.");
        }

        QueueFocus(canLaunch ? LaunchButton : SuccessCloseButton);
    }

    public void ShowFailure(string message, string? bundleLogPath)
    {
        ShowOnly(FailurePanel);
        HeaderSubtitle.Text = InstallerStrings.Get("Setup failed");
        FailureMessage.Text = string.IsNullOrWhiteSpace(message)
            ? InstallerStrings.Get("See the setup log for more information.")
            : message;
        logPath = bundleLogPath;
        OpenLogButton.IsEnabled = !string.IsNullOrWhiteSpace(logPath);
        QueueFocus(OpenLogButton.IsEnabled ? OpenLogButton : FailureCloseButton);
    }

    public void ShowHelp()
    {
        ShowOnly(SuccessPanel);
        HeaderSubtitle.Text = InstallerStrings.Get("Help");
        LaunchButton.Visibility = Visibility.Collapsed;
        SuccessHeading.Text = InstallerStrings.Get("OpenBlade Setup");
        SuccessMessage.Text = InstallerStrings.Get(
            "Run this installer normally to install, repair, or remove OpenBlade.");
        QueueFocus(SuccessCloseButton);
    }

    public void MarkCancellationRequested()
    {
        CancelConfirmationPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.IsEnabled = true;
        focusBeforeCancel = null;
        ProgressCancelButton.IsEnabled = false;
        ProgressMessage.Text = InstallerStrings.Get("Cancelling and restoring the previous state…");
        AddDetail(InstallerStrings.Get("Cancellation requested; waiting for safe rollback."));
    }

    private void ShowOnly(UIElement panel)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ReadyPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        SuccessPanel.Visibility = Visibility.Collapsed;
        FailurePanel.Visibility = Visibility.Collapsed;
        CancelConfirmationPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.IsEnabled = true;
        focusBeforeCancel = null;
        panel.Visibility = Visibility.Visible;
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e) =>
        bootstrapper.StartAction(LaunchAction.Install, purgeProfiles: false);

    private void RepairButton_Click(object sender, RoutedEventArgs e) =>
        bootstrapper.StartAction(LaunchAction.Repair, purgeProfiles: false);

    private void UninstallButton_Click(object sender, RoutedEventArgs e) =>
        bootstrapper.StartAction(LaunchAction.Uninstall, PurgeProfilesCheckBox.IsChecked == true);

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        focusBeforeCancel = Keyboard.FocusedElement;
        ProgressPanel.IsEnabled = false;
        CancelConfirmationPanel.Visibility = Visibility.Visible;
        QueueFocus(KeepWorkingButton);
    }

    private void KeepWorkingButton_Click(object sender, RoutedEventArgs e)
    {
        CancelConfirmationPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.IsEnabled = true;
        QueueFocus(focusBeforeCancel ?? ProgressCancelButton);
        focusBeforeCancel = null;
    }

    private void ConfirmCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelConfirmationPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.IsEnabled = true;
        focusBeforeCancel = null;
        bootstrapper.RequestCancel();
    }

    private void ReadyCancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void LaunchButton_Click(object sender, RoutedEventArgs e) => bootstrapper.LaunchOpenBlade();

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            bootstrapper.OpenPath(logPath!);
        }
    }

    private void DetailsToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool show = DetailsToggle.IsChecked == true;
        DetailsToggle.Content = InstallerStrings.Get(show ? "Hide _details" : "Show _details");
        DetailsBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void QueueFocus(IInputElement target) =>
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => Keyboard.Focus(target)));

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(new Action(ApplyTheme));
    }

    private void ApplyTheme() => InstallerTheme.Apply(this, InstallerTheme.ShouldUseDarkMode());

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        WindowHandle = new WindowInteropHelper(this).Handle;
        ApplyTheme();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        bootstrapper.Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!bootstrapper.CanClose())
        {
            e.Cancel = true;
        }
    }
}
