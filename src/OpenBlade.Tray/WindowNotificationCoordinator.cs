using System.Diagnostics;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

internal readonly record struct WindowNotificationObservation(
    PowerSource PowerSource,
    string ControllerAdvisoryText,
    string OperationFailure);

/// <summary>
/// Owns notification baselines and delivered update keys on the caller's UI thread.
/// Delivery and persistence remain caller-owned; no callbacks or background work are scheduled.
/// </summary>
internal sealed class WindowNotificationCoordinator
{
    private const string AppUpdateNotificationTag = "app-update";
    private const string FirmwareUpdateNotificationTag = "firmware-update";
    private const string ProfileNotificationTag = "profile-power-source";
    private const string ControllerAdvisoryNotificationTag = "controller-advisory";
    private const string OperationFailureNotificationTag = "operation-failure";
    private readonly Func<TrayNotification, bool> showNotification;
    private bool notificationObservationReady;
    private PowerSource lastObservedPowerSource = PowerSource.ExternalUnknown;
    private string lastObservedControllerAdvisory = string.Empty;
    private string lastObservedOperationFailure = string.Empty;

    internal WindowNotificationCoordinator(Func<TrayNotification, bool> showNotification)
    {
        this.showNotification = showNotification;
    }

    internal string? LastNotifiedReleaseTag { get; private set; }

    internal string? LastNotifiedFirmwareSignature { get; private set; }

    internal bool ObservationReady => notificationObservationReady;

    internal void RestoreDeliveredUpdates(string? releaseTag, string? firmwareSignature)
    {
        LastNotifiedReleaseTag = releaseTag;
        LastNotifiedFirmwareSignature = firmwareSignature;
    }

    internal bool OnAppUpdateAvailable(
        AppUpdateAvailableEventArgs args,
        NotificationPreferences preferences)
    {
        if (!preferences.IsEnabled(NotificationCategory.AppUpdates)
            || string.Equals(
                LastNotifiedReleaseTag,
                args.ReleaseTag,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryShowNotification(new TrayNotification(
            LocalizedResources.Get(
                "MainWindow_Notification_AppUpdateTitle",
                "OpenBlade update available"),
            LocalizedResources.Format(
                "MainWindow_Notification_AppUpdateBody",
                "OpenBlade {0} is ready to download.",
                args.ReleaseTag.TrimStart('v', 'V')),
            TrayNotificationSeverity.Information,
            ReplacementTag: AppUpdateNotificationTag,
            Destination: TrayNotificationDestination.Updates)))
        {
            return false;
        }
        LastNotifiedReleaseTag = args.ReleaseTag;
        return true;
    }

    internal bool OnFirmwareUpdateAvailable(
        FirmwareUpdateAvailableEventArgs args,
        NotificationPreferences preferences)
    {
        if (!preferences.IsEnabled(NotificationCategory.FirmwareUpdates))
        {
            return false;
        }

        if (string.Equals(
                LastNotifiedFirmwareSignature,
                args.Signature,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (args.CanMigrateLegacySignature(LastNotifiedFirmwareSignature))
        {
            LastNotifiedFirmwareSignature = args.Signature;
            return true;
        }

        if (!TryShowNotification(new TrayNotification(
            LocalizedResources.Get(
                "MainWindow_Notification_FirmwareUpdateTitle",
                "Razer firmware update available"),
            args.Summary,
            TrayNotificationSeverity.Information,
            ReplacementTag: FirmwareUpdateNotificationTag,
            Destination: TrayNotificationDestination.Firmware)))
        {
            return false;
        }
        LastNotifiedFirmwareSignature = args.Signature;
        return true;
    }

    internal void Prime(WindowNotificationObservation observation)
    {
        lastObservedPowerSource = observation.PowerSource;
        lastObservedControllerAdvisory = observation.ControllerAdvisoryText;
        lastObservedOperationFailure = observation.OperationFailure;
        notificationObservationReady = true;
    }

    internal void Observe(
        string? propertyName,
        WindowNotificationObservation observation,
        NotificationPreferences preferences)
    {
        if (!notificationObservationReady)
        {
            return;
        }

        if (propertyName == nameof(MainViewModel.ActivePowerSource))
        {
            PowerSource current = observation.PowerSource;
            bool changed = current != lastObservedPowerSource;
            lastObservedPowerSource = current;
            if (changed
                && current != PowerSource.ExternalUnknown
                && preferences.IsEnabled(
                    NotificationCategory.ProfileAndPowerSourceChanges))
            {
                string profile = current switch
                {
                    PowerSource.PluggedIn => LocalizedResources.Get("MainWindow_Notification_ProfileAc", "AC profile"),
                    PowerSource.UsbC => LocalizedResources.Get("MainWindow_Notification_ProfileUsbC", "USB-C profile"),
                    PowerSource.Battery => LocalizedResources.Get("MainWindow_Notification_ProfileBattery", "Battery profile"),
                    _ => LocalizedResources.Get("MainWindow_Notification_ProfileActive", "active power profile"),
                };
                TryShowNotification(new TrayNotification(
                    LocalizedResources.Get(
                        "MainWindow_Notification_ProfileChangedTitle",
                        "Power profile changed"),
                    LocalizedResources.Format(
                        "MainWindow_Notification_ProfileChangedBody",
                        "OpenBlade is now using the {0}.",
                        profile),
                    TrayNotificationSeverity.Information,
                    ReplacementTag: ProfileNotificationTag,
                    Destination: TrayNotificationDestination.Laptop));
            }
        }
        else if (propertyName == nameof(MainViewModel.ControllerAdvisoryText))
        {
            string current = observation.ControllerAdvisoryText;
            bool changed = !string.Equals(
                current,
                lastObservedControllerAdvisory,
                StringComparison.Ordinal);
            lastObservedControllerAdvisory = current;
            if (changed
                && !string.IsNullOrWhiteSpace(current)
                && preferences.IsEnabled(NotificationCategory.ControllerAdvisories))
            {
                TryShowNotification(new TrayNotification(
                    LocalizedResources.Get(
                        "MainWindow_Notification_ControllerDetectedTitle",
                        "Razer controller detected"),
                    current,
                    TrayNotificationSeverity.Information,
                    ReplacementTag: ControllerAdvisoryNotificationTag,
                    Destination: TrayNotificationDestination.Troubleshooting));
            }
        }
        else if (propertyName == nameof(MainViewModel.TroubleshootingRecentErrorStatus))
        {
            string current = observation.OperationFailure;
            bool changed = !string.Equals(
                current,
                lastObservedOperationFailure,
                StringComparison.Ordinal);
            lastObservedOperationFailure = current;
            if (changed
                && !string.IsNullOrWhiteSpace(current)
                && preferences.IsEnabled(NotificationCategory.OperationFailures))
            {
                TryShowNotification(new TrayNotification(
                    LocalizedResources.Get(
                        "MainWindow_Notification_OperationFailedTitle",
                        "OpenBlade operation failed"),
                    current,
                    TrayNotificationSeverity.Error,
                    ReplacementTag: OperationFailureNotificationTag,
                    Destination: TrayNotificationDestination.Troubleshooting));
            }
        }
    }

    private bool TryShowNotification(TrayNotification notification)
    {
        try
        {
            return showNotification(notification);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "OpenBlade could not show a notification: {0}",
                exception.Message);
            return false;
        }
    }

}
