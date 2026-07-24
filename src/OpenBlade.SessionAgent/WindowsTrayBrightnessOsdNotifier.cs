using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

internal sealed class NullDisplayBrightnessOsd : IDisplayBrightnessOsd
{
    internal static NullDisplayBrightnessOsd Instance { get; } = new();

    private NullDisplayBrightnessOsd()
    {
    }

    public void Show(byte brightness)
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class WindowsTrayBrightnessOsdNotifier : IDisplayBrightnessOsd
{
    private static readonly nint BroadcastWindow = new(0xFFFF);

    private readonly uint brightnessChangedMessage;
    private readonly Action<Exception>? errorHandler;

    private WindowsTrayBrightnessOsdNotifier(
        string messageName,
        Action<Exception>? errorHandler)
    {
        this.errorHandler = errorHandler;
        brightnessChangedMessage = RegisterWindowMessage(messageName);
        if (brightnessChangedMessage == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The display brightness notification message could not be registered.");
        }
    }

    public static IDisplayBrightnessOsd CreateBestEffort(
        SessionAgentEventEndpoint endpoint,
        Action<Exception>? errorHandler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        try
        {
            return new WindowsTrayBrightnessOsdNotifier(
                endpoint.DisplayBrightnessChangedMessageName,
                errorHandler);
        }
        catch (Exception exception)
        {
            errorHandler?.Invoke(exception);
            return NullDisplayBrightnessOsd.Instance;
        }
    }

    public void Show(byte brightness)
    {
        if (brightness > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(brightness));
        }

        if (!PostMessage(
                BroadcastWindow,
                brightnessChangedMessage,
                brightness,
                0))
        {
            errorHandler?.Invoke(
                new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The tray could not be notified of the display brightness change."));
        }
    }

    public void Dispose()
    {
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
