using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface IPerformanceShortcutNotificationSink : IDisposable
{
    void ShowSelected(PerformancePreset preset);

    void ShowApplied(string message);

    void ShowFailed(string message);
}

public static class PerformanceShortcutNotificationText
{
    private const string SelectedMarker = " selected ";

    public static string FormatAppliedBody(string serviceMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceMessage);

        return TryGetSelectedPreset(serviceMessage, out PerformancePreset preset)
            ? $"Current mode: {PerformancePresetPresentation.Label(preset)}"
            : serviceMessage;
    }

    public static bool TryGetSelectedPreset(
        string serviceMessage,
        out PerformancePreset preset)
    {
        preset = default;
        if (string.IsNullOrWhiteSpace(serviceMessage))
        {
            return false;
        }

        int markerIndex = serviceMessage.LastIndexOf(
            SelectedMarker,
            StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        string selectedValue = serviceMessage[
            (markerIndex + SelectedMarker.Length)..].TrimEnd('.');
        return Enum.TryParse(selectedValue, ignoreCase: false, out preset)
            && Enum.IsDefined(preset);
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

    public void ShowSelected(PerformancePreset preset)
    {
    }

    public void ShowFailed(string message)
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

    public void ShowSelected(PerformancePreset preset)
    {
        if (Volatile.Read(ref disposed) != 0 || !Enum.IsDefined(preset))
        {
            return;
        }

        Show(
            "Performance mode changed",
            $"Current mode: {PerformancePresetPresentation.Label(preset)}");
    }

    public void ShowFailed(string message)
    {
        if (Volatile.Read(ref disposed) != 0 || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Show("Performance mode could not be changed", message);
    }

    private void Show(string title, string body)
    {
        try
        {
            AppNotification notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
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
