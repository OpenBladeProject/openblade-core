using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using WixToolset.BootstrapperApplicationApi;

namespace OpenBlade.Installer.UI;

internal partial class InstallerWindow : Window
{
    private const int MaximumDetailLines = 500;
    private readonly OpenBladeBootstrapperApplication bootstrapper;
    private readonly bool allowInteraction;
    private readonly Queue<string> detailLines = new();
    private bool overwriteNewerVersion;
    private bool readyInstalled;
    private string? logPath;

    internal InstallerWindow(
        OpenBladeBootstrapperApplication bootstrapper,
        string version,
        bool allowInteraction = true)
    {
        this.bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
        this.allowInteraction = allowInteraction;
        InitializeComponent();
        VersionText.Text = $"Version {version}";
        SourceInitialized += Window_SourceInitialized;
        Closing += Window_Closing;
        Closed += Window_Closed;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
    }

    internal IntPtr WindowHandle { get; private set; }

    public void ShowReady(bool installed)
    {
        ShowOnly(ReadyPanel);
        overwriteNewerVersion = false;
        readyInstalled = installed;
        HeaderSubtitle.Text = installed ? "Maintenance" : "Setup";
        ReadyHeading.Text = installed ? "OpenBlade is installed" : "Install OpenBlade";
        ReadyMessage.Text = installed
            ? "Repair the installed files, or remove OpenBlade from this PC."
            : "Install the OpenBlade service, Session Agent, and tray application for this PC.";
        InstallButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        InstallButton.Content = "Install";
        RepairButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        PurgeProfilesCheckBox.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowNewerVersionConfirmation(
        string installedVersion,
        string installerVersion,
        bool returnToInstalledState)
    {
        ShowOnly(ReadyPanel);
        overwriteNewerVersion = true;
        readyInstalled = returnToInstalledState;
        HeaderSubtitle.Text = "Newer version installed";
        ReadyHeading.Text = "Replace the newer OpenBlade version?";
        ReadyMessage.Text =
            $"OpenBlade {installedVersion} is installed. This installer contains "
            + $"version {installerVersion}. Do you want to overwrite the newer version?";
        InstallButton.Content = "Overwrite";
        InstallButton.Visibility = Visibility.Visible;
        RepairButton.Visibility = Visibility.Collapsed;
        UninstallButton.Visibility = Visibility.Collapsed;
        PurgeProfilesCheckBox.Visibility = Visibility.Collapsed;
    }

    public void ShowProgress(LaunchAction action)
    {
        ShowOnly(ProgressPanel);
        HeaderSubtitle.Text = "Working";
        ProgressHeading.Text = action switch
        {
            LaunchAction.Uninstall => "Removing OpenBlade",
            LaunchAction.Repair => "Repairing OpenBlade",
            _ => "Installing OpenBlade",
        };
        ProgressMessage.Text = "Preparing…";
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
    }

    public void SetProgress(int percentage, string? message = null)
    {
        OverallProgress.Value = Math.Max(0, Math.Min(100, percentage));
        if (!string.IsNullOrWhiteSpace(message))
        {
            ProgressMessage.Text = message;
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
        ProgressMessage.Text = message.Trim();
    }

    public void ShowSuccess(LaunchAction action)
    {
        ShowOnly(SuccessPanel);
        HeaderSubtitle.Text = "Complete";
        bool canLaunch = action is LaunchAction.Install or LaunchAction.Repair;
        LaunchButton.Visibility = canLaunch ? Visibility.Visible : Visibility.Collapsed;
        SuccessHeading.Text = action == LaunchAction.Uninstall
            ? "OpenBlade was removed"
            : "OpenBlade is ready";
        SuccessMessage.Text = action == LaunchAction.Uninstall
            ? "Your saved profiles were handled according to your selection."
            : "Setup completed successfully.";
    }

    public void ShowFailure(string message, string? bundleLogPath)
    {
        ShowOnly(FailurePanel);
        HeaderSubtitle.Text = "Setup failed";
        FailureMessage.Text = string.IsNullOrWhiteSpace(message)
            ? "See the setup log for more information."
            : message;
        logPath = bundleLogPath;
        OpenLogButton.IsEnabled = !string.IsNullOrWhiteSpace(logPath);
    }

    public void ShowHelp()
    {
        ShowOnly(SuccessPanel);
        HeaderSubtitle.Text = "Help";
        LaunchButton.Visibility = Visibility.Collapsed;
        SuccessHeading.Text = "OpenBlade Setup";
        SuccessMessage.Text =
            "Run this installer normally to install, repair, or remove OpenBlade.";
    }

    public void MarkCancellationRequested()
    {
        CancelConfirmationPanel.Visibility = Visibility.Collapsed;
        ProgressCancelButton.IsEnabled = false;
        ProgressMessage.Text = "Cancelling and restoring the previous state…";
        AddDetail("Cancellation requested; waiting for safe rollback.");
    }

    private void ShowOnly(UIElement panel)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ReadyPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        SuccessPanel.Visibility = Visibility.Collapsed;
        FailurePanel.Visibility = Visibility.Collapsed;
        CancelConfirmationPanel.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e) =>
        bootstrapper.StartAction(
            LaunchAction.Install,
            purgeProfiles: false,
            overwriteNewerVersion);

    private void RepairButton_Click(object sender, RoutedEventArgs e) =>
        bootstrapper.StartAction(LaunchAction.Repair, purgeProfiles: false);

    private void UninstallButton_Click(object sender, RoutedEventArgs e) =>
        bootstrapper.StartAction(LaunchAction.Uninstall, PurgeProfilesCheckBox.IsChecked == true);

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelConfirmationPanel.Visibility = Visibility.Visible;
    }

    private void KeepWorkingButton_Click(object sender, RoutedEventArgs e) =>
        CancelConfirmationPanel.Visibility = Visibility.Collapsed;

    private void ConfirmCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelConfirmationPanel.Visibility = Visibility.Collapsed;
        bootstrapper.RequestCancel();
    }

    private void ReadyCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (overwriteNewerVersion)
        {
            ShowReady(readyInstalled);
        }
        else
        {
            Close();
        }
    }

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
        DetailsToggle.Content = show ? "Hide details" : "Show details";
        DetailsBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

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
