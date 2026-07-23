using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface ISpecialKeyActionExecutor
{
    Task ExecuteAsync(SpecialKeySlot slot, CancellationToken cancellationToken);
}

public interface ISpecialKeyInputSender
{
    void SendKeyboardShortcut(string shortcut);

    void SendText(string text);

    void SendMouseAction(SpecialKeyActionKind action);

    void SendMediaAction(SpecialKeyActionKind action);

    void ToggleMicrophoneMute();

    void Launch(string target, bool requireWebAddress);
}

public sealed class SpecialKeyActionExecutor : ISpecialKeyActionExecutor
{
    private readonly ISpecialKeyBindingSource bindings;
    private readonly IServiceClient serviceClient;
    private readonly ISpecialKeyInputSender input;

    public SpecialKeyActionExecutor(
        ISpecialKeyBindingSource bindings,
        IServiceClient serviceClient,
        ISpecialKeyInputSender input)
    {
        this.bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        this.serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
    }

    public async Task ExecuteAsync(
        SpecialKeySlot slot,
        CancellationToken cancellationToken)
    {
        SpecialKeyAction action = SpecialKeyBindingDefaults.Normalize(bindings.GetAction(slot));
        switch (action.Kind)
        {
            case SpecialKeyActionKind.Default:
                if (slot == SpecialKeySlot.M4)
                {
                    await CyclePowerModeAsync(cancellationToken).ConfigureAwait(false);
                }
                return;
            case SpecialKeyActionKind.Disabled:
                return;
            case SpecialKeyActionKind.KeyboardShortcut:
                input.SendKeyboardShortcut(RequireValue(action));
                return;
            case SpecialKeyActionKind.MouseLeftClick:
            case SpecialKeyActionKind.MouseRightClick:
            case SpecialKeyActionKind.MouseMiddleClick:
            case SpecialKeyActionKind.MouseBack:
            case SpecialKeyActionKind.MouseForward:
            case SpecialKeyActionKind.MouseDoubleClick:
            case SpecialKeyActionKind.MouseScrollUp:
            case SpecialKeyActionKind.MouseScrollDown:
                input.SendMouseAction(action.Kind);
                return;
            case SpecialKeyActionKind.MediaPreviousTrack:
            case SpecialKeyActionKind.MediaPlayPause:
            case SpecialKeyActionKind.MediaNextTrack:
            case SpecialKeyActionKind.VolumeMute:
            case SpecialKeyActionKind.VolumeDown:
            case SpecialKeyActionKind.VolumeUp:
                input.SendMediaAction(action.Kind);
                return;
            case SpecialKeyActionKind.MicrophoneMute:
                input.ToggleMicrophoneMute();
                return;
            case SpecialKeyActionKind.LaunchApplication:
                input.Launch(RequireValue(action), requireWebAddress: false);
                return;
            case SpecialKeyActionKind.OpenWebsite:
                input.Launch(RequireValue(action), requireWebAddress: true);
                return;
            case SpecialKeyActionKind.TypeText:
                input.SendText(RequireValue(action));
                return;
            case SpecialKeyActionKind.CyclePowerMode:
                await CyclePowerModeAsync(cancellationToken).ConfigureAwait(false);
                return;
            case SpecialKeyActionKind.ToggleGamingMode:
                await ToggleGamingModeAsync(cancellationToken).ConfigureAwait(false);
                return;
            case SpecialKeyActionKind.CycleLightingEffect:
                await CycleLightingEffectAsync(cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new InvalidOperationException("The special-key action is unsupported.");
        }
    }

    private async Task CyclePowerModeAsync(CancellationToken cancellationToken)
    {
        ControlOperationResult result = await serviceClient.CyclePerformanceShortcutAsync(
            PerformanceShortcutSource.DedicatedButton,
            cancellationToken).ConfigureAwait(false);
        EnsureAccepted(result);
    }

    private async Task ToggleGamingModeAsync(CancellationToken cancellationToken)
    {
        ServiceStatusSnapshot status = await serviceClient.GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        if (status.GamingModeEnabled is not { } enabled)
        {
            throw new InvalidOperationException(
                "Gaming mode is not currently available from the laptop firmware.");
        }

        ControlOperationResult result = await serviceClient.ApplyOneShotLaptopControlAsync(
            new ApplyOneShotLaptopControl(
                OneShotLaptopControl.GamingMode,
                Enabled: !enabled),
            cancellationToken).ConfigureAwait(false);
        EnsureAccepted(result);
    }

    private async Task CycleLightingEffectAsync(CancellationToken cancellationToken)
    {
        ServiceStatusSnapshot status = await serviceClient.GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlySet<LightingEffect>? supported = status.Capabilities?.LightingEffects;
        if (supported is null || supported.Count == 0)
        {
            throw new InvalidOperationException("Keyboard lighting is unavailable.");
        }

        LightingEffect[] cycle = Enum.GetValues<LightingEffect>()
            .Where(supported.Contains)
            .ToArray();
        LightingSettings current = status.DesiredState.Lighting.Value;
        int currentIndex = Array.IndexOf(cycle, current.Effect);
        LightingEffect next = cycle[(currentIndex + 1 + cycle.Length) % cycle.Length];
        LightingSettings settings = CreateLightingSettings(next, current);
        ControlOperationResult result = await serviceClient.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                settings,
                OverrideLifetime.UntilPowerSourceChange),
            cancellationToken).ConfigureAwait(false);
        EnsureAccepted(result);
    }

    private static LightingSettings CreateLightingSettings(
        LightingEffect effect,
        LightingSettings current)
    {
        byte brightness = current.Brightness == 0
            ? (byte)50
            : current.Brightness;
        RgbColor color = current.Color == default
            ? LightingSettingsFactory.DefaultVisibleColor
            : current.Color;
        LightingSettings next = effect switch
        {
            LightingEffect.Off => LightingSettingsFactory.Off(),
            LightingEffect.Static => LightingSettingsFactory.Static(color, brightness),
            LightingEffect.Spectrum => LightingSettingsFactory.SpectrumCycling(brightness),
            LightingEffect.Breathing => LightingSettingsFactory.BreathingSingle(color, brightness),
            LightingEffect.Reactive => LightingSettingsFactory.Reactive(
                color,
                brightness,
                ReactiveLightingDuration.Medium),
            LightingEffect.Tidal => LightingSettingsFactory.Tidal(
                color,
                new RgbColor(255, 0, 170),
                randomColors: false,
                LightingDirection.Leftward,
                brightness),
            LightingEffect.Starlight => LightingSettingsFactory.StarlightSingle(
                color,
                brightness,
                StarlightLightingDuration.Medium),
            LightingEffect.Wave => LightingSettingsFactory.Wave(
                LightingDirection.Leftward,
                brightness),
            LightingEffect.Wheel => LightingSettingsFactory.Wheel(
                LightingRotationDirection.Clockwise,
                brightness),
            LightingEffect.Ripple => LightingSettingsFactory.Ripple(color, brightness),
            LightingEffect.AmbientAwareness => LightingSettingsFactory.AmbientAwareness(
                AmbientAwarenessRegion.Full,
                brightness),
            _ => throw new InvalidOperationException("The lighting effect is unsupported."),
        };
        return next with { LogoMode = current.LogoMode };
    }

    private static string RequireValue(SpecialKeyAction action) =>
        string.IsNullOrWhiteSpace(action.Value)
            ? throw new InvalidOperationException("The selected action requires a value.")
            : action.Value;

    private static void EnsureAccepted(ControlOperationResult result)
    {
        if (result.Status is ControlOperationStatus.Failed or ControlOperationStatus.Blocked)
        {
            throw new InvalidOperationException(result.Message);
        }
    }
}

