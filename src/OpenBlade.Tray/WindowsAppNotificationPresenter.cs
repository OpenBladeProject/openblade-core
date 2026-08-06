using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace OpenBlade.Tray;

internal sealed class WindowsAppNotificationPresenter : IDisposable
{
    private readonly AppNotificationManager manager;
    private readonly string brandDirectory;
    private readonly Uri? defaultLogoUri;
    private readonly Action<TrayNotificationDestination> notificationInvoked;
    private readonly Action<Exception>? errorHandler;
    private int disposed;

    private WindowsAppNotificationPresenter(
        Action<TrayNotificationDestination> notificationInvoked,
        Action<Exception>? errorHandler)
    {
        this.notificationInvoked = notificationInvoked
            ?? throw new ArgumentNullException(nameof(notificationInvoked));
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
        Action<TrayNotificationDestination> notificationInvoked,
        Action<Exception>? errorHandler = null)
    {
        try
        {
            return new WindowsAppNotificationPresenter(notificationInvoked, errorHandler);
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
                .AddText(notification.Body)
                .AddArgument("destination", notification.Destination.ToString());
            Uri? logoUri = ResolveLogoUri(notification.LogoAssetRelativePath)
                ?? defaultLogoUri;
            if (logoUri is not null)
            {
                builder.SetAppLogoOverride(logoUri);
            }

            if (!string.IsNullOrWhiteSpace(notification.ReplacementTag))
            {
                builder.SetTag(notification.ReplacementTag);
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

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        if (TryReadDestination(args.Arguments, out TrayNotificationDestination destination))
        {
            notificationInvoked(destination);
        }
    }

    private static bool TryReadDestination(
        IDictionary<string, string> arguments,
        out TrayNotificationDestination destination)
    {
        destination = TrayNotificationDestination.Home;
        return arguments.TryGetValue("destination", out string? value)
            && Enum.TryParse(value, ignoreCase: false, out destination);
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
