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
    private const ushort GenericDesktopUsagePage = 0x0001;
    private const ushort ConsumerUsagePage = 0x000C;
    private const ushort PageOnlyUsage = 0x0000;
    private static readonly nint MessageOnlyWindow = new(-3);
    private static readonly long HookObservationMaximumAge =
        Math.Max(1, Stopwatch.Frequency / 2);
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
    private readonly Action<FunctionRowAction> publishFunctionRowAction;
    private readonly Func<FunctionKeyPrimaryMode?> readFunctionKeyPrimaryMode;
    private readonly Func<KeyboardDriverModeObservation> readDriverModeObservation;
    private readonly Func<GamingModeKeyPolicy> readGamingModeKeyPolicy;
    private readonly Func<RazerBladeInputDeviceKind> readVerifiedInputDeviceKind;
    private readonly Func<SpecialKeySlot, bool> readSpecialKeyInputAdmitted;
    private readonly Rz09_0581FunctionRowInputState functionRowInput = new();
    private readonly GamingModeKeyBlockState gamingModeKeyBlockState = new();
    private readonly LowLevelKeyboardHookHealthTracker hookHealth = new();
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
        publishFunctionRowAction = options.PublishFunctionRowAction
            ?? throw new ArgumentNullException(nameof(options.PublishFunctionRowAction));
        readFunctionKeyPrimaryMode = options.ReadFunctionKeyPrimaryMode
            ?? throw new ArgumentNullException(nameof(options.ReadFunctionKeyPrimaryMode));
        readDriverModeObservation = options.ReadDriverModeObservation
            ?? throw new ArgumentNullException(nameof(options.ReadDriverModeObservation));
        readGamingModeKeyPolicy = options.ReadGamingModeKeyPolicy
            ?? throw new ArgumentNullException(nameof(options.ReadGamingModeKeyPolicy));
        readVerifiedInputDeviceKind = options.ReadVerifiedInputDeviceKind
            ?? throw new ArgumentNullException(nameof(options.ReadVerifiedInputDeviceKind));
        readSpecialKeyInputAdmitted = options.IsSpecialKeyInputAdmitted
            ?? throw new ArgumentNullException(nameof(options.IsSpecialKeyInputAdmitted));
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
                source.ProcessRawInput(messageParameter);
            }
            catch (Exception exception)
            {
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
                owner.hookHealth.Record(
                    input.ScanCode,
                    keyDown,
                    Stopwatch.GetTimestamp());
                if (owner.gamingModeKeyBlockState.ShouldSuppress(
                    input.VirtualKey,
                    keyDown,
                    owner.readGamingModeKeyPolicy(),
                    altContext: (input.Flags & LowLevelAltDownFlag) != 0))
                {
                    return 1;
                }
                if (owner.readVerifiedInputDeviceKind()
                        == RazerBladeInputDeviceKind.Rz09_0581
                    && owner.functionRowInput.Process(
                    input.ScanCode,
                    keyDown,
                    owner.performanceFunctionKeyPressed,
                    owner.readFunctionKeyPrimaryMode(),
                    owner.readDriverModeObservation().AllowsFunctionRowInterception,
                    out FunctionRowAction? action))
                {
                    if (action is { } triggered)
                    {
                        owner.publishFunctionRowAction(triggered);
                    }
                    return 1;
                }
            }
        }

        return CallNextHookEx(0, code, windowParameter, messageParameter);
    }

    private void ProcessRawInput(nint rawInputHandle)
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
            || header.Size > MaximumRawInputBytes
            || !TryGetInputDeviceKind(header.Device, out RazerBladeInputDeviceKind deviceKind))
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
            return;
        }

        RawInputHeader receivedHeader = Marshal.PtrToStructure<RawInputHeader>(buffer);
        if (receivedHeader.Size < headerSize
            || receivedHeader.Size > receivedSize
            || receivedHeader.Device != header.Device)
        {
            return;
        }

        if (receivedHeader.Type == RawInputTypeKeyboard)
        {
            ProcessKeyboard(buffer, headerSize, receivedSize, deviceKind);
        }
        else if (receivedHeader.Type == RawInputTypeHid)
        {
            ProcessHid(buffer, headerSize, receivedSize, deviceKind);
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
        RazerBladeInputDeviceKind deviceKind)
    {
        int keyboardSize = Marshal.SizeOf<RawKeyboard>();
        if (receivedSize < headerSize + keyboardSize)
        {
            return;
        }

        RawKeyboard keyboard = Marshal.PtrToStructure<RawKeyboard>(
            nint.Add(buffer, checked((int)headerSize)));
        bool keyDown = (keyboard.Flags & RawKeyboardBreakFlag) == 0;

        bool observedByHook = hookHealth.Consume(
            keyboard.MakeCode,
            keyDown,
            Stopwatch.GetTimestamp(),
            HookObservationMaximumAge);
        if (deviceKind == RazerBladeInputDeviceKind.Rz09_0581
            && !observedByHook
            && Rz09_0581FunctionRowMediaInput.TryOverride(
                keyboard.MakeCode,
                keyDown,
                performanceFunctionKeyPressed,
                readFunctionKeyPrimaryMode(),
                readDriverModeObservation().AllowsFunctionRowInterception,
                out FunctionRowAction? recoveredAction))
        {
            if (recoveredAction is { } action)
            {
                publishFunctionRowAction(action);
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
        if (!ReactiveRawKeyboardInput.TryCreatePulses(
            deviceKind,
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
        RazerBladeInputDeviceKind deviceKind)
    {
        const int rawHidHeaderSize = sizeof(uint) * 2;
        if (receivedSize < headerSize + rawHidHeaderSize)
        {
            return;
        }

        nint hid = nint.Add(buffer, checked((int)headerSize));
        uint reportSize = unchecked((uint)Marshal.ReadInt32(hid));
        uint reportCount = unchecked((uint)Marshal.ReadInt32(hid, sizeof(uint)));
        if (reportSize == 0 || reportCount == 0)
        {
            return;
        }

        ulong rawBytes = (ulong)reportSize * reportCount;
        ulong requiredBytes = (ulong)headerSize + rawHidHeaderSize + rawBytes;
        if (rawBytes > MaximumRawInputBytes || requiredBytes > receivedSize)
        {
            return;
        }

        nint reports = nint.Add(hid, rawHidHeaderSize);
        Span<byte> report = stackalloc byte[3];
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
                // The USB capture preserves Fn+P's report-ID-01 usage, but
                // Windows Raw Input exposes only the same generic Fn pair as
                // M1 and M2. Do not infer or publish a shortcut from that pair.
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

    internal static bool ShouldOverrideSpecialKey(
        RazerBladeInputDeviceKind deviceKind,
        SpecialKeySlot specialKey,
        bool configuredOverride) =>
        configuredOverride
        || deviceKind == RazerBladeInputDeviceKind.Rz09_0528
        && specialKey == SpecialKeySlot.M4;

    private void ReinstallKeyboardHook()
    {
        nint previous = Interlocked.Exchange(ref keyboardHook, 0);
        if (previous != 0)
        {
            _ = UnhookWindowsHookEx(previous);
        }

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

        hookHealth.Clear();
        keyboardHook = replacement;
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
