namespace OpenBlade.Tray;

public enum TrayNotificationSeverity
{
    Information,
    Error,
}

public sealed record TrayNotification(
    string Title,
    string Body,
    TrayNotificationSeverity Severity,
    string? LogoAssetRelativePath = null,
    string? ReplacementTag = null);

public interface ITrayIconHost : IDisposable
{
    void ShowNotification(TrayNotification notification);
}
