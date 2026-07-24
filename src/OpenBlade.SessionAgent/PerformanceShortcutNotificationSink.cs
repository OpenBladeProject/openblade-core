using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenBlade.Contracts;
using OpenBlade.Session;
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

public sealed class WindowsTrayPerformanceShortcutNotificationSink
    : IPerformanceShortcutNotificationSink
{
    private static readonly nint BroadcastWindow = new(0xFFFF);

    private readonly uint notificationMessage;
    private readonly Action<Exception>? errorHandler;

    private WindowsTrayPerformanceShortcutNotificationSink(
        string messageName,
        Action<Exception>? errorHandler)
    {
        this.errorHandler = errorHandler;
        notificationMessage = RegisterWindowMessage(messageName);
        if (notificationMessage == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The performance notification message could not be registered.");
        }
    }

    public static IPerformanceShortcutNotificationSink CreateBestEffort(
        SessionAgentEventEndpoint endpoint,
        Action<Exception>? errorHandler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        try
        {
            return new WindowsTrayPerformanceShortcutNotificationSink(
                endpoint.PerformanceShortcutNotificationMessageName,
                errorHandler);
        }
        catch (Exception exception)
        {
            Report(errorHandler, exception);
            return NullPerformanceShortcutNotificationSink.Instance;
        }
    }

    public void ShowApplied(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        PerformancePreset? preset =
            PerformanceShortcutNotificationText.TryGetSelectedPreset(
                message,
                out PerformancePreset selected)
                ? selected
                : null;
        Send(new PerformanceShortcutNotificationSignal(
            PerformanceShortcutNotificationKind.CurrentMode,
            preset));
    }

    public void ShowSelected(PerformancePreset preset)
    {
        if (!Enum.IsDefined(preset))
        {
            return;
        }

        Send(new PerformanceShortcutNotificationSignal(
            PerformanceShortcutNotificationKind.CurrentMode,
            preset));
    }

    public void ShowFailed(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Send(new PerformanceShortcutNotificationSignal(
            PerformanceShortcutNotificationKind.Failed,
            Preset: null));
    }

    private void Send(PerformanceShortcutNotificationSignal signal)
    {
        if (!PostMessage(
                BroadcastWindow,
                notificationMessage,
                signal.Encode(),
                0))
        {
            Report(
                errorHandler,
                new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The tray could not be notified of the performance shortcut."));
        }
    }

    public void Dispose()
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

    [DllImport(
        "user32.dll",
        EntryPoint = "RegisterWindowMessageW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        nint window,
        uint message,
        nuint windowParameter,
        nint longParameter);
}
