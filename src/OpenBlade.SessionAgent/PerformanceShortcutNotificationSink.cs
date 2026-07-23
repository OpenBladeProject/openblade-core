using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface IPerformanceShortcutNotificationSink : IDisposable
{
    void ShowApplied(string message);
}

public static class PerformanceShortcutNotificationText
{
    private const string SelectedMarker = " selected ";

    public static string FormatAppliedBody(string serviceMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceMessage);

        int markerIndex = serviceMessage.LastIndexOf(
            SelectedMarker,
            StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return serviceMessage;
        }

        string selectedValue = serviceMessage[
            (markerIndex + SelectedMarker.Length)..].TrimEnd('.');
        return Enum.TryParse(selectedValue, ignoreCase: false, out PerformancePreset preset)
            && Enum.IsDefined(preset)
                ? $"Current mode: {PerformancePresetPresentation.Label(preset)}"
                : serviceMessage;
    }
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

public sealed class WindowsPerformanceShortcutNotificationSink
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

    public static IPerformanceShortcutNotificationSink CreateBestEffort(
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
                .AddText(PerformanceShortcutNotificationText.FormatAppliedBody(message))
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
