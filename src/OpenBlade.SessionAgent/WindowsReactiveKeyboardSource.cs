using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public sealed class WindowsReactiveKeyboardSourceFactory : IReactiveKeyboardSourceFactory
{
    public static WindowsReactiveKeyboardSourceFactory Instance { get; } = new();

    private WindowsReactiveKeyboardSourceFactory()
    {
    }

    public IReactiveKeyboardSource Create(ReactiveKeyboardSourceOptions options) =>
        new WindowsReactiveKeyboardSource(options);
}

/// <summary>
/// Session-local, message-only Raw Input window shared by Reactive lighting and
/// the captured Fn-layer shortcuts. The callbacks emit only anonymous matrix
/// slots or typed shortcut identities, never translated text, window titles,
/// or foreground-process information.
/// </summary>
internal sealed class WindowsReactiveKeyboardSource : IReactiveKeyboardSource
{
    private const uint WindowMessageInput = 0x00FF;
    private const uint WindowMessageInputDeviceChange = 0x00FE;
    private const uint WindowMessageClose = 0x0010;
    private const uint WindowMessageDestroy = 0x0002;
    private const uint RawInputCommand = 0x10000003;
    private const uint RawInputHeaderCommand = 0x10000005;
    private const uint RawInputTypeKeyboard = 1;
    private const uint RawInputTypeHid = 2;
    private const uint RawInputDeviceInputSink = 0x00000100;
    private const uint RawInputDeviceNotify = 0x00002000;
    private const uint RawInputDevicePageOnly = 0x00000020;
    private const uint RawInputDeviceRemove = 0x00000001;
    private const uint RawInputDeviceName = 0x20000007;
    private const int LowLevelKeyboardHook = 13;
    private const uint LowLevelInjectedFlag = 0x00000010;
    private const uint LowLevelAltDownFlag = 0x00000020;
    private const uint WindowMessageKeyDown = 0x0100;
    private const uint WindowMessageKeyUp = 0x0101;
    private const uint WindowMessageSystemKeyDown = 0x0104;
    private const uint WindowMessageSystemKeyUp = 0x0105;
    private const ushort RawKeyboardBreakFlag = 0x0001;
    private const int MaximumRawInputBytes = 64 * 1024;
    private const uint MaximumRawInputDevices = 128;
    private const ushort GenericDesktopUsagePage = 0x0001;
    private const ushort ConsumerUsagePage = 0x000C;
    private const ushort PageOnlyUsage = 0x0000;
    private static readonly nint MessageOnlyWindow = new(-3);
    private static readonly ConcurrentDictionary<nint, WindowsReactiveKeyboardSource>
        Sources = new();
    private static readonly WindowProcedure WindowProcedureCallback = ProcessWindowMessage;
    private static readonly LowLevelKeyboardProcedure KeyboardProcedureCallback =
        ProcessLowLevelKeyboard;
    private static WindowsReactiveKeyboardSource? keyboardHookOwner;

    private readonly Action<ReactiveKeyPulse> publish;
    private readonly Action<PerformanceShortcutSource> publishPerformanceShortcut;
    private readonly Action<SpecialKeySlot> publishSpecialKey;
    private readonly Func<SpecialKeySlot, bool> shouldOverrideSpecialKey;
    private readonly Action<SpecialKeySlot> publishDefaultLaptopKey;
    private readonly Action<FunctionRowInputEvent> publishFunctionRowInput;
    private readonly Func<FunctionKeyPrimaryMode?> readFunctionKeyPrimaryMode;
    private readonly Func<KeyboardDriverModeObservation> readDriverModeObservation;
    private readonly Func<GamingModeKeyPolicy> readGamingModeKeyPolicy;
    private readonly Func<RazerBladeInputDeviceKind> readVerifiedInputDeviceKind;
    private readonly Func<byte?> readBlade18KeyboardLayoutId;
    private readonly Func<SpecialKeySlot, bool> readSpecialKeyInputAdmitted;
    private readonly Action<Blade18FnShortcut>? publishBlade18FnShortcut;
    private readonly Func<bool> isBlade18FnInputAdmitted;
    private readonly Action<Blade18FnDiagnostic> reportBlade18FnDiagnostic;
    private readonly Rz090484FnInputState blade18Fn = new();
    private readonly Blade18DefaultFnInputState blade18Keyboard = new();
    private readonly Blade18FnHookCorrelation blade18Correlation = new();
    private readonly HashSet<(uint ScanCode, bool Extended)> blade18ConfirmedPresses = [];
    private readonly Action resetFunctionRowInput;
    private int blade18ResetRequested;
    private readonly RazerBladeFunctionRowInputState functionRowInput = new();
    private readonly SpecialKeyKeyboardSuppressionState specialKeyKeyboardInput = new();
    private readonly GamingModeKeyBlockState gamingModeKeyBlockState = new();
    private readonly LowLevelKeyboardHookHealthTracker hookHealth = new();
    private readonly Rz090528FunctionPInputState rz090528FunctionPInput = new();
    private readonly string windowClassName = $"OpenBlade.Reactive.{Guid.NewGuid():N}";
    private readonly TaskCompletionSource initialization =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread messageThread;
    private readonly ConcurrentDictionary<nint, RazerBladeInputDeviceKind>
        inputDevices = new();
    private nint rawInputBuffer;
    private int rawInputBufferCapacity;
    private nint windowHandle;
    private uint messageThreadId;
    private bool performanceFunctionKeyPressed;
    private bool dedicatedPerformanceButtonPressed;
    private bool functionPPressed;
    private bool defaultGamingModePressed;
    private bool defaultMicrophoneMutePressed;
    private readonly bool[] specialKeyPressed = new bool[5];
    private nint keyboardHook;
    private int disposed;

