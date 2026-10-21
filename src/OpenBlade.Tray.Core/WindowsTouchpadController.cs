using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenBlade.Tray;

public interface ITouchpadController
{
    bool? ReadEnabled();

    void Toggle();
}

/// <summary>
/// Reads Windows Precision Touchpad state and sends Windows' documented
/// Ctrl+Win+F24 enable/disable chord. The chord is a toggle; callers must read
/// state before sending it and confirm the requested state afterward.
/// https://learn.microsoft.com/windows-hardware/design/component-guidelines/touchpad-enable-or-disable-toggle-button
/// https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-touchpad_parameters_v1
/// touchpadEnabled is system information: SPI_SETTOUCHPADPARAMETERS ignores it.
/// </summary>
public sealed class WindowsTouchpadController : ITouchpadController
{
    internal const uint GetTouchpadParameters = 0x00AE;
    internal const uint TouchpadParametersVersion1 = 1;
    internal const uint TouchpadPresent = 1 << 0;
    internal const uint TouchpadEnabled = 1 << 3;

    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyShift = 0x10;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyAlt = 0x12;
    private const ushort VirtualKeyLeftWindows = 0x5B;
    private const ushort VirtualKeyRightWindows = 0x5C;
    private const ushort VirtualKeyF24 = 0x87;

    private readonly GetTouchpadParametersDelegate getTouchpadParameters;
    private readonly SendInputDelegate sendInput;
    private readonly GetAsyncKeyStateDelegate getAsyncKeyState;

    public WindowsTouchpadController()
        : this(NativeGetTouchpadParameters, NativeSendInput, NativeGetAsyncKeyState)
    {
    }

    internal WindowsTouchpadController(
        GetTouchpadParametersDelegate getTouchpadParameters,
        SendInputDelegate sendInput,
        GetAsyncKeyStateDelegate getAsyncKeyState)
    {
        this.getTouchpadParameters = getTouchpadParameters
            ?? throw new ArgumentNullException(nameof(getTouchpadParameters));
        this.sendInput = sendInput ?? throw new ArgumentNullException(nameof(sendInput));
        this.getAsyncKeyState = getAsyncKeyState
            ?? throw new ArgumentNullException(nameof(getAsyncKeyState));
    }

    public bool? ReadEnabled()
    {
        var parameters = new TouchpadParametersV1
        {
            VersionNumber = TouchpadParametersVersion1,
        };
        if (!getTouchpadParameters(ref parameters)
            || (parameters.SystemInformation & TouchpadPresent) == 0)
        {
            return null;
        }

        return (parameters.SystemInformation & TouchpadEnabled) != 0;
    }

    public void Toggle()
    {
        if (IsDown(VirtualKeyShift)
            || IsDown(VirtualKeyControl)
            || IsDown(VirtualKeyAlt)
            || IsDown(VirtualKeyLeftWindows)
            || IsDown(VirtualKeyRightWindows)
            || IsDown(VirtualKeyF24))
        {
            throw new InvalidOperationException(
                "The touchpad shortcut cannot run while one of its keys is already held.");
        }

        NativeInput[] inputs =
        [
            Key(VirtualKeyControl, keyUp: false),
            Key(VirtualKeyLeftWindows, keyUp: false),
            Key(VirtualKeyF24, keyUp: false),
            Key(VirtualKeyF24, keyUp: true),
            Key(VirtualKeyLeftWindows, keyUp: true),
            Key(VirtualKeyControl, keyUp: true),
        ];
        uint sent = sendInput(inputs);
        if (sent == inputs.Length)
        {
            return;
        }

        int error = Marshal.GetLastWin32Error();
        bool cleanupSucceeded = ReleaseAcceptedKeyDowns(inputs, sent);
        throw new Win32Exception(
            error,
            cleanupSucceeded
                ? "Windows did not accept the complete touchpad shortcut."
                : "Windows did not accept the complete touchpad shortcut or its bounded key-release cleanup.");
    }

    private bool IsDown(ushort virtualKey) =>
        (getAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;

    private bool ReleaseAcceptedKeyDowns(NativeInput[] inputs, uint sent)
    {
        int accepted = Math.Min(checked((int)sent), inputs.Length);
        var down = new List<ushort>(3);
        for (int index = 0; index < accepted; index++)
        {
            NativeKeyboardInput keyboard = inputs[index].Data.Keyboard;
            if ((keyboard.Flags & KeyEventKeyUp) == 0)
            {
                down.Add(keyboard.VirtualKey);
            }
            else
            {
                down.Remove(keyboard.VirtualKey);
            }
        }

        if (down.Count == 0)
        {
            return true;
        }

        NativeInput[] releases = down
            .AsEnumerable()
            .Reverse()
            .Select(key => Key(key, keyUp: true))
            .ToArray();
        return sendInput(releases) == releases.Length;
    }

    private static NativeInput Key(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new NativeInputUnion
        {
            Keyboard = new NativeKeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? KeyEventKeyUp : 0,
            },
        },
    };

    private static bool NativeGetTouchpadParameters(ref TouchpadParametersV1 parameters) =>
        SystemParametersInfo(
            GetTouchpadParameters,
            checked((uint)Marshal.SizeOf<TouchpadParametersV1>()),
            ref parameters,
            0);

    private static uint NativeSendInput(NativeInput[] inputs) => SendInput(
        checked((uint)inputs.Length),
        inputs,
        Marshal.SizeOf<NativeInput>());

    private static short NativeGetAsyncKeyState(ushort virtualKey) =>
        GetAsyncKeyState(virtualKey);

    internal delegate bool GetTouchpadParametersDelegate(
        ref TouchpadParametersV1 parameters);

    internal delegate uint SendInputDelegate(NativeInput[] inputs);

    internal delegate short GetAsyncKeyStateDelegate(ushort virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    internal struct TouchpadParametersV1
    {
        internal uint VersionNumber;
        internal uint MaximumSupportedContacts;
        internal uint LegacyTouchpadFeatures;
        // Win32 BOOL bitfields: present is bit 0, enabled is bit 3.
        internal uint SystemInformation;
        // Win32 BOOL bitfields containing the mutable user settings.
        internal uint UserSettings;
        internal uint SensitivityLevel;
        internal uint CursorSpeed;
        internal uint FeedbackIntensity;
        internal uint ClickForceSensitivity;
        internal uint RightClickZoneWidth;
        internal uint RightClickZoneHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeInput
    {
        internal uint Type;
        internal NativeInputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct NativeInputUnion
    {
        [FieldOffset(0)]
        internal NativeMouseInput Mouse;

        [FieldOffset(0)]
        internal NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeKeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref TouchpadParametersV1 value,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] NativeInput[] inputs,
        int structureSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