public sealed class WindowsSpecialKeyInputSender : ISpecialKeyInputSender
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseWheel = 0x0800;
    private const uint MouseXDown = 0x0080;
    private const uint MouseXUp = 0x0100;
    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;
    private const int WheelDelta = 120;

    public void SendKeyboardShortcut(string shortcut)
    {
        (ushort key, ushort[] modifiers) = SpecialKeyShortcutParser.Parse(shortcut);
        var inputs = new List<NativeInput>((modifiers.Length + 1) * 2);
        inputs.AddRange(modifiers.Select(KeyDown));
        inputs.Add(KeyDown(key));
        inputs.Add(KeyUp(key));
        for (int index = modifiers.Length - 1; index >= 0; index--)
        {
            inputs.Add(KeyUp(modifiers[index]));
        }
        Send(inputs);
    }

    public void SendText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("Text cannot be empty.", nameof(text));
        }
        if (text.Length > SpecialKeyBindingDefaults.MaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(text));
        }

        var inputs = new List<NativeInput>(text.Length * 2);
        foreach (char character in text)
        {
            inputs.Add(Unicode(character, keyUp: false));
            inputs.Add(Unicode(character, keyUp: true));
        }
        Send(inputs);
    }

    public void SendMouseAction(SpecialKeyActionKind action)
    {
        NativeInput[] inputs = action switch
        {
            SpecialKeyActionKind.MouseLeftClick =>
                [Mouse(MouseLeftDown), Mouse(MouseLeftUp)],
            SpecialKeyActionKind.MouseRightClick =>
                [Mouse(MouseRightDown), Mouse(MouseRightUp)],
            SpecialKeyActionKind.MouseMiddleClick =>
                [Mouse(MouseMiddleDown), Mouse(MouseMiddleUp)],
            SpecialKeyActionKind.MouseBack =>
                [Mouse(MouseXDown, XButton1), Mouse(MouseXUp, XButton1)],
            SpecialKeyActionKind.MouseForward =>
                [Mouse(MouseXDown, XButton2), Mouse(MouseXUp, XButton2)],
            SpecialKeyActionKind.MouseDoubleClick =>
                [Mouse(MouseLeftDown), Mouse(MouseLeftUp), Mouse(MouseLeftDown), Mouse(MouseLeftUp)],
            SpecialKeyActionKind.MouseScrollUp =>
                [Mouse(MouseWheel, unchecked((uint)WheelDelta))],
            SpecialKeyActionKind.MouseScrollDown =>
                [Mouse(MouseWheel, unchecked((uint)-WheelDelta))],
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        Send(inputs);
    }

    public void SendMediaAction(SpecialKeyActionKind action)
    {
        ushort key = action switch
        {
            SpecialKeyActionKind.MediaPreviousTrack => 0xB1,
            SpecialKeyActionKind.MediaNextTrack => 0xB0,
            SpecialKeyActionKind.MediaPlayPause => 0xB3,
            SpecialKeyActionKind.VolumeMute => 0xAD,
            SpecialKeyActionKind.VolumeDown => 0xAE,
            SpecialKeyActionKind.VolumeUp => 0xAF,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        Send([KeyDown(key), KeyUp(key)]);
    }

    public void ToggleMicrophoneMute()
    {
        object? enumeratorObject = null;
        object? deviceObject = null;
        object? endpointObject = null;
        try
        {
            enumeratorObject = new MmDeviceEnumerator();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(
                EDataFlow.Capture,
                ERole.Multimedia,
                out IMMDevice device));
            deviceObject = device;
            Guid endpointVolumeId = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(
                ref endpointVolumeId,
                23,
                0,
                out object endpoint));
            endpointObject = endpoint;
            var volume = (IAudioEndpointVolume)endpoint;
            Marshal.ThrowExceptionForHR(volume.GetMute(out bool muted));
            Guid context = Guid.Empty;
            Marshal.ThrowExceptionForHR(volume.SetMute(!muted, ref context));
        }
        finally
        {
            ReleaseCom(endpointObject);
            ReleaseCom(deviceObject);
            ReleaseCom(enumeratorObject);
        }
    }

    public void Launch(string target, bool requireWebAddress)
    {
        if (string.IsNullOrWhiteSpace(target)
            || target.Length > SpecialKeyBindingDefaults.MaximumTargetLength)
        {
            throw new ArgumentException("The launch target is invalid.", nameof(target));
        }
        if (requireWebAddress
            && (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException(
                "Website actions require an http or https address.",
                nameof(target));
        }

        _ = Process.Start(new ProcessStartInfo(target)
        {
            UseShellExecute = true,
        }) ?? throw new Win32Exception("Windows did not start the selected target.");
    }

    private static void Send(IReadOnlyList<NativeInput> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }
        NativeInput[] buffer = inputs.ToArray();
        uint sent = SendInput(
            checked((uint)buffer.Length),
            buffer,
            Marshal.SizeOf<NativeInput>());
        if (sent != buffer.Length)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows did not accept the complete input sequence.");
        }
    }

    private static NativeInput KeyDown(ushort key) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = key,
                Flags = IsExtendedKey(key) ? KeyEventExtendedKey : 0,
            },
        },
    };

    private static NativeInput KeyUp(ushort key) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = key,
                Flags = KeyEventKeyUp
                    | (IsExtendedKey(key) ? KeyEventExtendedKey : 0),
            },
        },
    };

    private static bool IsExtendedKey(ushort key) => key is
        0x21 or 0x22 or 0x23 or 0x24
        or 0x25 or 0x26 or 0x27 or 0x28
        or 0x2D or 0x2E;

    private static NativeInput Unicode(char character, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                ScanCode = character,
                Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0),
            },
        },
    };

    private static NativeInput Mouse(uint flags, uint data = 0) => new()
    {
        Type = InputMouse,
        Data = new InputUnion
        {
            Mouse = new MouseInput
            {
                MouseData = data,
                Flags = flags,
            },
        },
    };

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        internal MouseInput Mouse;

        [FieldOffset(0)]
        internal KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    private enum EDataFlow
    {
        Render,
        Capture,
        All,
    }

    private enum ERole
    {
        Console,
        Multimedia,
        Communications,
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MmDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            EDataFlow dataFlow,
            ERole role,
            out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            uint classContext,
            nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] NativeInput[] inputs,
        int structureSize);
}
