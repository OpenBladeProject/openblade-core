namespace OpenBlade.Tray;

public enum TrayNotificationSeverity
{
    Information,
    Error,
    Warning,
}

public enum TrayNotificationDestination
{
    Home,
    Laptop,
    Updates,
    Firmware,
    Troubleshooting,
}

public sealed record TrayNotification(
    string Title,
    string Body,
    TrayNotificationSeverity Severity,
    string? LogoAssetRelativePath = null,
    string? ReplacementTag = null,
    TrayNotificationDestination Destination = TrayNotificationDestination.Home);

public interface ITrayIconHost : IDisposable
{
    void ShowNotification(TrayNotification notification);
}
