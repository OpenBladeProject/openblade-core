using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace OpenBlade.Tray;

internal sealed class WindowsAppNotificationPresenter : IDisposable
{
    private readonly AppNotificationManager manager;
    private readonly string brandDirectory;
    private readonly Uri? defaultLogoUri;
    private readonly Action<Exception>? errorHandler;
    private int disposed;

    private WindowsAppNotificationPresenter(Action<Exception>? errorHandler)
    {
        this.errorHandler = errorHandler;
        brandDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Brand");
        string defaultLogoPath = Path.Combine(brandDirectory, "OpenBlade.png");
        defaultLogoUri = File.Exists(defaultLogoPath)
            ? new Uri(defaultLogoPath)
            : null;

        manager = AppNotificationManager.Default;
        manager.NotificationInvoked += OnNotificationInvoked;
        try
        {
            manager.Register();
        }
        catch
        {
            manager.NotificationInvoked -= OnNotificationInvoked;
            throw;
        }
    }

    internal static WindowsAppNotificationPresenter? CreateBestEffort(
        Action<Exception>? errorHandler = null)
    {
        try
        {
            return new WindowsAppNotificationPresenter(errorHandler);
        }
        catch (Exception exception)
        {
            Report(errorHandler, exception);
            return null;
        }
    }

    internal bool TryShow(TrayNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (Volatile.Read(ref disposed) != 0)
        {
            return false;
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(notification.Title)
                .AddText(notification.Body);
            Uri? logoUri = ResolveLogoUri(notification.LogoAssetRelativePath)
                ?? defaultLogoUri;
            if (logoUri is not null)
            {
                builder.SetAppLogoOverride(logoUri);
            }

            manager.Show(builder.BuildNotification());
            return true;
        }
        catch (Exception exception)
        {
            Report(errorHandler, exception);
            return false;
        }
    }

    private Uri? ResolveLogoUri(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        string brandRoot = Path.GetFullPath(brandDirectory)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(
            Path.Combine(brandDirectory, relativePath));
        return candidate.StartsWith(
                brandRoot,
                StringComparison.OrdinalIgnoreCase)
            && File.Exists(candidate)
            ? new Uri(candidate)
            : null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            manager.Unregister();
        }
        catch (Exception exception)
        {
            Report(errorHandler, exception);
        }
        finally
        {
            manager.NotificationInvoked -= OnNotificationInvoked;
        }
    }

    private static void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
    }

    private static void Report(Action<Exception>? errorHandler, Exception exception)
    {
        try
        {
            errorHandler?.Invoke(exception);
        }
        catch
        {
        }
    }
}
