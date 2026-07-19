using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace OpenBlade.SessionAgent;

public interface IPerformanceShortcutNotificationSink : IDisposable
{
    void ShowApplied(string message);
}

internal sealed class NullPerformanceShortcutNotificationSink
    : IPerformanceShortcutNotificationSink
{
    internal static NullPerformanceShortcutNotificationSink Instance { get; } = new();

    private NullPerformanceShortcutNotificationSink()
    {
    }

    public void ShowApplied(string message)
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class WindowsPerformanceShortcutNotificationSink
    : IPerformanceShortcutNotificationSink
{
    private readonly AppNotificationManager manager;
    private readonly Action<Exception>? errorHandler;
    private int disposed;

    private WindowsPerformanceShortcutNotificationSink(Action<Exception>? errorHandler)
    {
        this.errorHandler = errorHandler;
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

    internal static IPerformanceShortcutNotificationSink CreateBestEffort(
        Action<Exception>? errorHandler = null)
    {
        try
        {
            return new WindowsPerformanceShortcutNotificationSink(errorHandler);
        }
        catch (Exception exception)
        {
            Report(errorHandler, exception);
            return NullPerformanceShortcutNotificationSink.Instance;
        }
    }

    public void ShowApplied(string message)
    {
        if (Volatile.Read(ref disposed) != 0 || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            AppNotification notification = new AppNotificationBuilder()
                .AddText("Performance mode changed")
                .AddText(message)
                .BuildNotification();
            manager.Show(notification);
        }
        catch (Exception exception)
        {
            Report(errorHandler, exception);
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