    internal WindowsReactiveKeyboardSource(ReactiveKeyboardSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        publish = options.PublishPulse
            ?? throw new ArgumentNullException(nameof(options.PublishPulse));
        publishPerformanceShortcut = options.PublishPerformanceShortcut
            ?? throw new ArgumentNullException(nameof(options.PublishPerformanceShortcut));
        publishSpecialKey = options.PublishSpecialKey
            ?? throw new ArgumentNullException(nameof(options.PublishSpecialKey));
        shouldOverrideSpecialKey = options.ShouldOverrideSpecialKey
            ?? throw new ArgumentNullException(nameof(options.ShouldOverrideSpecialKey));
        publishDefaultLaptopKey = options.PublishDefaultLaptopKey
            ?? throw new ArgumentNullException(nameof(options.PublishDefaultLaptopKey));
        publishFunctionRowInput = options.PublishFunctionRowInput
            ?? throw new ArgumentNullException(nameof(options.PublishFunctionRowInput));
        readFunctionKeyPrimaryMode = options.ReadFunctionKeyPrimaryMode
            ?? throw new ArgumentNullException(nameof(options.ReadFunctionKeyPrimaryMode));
        readDriverModeObservation = options.ReadDriverModeObservation
            ?? throw new ArgumentNullException(nameof(options.ReadDriverModeObservation));
        readGamingModeKeyPolicy = options.ReadGamingModeKeyPolicy
            ?? throw new ArgumentNullException(nameof(options.ReadGamingModeKeyPolicy));
        readBlade18KeyboardLayoutId = options.ReadBlade18KeyboardLayoutId;
        readVerifiedInputDeviceKind = options.ReadVerifiedInputDeviceKind
            ?? throw new ArgumentNullException(nameof(options.ReadVerifiedInputDeviceKind));
        readSpecialKeyInputAdmitted = options.IsSpecialKeyInputAdmitted
            ?? throw new ArgumentNullException(nameof(options.IsSpecialKeyInputAdmitted));
        resetFunctionRowInput = options.ResetFunctionRowInput;
        publishBlade18FnShortcut = options.PublishBlade18FnShortcut;
        isBlade18FnInputAdmitted = options.IsBlade18FnInputAdmitted ?? (() => false);
        reportBlade18FnDiagnostic = options.ReportBlade18FnDiagnostic;
        messageThread = new Thread(RunMessageWindow)
        {
            IsBackground = true,
            Name = "OpenBlade Reactive input",
        };
        messageThread.Start();
        try
        {
            initialization.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Task Completion => completion.Task;

    public void ResetFunctionPInput()
    {
        rz090528FunctionPInput.RequestReset();
        Interlocked.Exchange(ref blade18ResetRequested, 1);
    }

    private bool ReadBlade18Admission()
    {
        if (Interlocked.Exchange(ref blade18ResetRequested, 0) != 0)
        {
            blade18Fn.Reset();
            CancelBlade18Input();
        }
        bool admitted = readVerifiedInputDeviceKind() == RazerBladeInputDeviceKind.Rz09_0484
            && isBlade18FnInputAdmitted();
        if (!admitted)
        {
            blade18Fn.Invalidate();
            CancelBlade18Input();
        }
        return admitted;
    }

    private void CancelBlade18Input()
    {
        blade18Keyboard.Cancel();
        blade18Correlation.Clear();
        if (blade18ConfirmedPresses.Count > 0)
        {
            blade18ConfirmedPresses.Clear();
            resetFunctionRowInput();
        }
    }

    private void ConfirmBlade18Input(Blade18DefaultFnInputEvent input, bool extended)
    {
        var key = (input.ScanCode, extended);
        if (input.Phase == FunctionRowInputPhase.Down)
        {
            blade18ConfirmedPresses.Add(key);
        }
        else if (input.Phase == FunctionRowInputPhase.Up)
        {
            if (!blade18ConfirmedPresses.Remove(key))
            {
                return;
            }
        }
        else if (!blade18ConfirmedPresses.Contains(key))
        {
            return;
        }
        PublishBlade18Input(input);
    }

    internal static Blade18FnReportObservation ObserveBlade18FnReport(
        Rz090484FnInputState state,
        RazerBladeInputDeviceKind deviceKind,
        nint collection,
        ReadOnlySpan<byte> report,
        bool admitted,
        Action<Blade18FnDiagnostic> reportDiagnostic)
    {
        Blade18FnReportObservation observation = state.ObserveHid(
            deviceKind,
            collection,
            report,
            admitted);
        Blade18FnDiagnostic? diagnostic = observation switch
        {
            Blade18FnReportObservation.FirstReleased or Blade18FnReportObservation.Released =>
                new(Blade18FnDiagnosticStage.FnReportObserved, Phase: FunctionRowInputPhase.Up),
            Blade18FnReportObservation.Pressed =>
                new(Blade18FnDiagnosticStage.FnReportObserved, Phase: FunctionRowInputPhase.Down),
            _ => null,
        };
        if (diagnostic is not null)
        {
            ReportBlade18FnDiagnostic(reportDiagnostic, diagnostic);
        }
        return observation;
    }

    internal static void EmitAndRouteConfirmedBlade18Shortcut(
        Blade18DefaultFnInputEvent input,
        bool extended,
        Action<Blade18FnDiagnostic> reportDiagnostic,
        Action<Blade18DefaultFnInputEvent, bool> route)
    {
        if (TryGetBlade18FnShortcut(input.Action, out Blade18FnShortcut shortcut))
        {
            ReportBlade18FnDiagnostic(
                reportDiagnostic,
                new(
                    Blade18FnDiagnosticStage.HookRawInputConfirmed,
                    shortcut,
                    input.Phase));
        }
        route(input, extended);
    }

    internal static bool TryGetBlade18FnShortcut(
        Blade18DefaultFnAction action,
        out Blade18FnShortcut shortcut)
    {
        shortcut = action switch
        {
            Blade18DefaultFnAction.Performance => Blade18FnShortcut.Performance,
            Blade18DefaultFnAction.RefreshRate => Blade18FnShortcut.RefreshRate,
            Blade18DefaultFnAction.Touchpad => Blade18FnShortcut.Touchpad,
            _ => default,
        };
        return action is Blade18DefaultFnAction.Performance
            or Blade18DefaultFnAction.RefreshRate
            or Blade18DefaultFnAction.Touchpad;
    }

    private static void ReportBlade18FnDiagnostic(
        Action<Blade18FnDiagnostic> reportDiagnostic,
        Blade18FnDiagnostic diagnostic)
    {
        try
        {
            reportDiagnostic(diagnostic);
        }
        catch
        {
            // Diagnostics must not affect input gating or suppression.
        }
    }

    private void PublishBlade18Input(Blade18DefaultFnInputEvent input)
    {
        if (input.Action is Blade18DefaultFnAction.Performance
            or Blade18DefaultFnAction.RefreshRate or Blade18DefaultFnAction.Touchpad)
        {
            if (input.Phase == FunctionRowInputPhase.Up)
            {
                if (input.Action == Blade18DefaultFnAction.Performance)
                {
                    publishPerformanceShortcut(PerformanceShortcutSource.FunctionP);
                }
                else
                {
                    publishBlade18FnShortcut?.Invoke(input.Action == Blade18DefaultFnAction.RefreshRate
                        ? Blade18FnShortcut.RefreshRate : Blade18FnShortcut.Touchpad);
                }
            }
            return;
        }
        FunctionRowAction action = input.Action switch
        {
            Blade18DefaultFnAction.VolumeMute => FunctionRowAction.VolumeMute,
            Blade18DefaultFnAction.VolumeDown => FunctionRowAction.VolumeDown,
            Blade18DefaultFnAction.VolumeUp => FunctionRowAction.VolumeUp,
            Blade18DefaultFnAction.Project => FunctionRowAction.Project,
            Blade18DefaultFnAction.PreviousTrack => FunctionRowAction.PreviousTrack,
            Blade18DefaultFnAction.PlayPause => FunctionRowAction.PlayPause,
            Blade18DefaultFnAction.NextTrack => FunctionRowAction.NextTrack,
            Blade18DefaultFnAction.DisplayBrightnessDown => FunctionRowAction.DisplayBrightnessDown,
            Blade18DefaultFnAction.DisplayBrightnessUp => FunctionRowAction.DisplayBrightnessUp,
            Blade18DefaultFnAction.KeyboardBrightnessDown => FunctionRowAction.KeyboardBrightnessDown,
            Blade18DefaultFnAction.KeyboardBrightnessUp => FunctionRowAction.KeyboardBrightnessUp,
            Blade18DefaultFnAction.PrintScreen => FunctionRowAction.PrintScreen,
            Blade18DefaultFnAction.PageUp => FunctionRowAction.PageUp,
            Blade18DefaultFnAction.Home => FunctionRowAction.Home,
            Blade18DefaultFnAction.PageDown => FunctionRowAction.PageDown,
            Blade18DefaultFnAction.End => FunctionRowAction.End,
            Blade18DefaultFnAction.Insert => FunctionRowAction.Insert,
            _ => throw new InvalidOperationException("Unknown Blade 18 Fn action."),
        };
        publishFunctionRowInput(new(input.ScanCode, action, input.Phase));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        nint handle = Volatile.Read(ref windowHandle);
        bool closePosted = handle != 0 && PostMessage(handle, WindowMessageClose, 0, 0);
        if (!closePosted && Volatile.Read(ref messageThreadId) is uint threadId and not 0)
        {
            _ = PostThreadMessage(threadId, 0x0012, 0, 0); // WM_QUIT
        }

        if (GetCurrentThreadId() != Volatile.Read(ref messageThreadId)
            && !messageThread.Join(TimeSpan.FromSeconds(5)))
        {
            Trace.TraceWarning("Reactive input message window did not stop promptly.");
        }
    }

    private void RunMessageWindow()
    {
        nint module = 0;
        bool classRegistered = false;
        bool inputRegistered = false;
        try
        {
            messageThreadId = GetCurrentThreadId();
            module = GetModuleHandle(null);
            if (module == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var windowClass = new WindowClass
            {
                Size = checked((uint)Marshal.SizeOf<WindowClass>()),
                Instance = module,
                Procedure = WindowProcedureCallback,
                ClassName = windowClassName,
            };
            if (RegisterClassEx(ref windowClass) == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to register the Reactive input message window.");
            }
            classRegistered = true;

            nint created = CreateWindowEx(
                0,
                windowClassName,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                MessageOnlyWindow,
                0,
                module,
                0);
            if (created == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to create the Reactive input message window.");
            }

            windowHandle = created;
            Sources[created] = this;
            RegisterRawInput(created, RawInputDeviceInputSink | RawInputDeviceNotify);
            inputRegistered = true;
            if (Interlocked.CompareExchange(ref keyboardHookOwner, this, null) is not null)
            {
                throw new InvalidOperationException(
                    "A special-key keyboard hook is already active in this session agent.");
            }
            keyboardHook = SetWindowsHookEx(
                LowLevelKeyboardHook,
                KeyboardProcedureCallback,
                module,
                0);
            if (keyboardHook == 0)
            {
                Interlocked.CompareExchange(ref keyboardHookOwner, null, this);
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to observe shared Fn-layer keys.");
            }
            initialization.TrySetResult();

            while (true)
            {
                int result = GetMessage(out WindowMessage message, 0, 0, 0);
                if (result == 0)
                {
                    break;
                }
                if (result < 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            initialization.TrySetException(exception);
            completion.TrySetException(exception);
            if (Volatile.Read(ref disposed) == 0)
            {
                Trace.TraceError(
                    "Reactive keyboard observation failed: {0}",
                    exception.GetType().Name);
            }
        }
        finally
        {
            nint hook = Interlocked.Exchange(ref keyboardHook, 0);
            if (hook != 0 && !UnhookWindowsHookEx(hook))
            {
                Trace.TraceWarning(
                    "Special-key keyboard hook cleanup failed: {0}",
                    Marshal.GetLastWin32Error());
            }
            Interlocked.CompareExchange(ref keyboardHookOwner, null, this);
            ReleaseRawInputBuffer();
            if (inputRegistered)
            {
                try
                {
                    RegisterRawInput(0, RawInputDeviceRemove);
                }
                catch (Win32Exception exception)
                {
                    Trace.TraceWarning(
                        "Reactive input registration cleanup failed: {0}",
                        exception.NativeErrorCode);
                }
            }

            nint handle = Interlocked.Exchange(ref windowHandle, 0);
            if (handle != 0)
            {
                Sources.TryRemove(handle, out _);
                _ = DestroyWindow(handle);
            }
            if (classRegistered)
            {
                _ = UnregisterClass(windowClassName, module);
            }

            initialization.TrySetException(
                new InvalidOperationException("Reactive input stopped before initialization."));
            completion.TrySetResult();
        }
    }

    private static nint ProcessWindowMessage(
        nint window,
        uint message,
        nuint windowParameter,
        nint messageParameter)
    {
        if (message == WindowMessageInput
            && Sources.TryGetValue(window, out WindowsReactiveKeyboardSource? source)
            && Volatile.Read(ref source.disposed) == 0)
        {
            try
            {
                source.ProcessRawInput(
                    messageParameter,
                    unchecked((uint)GetMessageTime()));
            }
            catch (Exception exception)
            {
                source.rz090528FunctionPInput.Reset();
                source.ResetFunctionPInput();
                Trace.TraceError(
                    "Reactive keyboard packet processing failed: {0}",
                    exception.GetType().Name);
            }
        }
        else if (message == WindowMessageInputDeviceChange
            && Sources.TryGetValue(window, out WindowsReactiveKeyboardSource? changedSource))
        {
            // Raw Input device handles can be replaced across sleep, reconnect,
            // and driver restarts. Never retain an identity decision for a
            // handle after Windows reports either arrival or removal.
            changedSource.inputDevices.TryRemove(messageParameter, out _);
            changedSource.rz090528FunctionPInput.Reset();
            changedSource.ResetFunctionPInput();
            changedSource.CancelBlade18InputForTopology();
        }
        else if (message == WindowMessageClose)
        {
            _ = DestroyWindow(window);
            return 0;
        }
        else if (message == WindowMessageDestroy)
        {
            Sources.TryRemove(window, out _);
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(window, message, windowParameter, messageParameter);
    }

    private static nint ProcessLowLevelKeyboard(
        int code,
        nuint windowParameter,
        nint messageParameter)
    {
        WindowsReactiveKeyboardSource? owner = Volatile.Read(ref keyboardHookOwner);
        if (code >= 0
            && owner is not null
            && windowParameter is
                WindowMessageKeyDown or
                WindowMessageKeyUp or
                WindowMessageSystemKeyDown or
                WindowMessageSystemKeyUp)
        {
            LowLevelKeyboardInput input =
                Marshal.PtrToStructure<LowLevelKeyboardInput>(messageParameter);
            if ((input.Flags & LowLevelInjectedFlag) == 0)
            {
                bool keyDown = windowParameter is
                    WindowMessageKeyDown or WindowMessageSystemKeyDown;
                owner.hookHealth.Record(input.ScanCode, keyDown, input.Time);
                if (owner.gamingModeKeyBlockState.ShouldSuppress(
                    input.VirtualKey,
                    keyDown,
                    owner.readGamingModeKeyPolicy(),
                    altContext: (input.Flags & LowLevelAltDownFlag) != 0))
                {
                    return 1;
                }
                if (owner.specialKeyKeyboardInput.ShouldSuppress(
                    input.VirtualKey,
                    keyDown,
                    owner.performanceFunctionKeyPressed,
                    owner.readVerifiedInputDeviceKind(),
                    owner.readSpecialKeyInputAdmitted,
                    owner.shouldOverrideSpecialKey,
                    out SpecialKeySlot? triggeredSpecialKey))
                {
                    if (triggeredSpecialKey is { } specialKey)
                    {
                        owner.specialKeyPressed[(int)specialKey] = true;
                        owner.publishSpecialKey(specialKey);
                    }
                    return 1;
                }
                // The low-level callback precedes the async-state update, so
                // the high bit distinguishes a held repeat from a new down
                // whose earlier release was never observed.
                bool keyWasAlreadyDown = keyDown
                    && (GetAsyncKeyState(checked((int)input.VirtualKey)) & 0x8000) != 0;
                bool blade18Admitted = owner.ReadBlade18Admission();
                if (ProcessBlade18KeyboardInput(
                    owner.blade18Keyboard,
                    input.ScanCode,
                    extended: (input.Flags & 0x01) != 0,
                    keyDown,
                    keyWasAlreadyDown,
                    owner.blade18Fn.FunctionHeld,
                    blade18Admitted,
                    HasExclusiveBlade18Keyboard,
                    owner.CancelBlade18InputForTopology,
                    out Blade18DefaultFnInputEvent? blade18Event))
                {
                    if (!owner.blade18Correlation.Record(
                        input.ScanCode, (input.Flags & 0x01) != 0,
                        keyDown, input.Time, blade18Event))
                    {
                        owner.CancelBlade18Input();
                    }
                    return 1;
                }
                bool suppressFunctionRow = FunctionRowKeyboardHookProcessor.Process(
                    owner.functionRowInput,
                    owner.readVerifiedInputDeviceKind(),
                    input.ScanCode,
                    keyDown,
                    keyWasAlreadyDown,
                    owner.performanceFunctionKeyPressed,
                    owner.readFunctionKeyPrimaryMode(),
                    owner.readDriverModeObservation().AllowsFunctionRowInterception,
                    owner.publishFunctionRowInput);
                if (suppressFunctionRow)
                {
                    return 1;
                }
            }
        }

        return CallNextHookEx(0, code, windowParameter, messageParameter);
    }

    internal static bool ProcessBlade18KeyboardInput(
        Blade18DefaultFnInputState state,
        uint scanCode,
        bool extended,
        bool keyDown,
        bool keyWasAlreadyDown,
        bool functionHeld,
        bool admitted,
        Func<bool> hasExclusiveKeyboard,
        Action cancelForTopology,
        out Blade18DefaultFnInputEvent? inputEvent)
    {
        // This backend lacks a verified per-device suppression or Driver-mode
        // path. Check fresh topology before swallowing any global hook edge,
        // including repeats/releases latched while topology was still valid.
        if (state.MaySuppress(scanCode, extended, keyDown, keyWasAlreadyDown, functionHeld, admitted)
            && !hasExclusiveKeyboard())
        {
            state.PassThroughAllPresses();
            cancelForTopology();
            admitted = false;
        }
        return state.Process(scanCode, extended, keyDown, keyWasAlreadyDown,
            functionHeld, admitted, out inputEvent);
    }

    private void CancelBlade18InputForTopology()
    {
        blade18Fn.Invalidate();
        blade18Keyboard.PassThroughAllPresses();
        CancelBlade18Input();
    }

    private static bool HasExclusiveBlade18Keyboard() => HasExclusiveBlade18Keyboard(
        IsLocalInputSession(), GetRawInputDeviceList, TryResolveInputDevice,
        static reason => Trace.TraceInformation("Blade 18 Fn interception paused: {0}", reason));

    internal static bool HasExclusiveBlade18Keyboard(
        bool localSession,
        RawInputDeviceListReader readDevices,
        Func<nint, RazerBladeInputDeviceKind?> resolveDevice,
        Action<string>? reportUnavailable = null)
    {
        // RDP devices are omitted from GetRawInputDeviceList, so a confirmed
        // local session is necessary even when the list contains only the laptop.
        if (!localSession)
        {
            reportUnavailable?.Invoke("The input session is remote or could not be verified as local.");
            return false;
        }
        uint count = 0;
        uint entrySize = checked((uint)Marshal.SizeOf<RawInputDeviceListEntry>());
        if (readDevices(null, ref count, entrySize) != 0
            || count == 0 || count > MaximumRawInputDevices)
        {
            reportUnavailable?.Invoke("The Raw Input device count failed or was outside the supported bounds.");
            return false;
        }
        var devices = new RawInputDeviceListEntry[count];
        uint returned = readDevices(devices, ref count, entrySize);
        // Success returns the number written; count can remain the original
        // capacity when a device disappears between the two calls.
        if (returned == 0 || returned > devices.Length)
        {
            reportUnavailable?.Invoke("The Raw Input device list failed or changed during enumeration.");
            return false;
        }
        bool foundKeyboard = false;
        for (int index = 0; index < returned; index++)
        {
            RawInputDeviceListEntry device = devices[index];
            if (device.Type != RawInputTypeKeyboard)
            {
                continue;
            }
            if (device.Device == 0
                || resolveDevice(device.Device) != RazerBladeInputDeviceKind.Rz09_0484)
            {
                reportUnavailable?.Invoke("Another keyboard is present or a keyboard identity could not be verified.");
                return false;
            }
            foundKeyboard = true;
        }
        if (!foundKeyboard)
        {
            reportUnavailable?.Invoke("No Blade 18 keyboard was found in the Raw Input device list.");
        }
        return foundKeyboard;
    }

    private static bool IsLocalInputSession()
    {
        const int currentSession = -1;
        const int clientProtocolType = 16;
        nint buffer = 0;
        try
        {
            return WTSQuerySessionInformation(0, currentSession, clientProtocolType,
                    out buffer, out uint bytes)
                && buffer != 0 && bytes == sizeof(ushort)
                && Marshal.ReadInt16(buffer) == 0;
        }
        finally
        {
            if (buffer != 0)
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    private void InvalidateBlade18Packet()
    {
        blade18Fn.Invalidate();
        CancelBlade18Input();
    }

    private void ProcessRawInput(nint rawInputHandle, uint messageTime)
    {
        uint headerSize = checked((uint)Marshal.SizeOf<RawInputHeader>());
        uint headerBytes = headerSize;
        var header = new RawInputHeader();
        uint headerResult = GetRawInputHeader(
            rawInputHandle,
            RawInputHeaderCommand,
            ref header,
            ref headerBytes,
            headerSize);
        if (headerResult == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (headerResult < headerSize
            || headerBytes < headerSize
            || header.Size < headerSize
            || header.Size > MaximumRawInputBytes)
        {
            InvalidateBlade18Packet();
            return;
        }

        bool targetDevice = TryGetInputDeviceKind(
            header.Device,
            out RazerBladeInputDeviceKind deviceKind);
        // Every keyboard packet must consume its exact hook observation. If
        // non-target keyboards left observations behind, an identical target
        // edge could falsely make a missing hook appear healthy.
        if (header.Type != RawInputTypeKeyboard && !targetDevice)
        {
            return;
        }

        uint size = header.Size;
        nint buffer = EnsureRawInputBuffer(size);
        uint receivedSize = size;
        uint result = GetRawInputData(
            rawInputHandle,
            RawInputCommand,
            buffer,
            ref receivedSize,
            headerSize);
        if (result == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (result < headerSize || receivedSize < headerSize)
        {
            InvalidateBlade18Packet();
            return;
        }

        RawInputHeader receivedHeader = Marshal.PtrToStructure<RawInputHeader>(buffer);
        if (receivedHeader.Size < headerSize
            || receivedHeader.Size > receivedSize
            || receivedHeader.Device != header.Device)
        {
            InvalidateBlade18Packet();
            return;
        }

        if (receivedHeader.Type == RawInputTypeKeyboard)
        {
            ProcessKeyboard(buffer, headerSize, receivedSize, deviceKind, messageTime);
        }
        else if (receivedHeader.Type == RawInputTypeHid
            && deviceKind != RazerBladeInputDeviceKind.Unsupported)
        {
            ProcessHid(buffer, headerSize, receivedSize, deviceKind, receivedHeader.Device);
        }
    }

    private nint EnsureRawInputBuffer(uint size)
    {
        int requested = checked((int)size);
        if (rawInputBufferCapacity >= requested)
        {
            return rawInputBuffer;
        }

        rawInputBuffer = rawInputBuffer == 0
            ? Marshal.AllocHGlobal(requested)
            : Marshal.ReAllocHGlobal(rawInputBuffer, requested);
        rawInputBufferCapacity = requested;
        return rawInputBuffer;
    }

    private void ReleaseRawInputBuffer()
    {
        nint buffer = Interlocked.Exchange(ref rawInputBuffer, 0);
        rawInputBufferCapacity = 0;
        if (buffer != 0)
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ProcessKeyboard(
        nint buffer,
        uint headerSize,
        uint receivedSize,
        RazerBladeInputDeviceKind deviceKind,
        uint messageTime)
    {
        int keyboardSize = Marshal.SizeOf<RawKeyboard>();
        if (receivedSize < headerSize + keyboardSize)
        {
            if (deviceKind == RazerBladeInputDeviceKind.Rz09_0484)
            {
                InvalidateBlade18Packet();
            }
            return;
        }

        RawKeyboard keyboard = Marshal.PtrToStructure<RawKeyboard>(
            nint.Add(buffer, checked((int)headerSize)));
        bool keyDown = (keyboard.Flags & RawKeyboardBreakFlag) == 0;

        if (rz090528FunctionPInput.TryCreateTrigger(
                deviceKind,
                keyboard.MakeCode,
                keyboard.Flags,
                keyboard.VirtualKey,
                IsRz090528FunctionPAdmitted()))
        {
            publishPerformanceShortcut(PerformanceShortcutSource.FunctionP);
        }

        bool observedByHook = hookHealth.Consume(
            keyboard.MakeCode,
            keyDown,
            messageTime);
        bool extended = (keyboard.Flags & 0x02) != 0;
        bool blade18Admitted = ReadBlade18Admission();
        bool correlated = blade18Correlation.TryConsume(
            keyboard.MakeCode, extended, keyDown, messageTime,
            out Blade18DefaultFnInputEvent? blade18Event);
        if (correlated && !HasExclusiveBlade18Keyboard())
        {
            CancelBlade18InputForTopology();
            blade18Admitted = false;
        }
        if (correlated)
        {
            if (deviceKind == RazerBladeInputDeviceKind.Rz09_0484 && blade18Admitted)
            {
                if (blade18Event is { } confirmed)
                {
                    EmitAndRouteConfirmedBlade18Shortcut(
                        confirmed,
                        extended,
                        reportBlade18FnDiagnostic,
                        ConfirmBlade18Input);
                }
            }
            else
            {
                // A hook has no keyboard identity. Never execute its tentative
                // action until the matching Raw Input packet confirms it.
                CancelBlade18Input();
            }
        }
        else if (!observedByHook
            && deviceKind == RazerBladeInputDeviceKind.Rz09_0484
            && blade18Keyboard.ReconcilePassedThroughEdge(
                keyboard.MakeCode, extended, keyDown, out Blade18DefaultFnInputEvent? release))
        {
            if (blade18Admitted && release is { } released)
            {
                ConfirmBlade18Input(released, extended);
            }
            if (keyDown && blade18ConfirmedPresses.Remove((keyboard.MakeCode, extended)))
            {
                resetFunctionRowInput();
            }
            ReinstallKeyboardHook();
        }
        FunctionRowHookRecoveryResult recovery =
            FunctionRowHookRecovery.EvaluateTargetPacket(
                functionRowInput,
                deviceKind,
                observedByHook,
                keyboard.MakeCode,
                keyDown);
        if (recovery.ReinstallHook)
        {
            if (recovery.ReleaseEvent is { } inputEvent)
            {
                publishFunctionRowInput(inputEvent);
            }

            ReinstallKeyboardHook();
        }
        if (deviceKind == RazerBladeInputDeviceKind.Rz09_0581
            && Rz09_0581PerformanceShortcutRawHidInput.TryCreateFunctionPTrigger(
            keyboard.MakeCode,
            keyboard.Flags,
            performanceFunctionKeyPressed,
            ref functionPPressed,
            out PerformanceShortcutSource shortcut))
        {
            publishPerformanceShortcut(shortcut);
        }
        if (SpecialKeyRawKeyboardState.TryCreateTrigger(
                deviceKind,
                keyboard.VirtualKey,
                keyDown,
                performanceFunctionKeyPressed,
                specialKeyPressed,
                out SpecialKeySlot specialKey)
            && readSpecialKeyInputAdmitted(specialKey)
            && ShouldOverrideSpecialKey(
                deviceKind,
                specialKey,
                shouldOverrideSpecialKey(specialKey)))
        {
            publishSpecialKey(specialKey);
        }
        // A pulse carries no model tag. Do not send another known laptop's
        // slot numbers into the currently verified model's renderer.
        if (deviceKind != readVerifiedInputDeviceKind())
        {
            return;
        }
        if (!ReactiveRawKeyboardInput.TryCreatePulses(
            deviceKind,
            readBlade18KeyboardLayoutId(),
            keyboard.MakeCode,
            keyboard.Flags,
            out ReactiveKeyPulse primary,
            out ReactiveKeyPulse? secondary))
        {
            return;
        }

        long observedTimestamp = Stopwatch.GetTimestamp();
        publish(WithObservedTimestamp(primary, observedTimestamp));
        if (secondary is { } secondPulse)
        {
            publish(WithObservedTimestamp(secondPulse, observedTimestamp));
        }
    }

    private void ProcessHid(
        nint buffer,
        uint headerSize,
        uint receivedSize,
        RazerBladeInputDeviceKind deviceKind,
        nint collection)
    {
        // Malformed target packets cancel held shortcuts before any action.
        bool blade18 = deviceKind == RazerBladeInputDeviceKind.Rz09_0484;
        bool blade18Admitted = ReadBlade18Admission();
        bool boundBlade18Collection = blade18 && blade18Fn.IsBoundCollection(collection);
        const int rawHidHeaderSize = sizeof(uint) * 2;
        if (receivedSize < headerSize + rawHidHeaderSize)
        {
            if (boundBlade18Collection)
            {
                blade18Fn.Invalidate();
                CancelBlade18Input();
            }
            return;
        }

        nint hid = nint.Add(buffer, checked((int)headerSize));
        uint reportSize = unchecked((uint)Marshal.ReadInt32(hid));
        uint reportCount = unchecked((uint)Marshal.ReadInt32(hid, sizeof(uint)));
        if (reportSize == 0 || reportCount == 0)
        {
            if (boundBlade18Collection)
            {
                blade18Fn.Invalidate();
                CancelBlade18Input();
            }
            return;
        }

        ulong rawBytes = (ulong)reportSize * reportCount;
        ulong requiredBytes = (ulong)headerSize + rawHidHeaderSize + rawBytes;
        if (rawBytes > MaximumRawInputBytes || requiredBytes > receivedSize)
        {
            if (boundBlade18Collection)
            {
                blade18Fn.Invalidate();
                CancelBlade18Input();
            }
            return;
        }

        nint reports = nint.Add(hid, rawHidHeaderSize);
        if (blade18 && reportSize != 16)
        {
            if (boundBlade18Collection)
            {
                blade18Fn.Invalidate();
                CancelBlade18Input();
            }
            return;
        }
        Span<byte> report = stackalloc byte[blade18 ? 16 : 3];
        report.Clear();
        int inspectedBytes = Math.Min(report.Length, checked((int)reportSize));
        for (uint index = 0; index < reportCount; index++)
        {
            nint reportAddress = nint.Add(
                reports,
                checked((int)(index * reportSize)));
            for (int offset = 0; offset < inspectedBytes; offset++)
            {
                report[offset] = Marshal.ReadByte(reportAddress, offset);
            }
            if (blade18)
            {
                ObserveBlade18FnReport(
                    blade18Fn,
                    deviceKind,
                    collection,
                    report,
                    blade18Admitted,
                    reportBlade18FnDiagnostic);
                if (blade18Fn.IsAmbiguous)
                {
                    CancelBlade18Input();
                }
                continue;
            }
            rz090528FunctionPInput.ObserveHid(
                deviceKind,
                report[..inspectedBytes],
                IsRz090528FunctionPAdmitted());
            bool specialTriggered = RazerBladeSpecialKeyRawHidInput.TryCreateTrigger(
                deviceKind,
                report[..inspectedBytes],
                ref performanceFunctionKeyPressed,
                specialKeyPressed,
                out SpecialKeySlot specialKey);
            bool inputAdmitted = specialTriggered
                && readSpecialKeyInputAdmitted(specialKey);
            bool overrideSpecialKey = inputAdmitted
                && specialTriggered
                && ShouldOverrideSpecialKey(
                    deviceKind,
                    specialKey,
                    shouldOverrideSpecialKey(specialKey));
            if (overrideSpecialKey)
            {
                publishSpecialKey(specialKey);
            }
            if (specialTriggered
                && specialKey == SpecialKeySlot.M4
                && overrideSpecialKey)
            {
                continue;
            }
            if (deviceKind == RazerBladeInputDeviceKind.Rz09_0528)
            {
                // PID 02C6 Fn+P is handled by its composite HID/keyboard
                // state above. Its M4 packet must not reach the 02E0 decoder.
                continue;
            }
            if (Rz09_0581DefaultLaptopKeyRawHidInput.TryCreateTrigger(
                report[..inspectedBytes],
                ref defaultGamingModePressed,
                ref defaultMicrophoneMutePressed,
                out SpecialKeySlot defaultLaptopKey))
            {
                publishDefaultLaptopKey(defaultLaptopKey);
            }
            if (Rz09_0581PerformanceShortcutRawHidInput.TryCreateTrigger(
                report[..inspectedBytes],
                ref performanceFunctionKeyPressed,
                ref dedicatedPerformanceButtonPressed,
                ref functionPPressed,
                out PerformanceShortcutSource shortcut))
            {
                publishPerformanceShortcut(shortcut);
            }
            // Fn-layer highlighting is composed by the keyboard firmware. The
            // PID 02E0 capture shows no changed host matrix frame on Fn make;
            // converting it to a single-slot Reactive pulse instead replaces
            // the firmware overlay with an incorrect host-rendered frame.
        }
    }

    private bool IsRz090528FunctionPAdmitted() =>
        readVerifiedInputDeviceKind() == RazerBladeInputDeviceKind.Rz09_0528
        && readDriverModeObservation().AllowsFunctionRowInterception;

    internal static bool ShouldOverrideSpecialKey(
        RazerBladeInputDeviceKind deviceKind,
        SpecialKeySlot specialKey,
        bool configuredOverride) =>
        configuredOverride
        || deviceKind == RazerBladeInputDeviceKind.Rz09_0528
        && specialKey == SpecialKeySlot.M4;

    private void ReinstallKeyboardHook()
    {
        nint replacement = SetWindowsHookEx(
            LowLevelKeyboardHook,
            KeyboardProcedureCallback,
            GetModuleHandle(null),
            0);
        if (replacement == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to restore the function-row keyboard hook.");
        }

        nint previous = Interlocked.Exchange(ref keyboardHook, replacement);
        hookHealth.Clear();
        blade18Correlation.Clear();
        if (previous != 0 && !UnhookWindowsHookEx(previous))
        {
            Trace.TraceWarning(
                "Superseded function-row keyboard hook cleanup failed: {0}",
                Marshal.GetLastWin32Error());
        }
        Trace.TraceWarning(
            "OpenBlade restored a missing function-row keyboard hook from target-device Raw Input.");
    }

    private static ReactiveKeyPulse WithObservedTimestamp(
        ReactiveKeyPulse pulse,
        long observedTimestamp) =>
        new(pulse.MatrixSlot, observedTimestamp);

    private bool TryGetInputDeviceKind(
        nint deviceHandle,
        out RazerBladeInputDeviceKind deviceKind)
    {
        if (inputDevices.TryGetValue(deviceHandle, out deviceKind))
        {
            return deviceKind != RazerBladeInputDeviceKind.Unsupported;
        }

        RazerBladeInputDeviceKind? resolved = TryResolveInputDevice(deviceHandle);
        if (resolved is not { } kind)
        {
            // A failed identity query is not evidence that the device is
            // unsupported. Retry on its next packet instead of permanently
            // caching a transient setup/resume failure.
            deviceKind = RazerBladeInputDeviceKind.Unsupported;
            return false;
        }

        inputDevices.TryAdd(deviceHandle, kind);
        deviceKind = kind;
        return kind != RazerBladeInputDeviceKind.Unsupported;
    }

    private static RazerBladeInputDeviceKind? TryResolveInputDevice(nint deviceHandle)
    {
        if (deviceHandle == 0)
        {
            return RazerBladeInputDeviceKind.Unsupported;
        }

        uint characterCount = 0;
        uint sizingResult = GetRawInputDeviceInfo(
            deviceHandle,
            RawInputDeviceName,
            null,
            ref characterCount);
        if (sizingResult == uint.MaxValue || characterCount == 0)
        {
            return null;
        }
        if (characterCount > 4096)
        {
            return RazerBladeInputDeviceKind.Unsupported;
        }

        var name = new StringBuilder(checked((int)characterCount));
        uint result = GetRawInputDeviceInfo(
            deviceHandle,
            RawInputDeviceName,
            name,
            ref characterCount);
        return result == uint.MaxValue
            ? null
            : RazerBladeReactiveInputDevice.ClassifyPath(name.ToString());
    }

    private static void RegisterRawInput(nint target, uint flags)
    {
        RawInputDevice[] devices =
        [
            new()
            {
                UsagePage = GenericDesktopUsagePage,
                Usage = PageOnlyUsage,
                Flags = flags | RawInputDevicePageOnly,
                Target = target,
            },
            new()
            {
                UsagePage = ConsumerUsagePage,
                Usage = PageOnlyUsage,
                Flags = flags | RawInputDevicePageOnly,
                Target = target,
            },
        ];
        if (!RegisterRawInputDevices(
            devices,
            checked((uint)devices.Length),
            checked((uint)Marshal.SizeOf<RawInputDevice>())))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to register keyboard input for Reactive lighting.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        internal ushort UsagePage;
        internal ushort Usage;
        internal uint Flags;
        internal nint Target;
    }

    internal delegate uint RawInputDeviceListReader(
        RawInputDeviceListEntry[]? devices, ref uint count, uint structureSize);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDeviceListEntry
    {
        internal nint Device;
        internal uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        internal uint Type;
        internal uint Size;
        internal nint Device;
        internal nuint WindowParameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        internal ushort MakeCode;
        internal ushort Flags;
        internal ushort Reserved;
        internal ushort VirtualKey;
        internal uint Message;
        internal uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardInput
    {
        internal uint VirtualKey;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        internal WindowProcedure Procedure;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint BackgroundBrush;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMessage
    {
        internal nint Window;
        internal uint Message;
        internal nuint WindowParameter;
        internal nint MessageParameter;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    private delegate nint WindowProcedure(
        nint window,
        uint message,
        nuint windowParameter,
        nint messageParameter);

    private delegate nint LowLevelKeyboardProcedure(
        int code,
        nuint windowParameter,
        nint messageParameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(
        nint window,
        uint message,
        nuint windowParameter,
        nint messageParameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        nuint windowParameter,
        nint messageParameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(
        out WindowMessage message,
        nint window,
        uint minimumMessage,
        uint maximumMessage);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref WindowMessage message);

    [DllImport("user32.dll")]
    private static extern int GetMessageTime();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref WindowMessage message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProcedure procedure,
        nint module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(
        nint hook,
        int code,
        nuint windowParameter,
        nint messageParameter);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(
        nint window,
        uint message,
        nuint windowParameter,
        nint messageParameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] rawInputDevices,
        uint deviceCount,
        uint structureSize);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        nint server, int sessionId, int informationClass, out nint buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint buffer);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(
        [Out] RawInputDeviceListEntry[]? devices, ref uint count, uint structureSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        nint rawInputHandle,
        uint command,
        nint data,
        ref uint size,
        uint headerSize);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetRawInputData",
        SetLastError = true)]
    private static extern uint GetRawInputHeader(
        nint rawInputHandle,
        uint command,
        ref RawInputHeader data,
        ref uint size,
        uint headerSize);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetRawInputDeviceInfoW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        nint device,
        uint command,
        StringBuilder? data,
        ref uint size);
}
