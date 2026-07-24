using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace OpenBlade.Tray;

internal sealed class WindowsAppNotificationPresenter : IDisposable
{
    private readonly AppNotificationManager manager;
    private readonly Uri? logoUri;
    private readonly Action<Exception>? errorHandler;
    private int disposed;

    private WindowsAppNotificationPresenter(Action<Exception>? errorHandler)
    {
        this.errorHandler = errorHandler;
        string logoPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Brand",
            "OpenBlade.png");
        logoUri = File.Exists(logoPath)
            ? new Uri(logoPath)
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
