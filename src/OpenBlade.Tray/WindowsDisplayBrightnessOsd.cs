using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.Tray;

internal interface IDisplayBrightnessOsdPresenter : IDisposable
{
    void Show(byte brightness);
}

internal sealed class NullDisplayBrightnessOsd : IDisplayBrightnessOsdPresenter
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

/// <summary>
/// Shows a compact, non-activating Windows-style brightness flyout. Windows
/// does not expose its hardware brightness OSD to applications, so OpenBlade
/// owns this session-local presentation while WMI remains the documented
/// display-brightness control path.
/// </summary>
public sealed class WindowsDisplayBrightnessOsd : IDisplayBrightnessOsdPresenter
{
    private const uint ShowBrightnessMessage = 0x8001; // WM_APP + 1
    private const uint AdvanceSlideMessage = 0x8002; // WM_APP + 2
    private const uint WindowMessageClose = 0x0010;
    private const uint WindowMessageDestroy = 0x0002;
    private const uint WindowMessageEraseBackground = 0x0014;
    private const uint WindowMessagePaint = 0x000F;
    private const uint WindowMessageTimer = 0x0113;
    private const uint WindowMessageDpiChanged = 0x02E0;
    private const nuint HideTimerId = 1;
    internal const uint HideDelayMilliseconds = 2000;
    internal const double SlideDurationMilliseconds = 250;
    private const uint ExtendedStyle = 0x00000008 // WS_EX_TOPMOST
        | 0x00000020 // WS_EX_TRANSPARENT
        | 0x00000080 // WS_EX_TOOLWINDOW
        | 0x08000000 // WS_EX_NOACTIVATE
        | 0x00080000; // WS_EX_LAYERED
    private const uint PopupStyle = 0x80000000;
    private const uint LayeredAlpha = 0x00000002;
    private const uint MonitorDefaultToPrimary = 0x00000001;
    private const int EffectiveMonitorDpi = 0;
    private const uint SetWindowNoActivate = 0x0010;
    private const int ShowNoActivate = 4;
    private const int ShowHide = 0;
    private const int TransparentBackgroundMode = 1;
    private const int NullPen = 8;
    private const int HalftoneStretchMode = 4;
    private const int TrackSupersampleFactor = 4;
    private const uint DrawTextCenter = 0x00000001;
    private const uint DrawTextRight = 0x00000002;
    private const uint DrawTextVerticalCenter = 0x00000004;
    private const uint DrawTextSingleLine = 0x00000020;
    private const uint DrawTextNoPrefix = 0x00000800;
    private const uint SourceCopy = 0x00CC0020;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmRoundCorners = 2;
    private const int ColorHighlight = 13;
    private const string AccentPaletteRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private static readonly nint PerMonitorAwareV2 = new(-4);
    private static readonly ConcurrentDictionary<nint, WindowsDisplayBrightnessOsd>
        Instances = new();
    private static readonly WindowProcedure WindowProcedureCallback = ProcessWindowMessage;

    private readonly string windowClassName =
        $"OpenBlade.DisplayBrightnessOsd.{Guid.NewGuid():N}";
    private readonly Thread messageThread;
    private readonly TaskCompletionSource initialization =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<Exception>? errorHandler;
    private readonly object slideGate = new();
    private nint windowHandle;
    private int latestBrightness;
    private int disposed;
    private DisplayBrightnessOsdBounds displayedBounds;
    private nint taskbarHandle;
    private int hiddenY;
    private int currentY;
    private int slideStartY;
    private int slideEndY;
    private long slideStartedTimestamp;
    private int slideFramePending;
    private bool slideActive;
    private bool slideHidesWindow;
    private CancellationTokenSource? slideCancellation;

    private WindowsDisplayBrightnessOsd(Action<Exception>? errorHandler)
    {
        this.errorHandler = errorHandler;
        messageThread = new Thread(RunMessageWindow)
        {
            IsBackground = true,
            Name = "OpenBlade display brightness OSD",
        };
        messageThread.SetApartmentState(ApartmentState.STA);
        messageThread.Start();
        initialization.Task.GetAwaiter().GetResult();
    }

    internal static IDisplayBrightnessOsdPresenter CreateBestEffort(
        Action<Exception>? errorHandler = null)
    {
        try
        {
            return new WindowsDisplayBrightnessOsd(errorHandler);
        }
        catch (Exception exception)
        {
            Report(errorHandler, exception);
            return NullDisplayBrightnessOsd.Instance;
        }
    }

    public void Show(byte brightness)
    {
        if (brightness > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(brightness));
        }
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref latestBrightness, brightness);
        nint handle = Volatile.Read(ref windowHandle);
        if (handle == 0 || !PostMessage(handle, ShowBrightnessMessage, 0, 0))
        {
            Report(
                errorHandler,
                new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The display brightness OSD could not be updated."));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        nint handle = Volatile.Read(ref windowHandle);
        if (handle != 0)
        {
            _ = PostMessage(handle, WindowMessageClose, 0, 0);
        }
        if (Thread.CurrentThread != messageThread
            && !messageThread.Join(TimeSpan.FromSeconds(3)))
        {
            Trace.TraceWarning("Display brightness OSD did not stop promptly.");
        }
    }

    internal static DisplayBrightnessOsdBounds CalculateBounds(
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        uint dpi)
    {
        double scale = Math.Max(1d, dpi / 96d);
        int width = checked((int)Math.Round(194 * scale));
        int height = checked((int)Math.Round(49.5 * scale));
        int bottomMargin = checked((int)Math.Round(72 * scale));
        int x = workLeft + Math.Max(0, (workRight - workLeft - width) / 2);
        int y = Math.Max(workTop, workBottom - height - bottomMargin);
        return new DisplayBrightnessOsdBounds(x, y, width, height);
    }

    private void RunMessageWindow()
    {
        nint module = 0;
        nint previousDpiContext = 0;
        bool classRegistered = false;
        try
        {
            previousDpiContext = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            if (previousDpiContext == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to enable crisp per-monitor rendering for the brightness OSD.");
            }

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
                    "Unable to register the display brightness OSD.");
            }
            classRegistered = true;

            nint created = CreateWindowEx(
                ExtendedStyle,
                windowClassName,
                "OpenBlade brightness",
                PopupStyle,
                0,
                0,
                194,
                50,
                0,
                0,
                module,
                0);
            if (created == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to create the display brightness OSD.");
            }

            windowHandle = created;
            Instances[created] = this;
            ApplyWindowStyle(created);
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
            Report(errorHandler, exception);
        }
        finally
        {
            nint handle = Interlocked.Exchange(ref windowHandle, 0);
            if (handle != 0)
            {
                Instances.TryRemove(handle, out _);
                _ = DestroyWindow(handle);
            }
            if (classRegistered)
            {
                _ = UnregisterClass(windowClassName, module);
            }
            if (previousDpiContext != 0)
            {
                _ = SetThreadDpiAwarenessContext(previousDpiContext);
            }

            initialization.TrySetException(
                new InvalidOperationException(
                    "The display brightness OSD stopped during initialization."));
        }
    }

    private nint ProcessMessage(
        nint window,
        uint message,
        nuint windowParameter,
        nint messageParameter)
    {
        switch (message)
        {
            case ShowBrightnessMessage:
                _ = InvalidateRect(window, 0, false);
                PositionAndShow(window);
                _ = KillTimer(window, HideTimerId);
                _ = SetTimer(window, HideTimerId, HideDelayMilliseconds, 0);
                return 0;
            case AdvanceSlideMessage:
                Interlocked.Exchange(ref slideFramePending, 0);
                AdvanceSlideAnimation(window);
                return 0;
            case WindowMessageTimer when windowParameter == HideTimerId:
                _ = KillTimer(window, HideTimerId);
                StartHideAnimation(window);
                return 0;
            case WindowMessagePaint:
                Paint(window);
                return 0;
            case WindowMessageDpiChanged:
                ApplyDpiChange(
                    window,
                    checked((uint)(windowParameter & 0xFFFF)));
                return 0;
            case WindowMessageEraseBackground:
                return 1;
            case WindowMessageClose:
                _ = DestroyWindow(window);
                return 0;
            case WindowMessageDestroy:
                _ = KillTimer(window, HideTimerId);
                CancelSlideAnimation();
                Instances.TryRemove(window, out _);
                windowHandle = 0;
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProc(window, message, windowParameter, messageParameter);
        }
    }

    private void PositionAndShow(nint window)
    {
        nint foreground = GetForegroundWindow();
        nint monitor = MonitorFromWindow(foreground, MonitorDefaultToPrimary);
        var monitorInfo = new MonitorInformation
        {
            Size = checked((uint)Marshal.SizeOf<MonitorInformation>()),
        };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The active monitor could not be located for the brightness OSD.");
        }

        uint dpi = GetEffectiveMonitorDpi(monitor, window);
        DisplayBrightnessOsdBounds bounds = CalculateBounds(
            monitorInfo.WorkArea.Left,
            monitorInfo.WorkArea.Top,
            monitorInfo.WorkArea.Right,
            monitorInfo.WorkArea.Bottom,
            dpi == 0 ? 96u : dpi);
        ApplyCornerRegion(window, bounds, dpi);
        bool wasVisible = IsWindowVisible(window);
        displayedBounds = bounds;
        taskbarHandle = FindTaskbarForMonitor(monitor);
        hiddenY = monitorInfo.WorkArea.Bottom;
        if (!wasVisible)
        {
            Volatile.Write(ref currentY, hiddenY);
            PositionWindow(window, bounds, taskbarHandle, hiddenY);
            _ = UpdateWindow(window);
            _ = ShowWindow(window, ShowNoActivate);
            StartSlideAnimation(
                window,
                bounds,
                taskbarHandle,
                hiddenY,
                bounds.Y,
                hidesWindow: false);
            return;
        }

        if (Volatile.Read(ref slideActive))
        {
            int latestY = Volatile.Read(ref currentY);
            PositionWindow(window, bounds, taskbarHandle, latestY);
            if (Volatile.Read(ref slideHidesWindow))
            {
                StartSlideAnimation(
                    window,
                    bounds,
                    taskbarHandle,
                    latestY,
                    bounds.Y,
                    hidesWindow: false);
            }
            return;
        }

        Volatile.Write(ref currentY, bounds.Y);
        PositionWindow(window, bounds, taskbarHandle, bounds.Y);
        _ = ShowWindow(window, ShowNoActivate);
    }

    private void ApplyDpiChange(nint window, uint dpi)
    {
        nint monitor = MonitorFromWindow(window, MonitorDefaultToPrimary);
        var monitorInfo = new MonitorInformation
        {
            Size = checked((uint)Marshal.SizeOf<MonitorInformation>()),
        };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        uint effectiveDpi = dpi == 0
            ? GetEffectiveMonitorDpi(monitor, window)
            : dpi;
        DisplayBrightnessOsdBounds bounds = CalculateBounds(
            monitorInfo.WorkArea.Left,
            monitorInfo.WorkArea.Top,
            monitorInfo.WorkArea.Right,
            monitorInfo.WorkArea.Bottom,
            effectiveDpi == 0 ? 96u : effectiveDpi);
        CancelSlideAnimation();
        displayedBounds = bounds;
        taskbarHandle = FindTaskbarForMonitor(monitor);
        hiddenY = monitorInfo.WorkArea.Bottom;
        int y = IsWindowVisible(window)
            ? bounds.Y
            : hiddenY;
        Volatile.Write(ref currentY, y);
        ApplyCornerRegion(window, bounds, effectiveDpi);
        PositionWindow(window, bounds, taskbarHandle, y);
        _ = InvalidateRect(window, 0, false);
    }

    private static uint GetEffectiveMonitorDpi(nint monitor, nint window)
    {
        if (GetDpiForMonitor(
                monitor,
                EffectiveMonitorDpi,
                out uint dpiX,
                out _) >= 0
            && dpiX > 0)
        {
            return dpiX;
        }

        uint windowDpi = GetDpiForWindow(window);
        return windowDpi == 0 ? 96u : windowDpi;
    }

    private static void ApplyCornerRegion(
        nint window,
        DisplayBrightnessOsdBounds bounds,
        uint dpi)
    {
        int cornerDiameter = Math.Max(
            10,
            checked((int)Math.Round(
                14 * Math.Max(1d, dpi / 96d))));
        nint region = CreateRoundRectRgn(
            0,
            0,
            bounds.Width + 1,
            bounds.Height + 1,
            cornerDiameter,
            cornerDiameter);
        if (region != 0 && !SetWindowRgn(window, region, true))
        {
            _ = DeleteObject(region);
        }
    }

    private void StartHideAnimation(nint window)
    {
        if (!IsWindowVisible(window))
        {
            return;
        }

        StartSlideAnimation(
            window,
            displayedBounds,
            taskbarHandle,
            Volatile.Read(ref currentY),
            hiddenY,
            hidesWindow: true);
    }

    private void StartSlideAnimation(
        nint window,
        DisplayBrightnessOsdBounds bounds,
        nint taskbar,
        int startY,
        int endY,
        bool hidesWindow)
    {
        CancelSlideAnimation();
        if (startY == endY)
        {
            Volatile.Write(ref currentY, endY);
            PositionWindow(window, bounds, taskbar, endY);
            if (hidesWindow)
            {
                _ = ShowWindow(window, ShowHide);
            }
            return;
        }

        var cancellation = new CancellationTokenSource();
        lock (slideGate)
        {
            slideCancellation = cancellation;
            slideStartY = startY;
            slideEndY = endY;
            slideStartedTimestamp = Stopwatch.GetTimestamp();
            slideActive = true;
            slideHidesWindow = hidesWindow;
        }
        CancellationToken token = cancellation.Token;
        _ = Task.Run(
            () => RunSlideFrameClock(window, token));
    }

    private void RunSlideFrameClock(nint window, CancellationToken token)
    {
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // DwmFlush blocks until the compositor presents the frame,
                // then the owning window thread applies that frame. Keeping
                // SetWindowPos on the owner prevents DWM from coalescing the
                // entire animation into its final position.
                if (DwmFlush() < 0)
                {
                    Thread.Sleep(1);
                }
                token.ThrowIfCancellationRequested();
                if (Interlocked.Exchange(ref slideFramePending, 1) == 0
                    && !PostMessage(window, AdvanceSlideMessage, 0, 0))
                {
                    Interlocked.Exchange(ref slideFramePending, 0);
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The brightness OSD animation frame could not be queued.");
                }

                if (Stopwatch.GetElapsedTime(slideStartedTimestamp)
                    .TotalMilliseconds >= SlideDurationMilliseconds)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Report(errorHandler, exception);
        }
    }

    private void AdvanceSlideAnimation(nint window)
    {
        CancellationTokenSource? cancellation;
        int startY;
        int endY;
        long started;
        bool hidesWindow;
        lock (slideGate)
        {
            if (!slideActive || slideCancellation is null)
            {
                return;
            }

            cancellation = slideCancellation;
            startY = slideStartY;
            endY = slideEndY;
            started = slideStartedTimestamp;
            hidesWindow = slideHidesWindow;
        }

        double elapsedMilliseconds =
            Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        double progress = Math.Clamp(
            elapsedMilliseconds / SlideDurationMilliseconds,
            0,
            1);
        double easedProgress = hidesWindow
            ? progress == 0
                ? 0
                : Math.Pow(2, 10 * progress - 10)
            : progress == 1
                ? 1
                : 1 - Math.Pow(2, -10 * progress);
        int y = checked((int)Math.Round(
            startY + (endY - startY) * easedProgress));
        PositionWindow(window, displayedBounds, taskbarHandle, y);
        Volatile.Write(ref currentY, y);

        if (progress >= 1)
        {
            CompleteSlideAnimation(
                window,
                endY,
                hidesWindow,
                cancellation);
        }
    }

    private void CompleteSlideAnimation(
        nint window,
        int endY,
        bool hidesWindow,
        CancellationTokenSource cancellation)
    {
        lock (slideGate)
        {
            if (!ReferenceEquals(slideCancellation, cancellation))
            {
                return;
            }
            slideCancellation = null;
            slideActive = false;
            slideHidesWindow = false;
        }
        cancellation.Dispose();
        Volatile.Write(ref currentY, endY);
        if (hidesWindow)
        {
            _ = ShowWindow(window, ShowHide);
        }
    }

    private void CancelSlideAnimation()
    {
        CancellationTokenSource? cancellation;
        lock (slideGate)
        {
            cancellation = slideCancellation;
            slideCancellation = null;
            slideActive = false;
            slideHidesWindow = false;
        }
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private static void PositionWindow(
        nint window,
        DisplayBrightnessOsdBounds bounds,
        nint taskbar,
        int y)
    {
        nint insertAfter = taskbar == 0
            ? new nint(-1) // HWND_TOPMOST
            : taskbar;
        if (!SetWindowPos(
            window,
            insertAfter,
            bounds.X,
            y,
            bounds.Width,
            bounds.Height,
            SetWindowNoActivate))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The brightness OSD could not be positioned.");
        }
    }

    private static nint FindTaskbarForMonitor(nint monitor)
    {
        nint match = 0;
        _ = EnumWindows(
            (candidate, _) =>
            {
                var className = new StringBuilder(64);
                if (GetClassName(candidate, className, className.Capacity) == 0)
                {
                    return true;
                }

                string value = className.ToString();
                if ((string.Equals(
                        value,
                        "Shell_TrayWnd",
                        StringComparison.Ordinal)
                    || string.Equals(
                        value,
                        "Shell_SecondaryTrayWnd",
                        StringComparison.Ordinal))
                    && MonitorFromWindow(candidate, 0) == monitor)
                {
                    match = candidate;
                    return false;
                }

                return true;
            },
            0);
        return match;
    }

    private void Paint(nint window)
    {
        nint target = BeginPaint(window, out PaintStructure paint);
        if (target == 0)
        {
            return;
        }

        nint buffer = 0;
        nint bitmap = 0;
        nint previousBitmap = 0;
        try
        {
            if (!GetClientRect(window, out Rectangle client))
            {
                return;
            }

            buffer = CreateCompatibleDC(target);
            bitmap = CreateCompatibleBitmap(
                target,
                client.Right - client.Left,
                client.Bottom - client.Top);
            if (buffer == 0 || bitmap == 0)
            {
                return;
            }
            previousBitmap = SelectObject(buffer, bitmap);

            Fill(buffer, client, ColorRef(50, 48, 49));
            uint dpi = GetDpiForWindow(window);
            DrawBrightnessContent(
                buffer,
                client,
                checked((byte)Volatile.Read(ref latestBrightness)),
                dpi == 0 ? 96u : dpi);
            _ = BitBlt(
                target,
                0,
                0,
                client.Right,
                client.Bottom,
                buffer,
                0,
                0,
                SourceCopy);
        }
        finally
        {
            if (buffer != 0 && previousBitmap != 0)
            {
                _ = SelectObject(buffer, previousBitmap);
            }
            if (bitmap != 0)
            {
                _ = DeleteObject(bitmap);
            }
            if (buffer != 0)
            {
                _ = DeleteDC(buffer);
            }
            _ = EndPaint(window, ref paint);
        }
    }

    private static void DrawBrightnessContent(
        nint deviceContext,
        Rectangle client,
        byte brightness,
        uint dpi)
    {
        double scale = Math.Max(1d, dpi / 96d);
        int iconLeft = checked((int)Math.Round(8 * scale));
        int iconRight = checked((int)Math.Round(37 * scale));
        int trackLeft = checked((int)Math.Round(43.5 * scale));
        int trackRight = checked((int)Math.Round(153.5 * scale));
        int trackTop = checked((int)Math.Round(22 * scale));
        int trackHeight = Math.Max(
            4,
            checked((int)Math.Round(4 * scale)));

        nint iconFont = CreateFont(
            -checked((int)Math.Round(17 * scale)),
            0,
            0,
            0,
            400,
            0,
            0,
            0,
            1,
            0,
            0,
            4,
            0,
            "Segoe Fluent Icons");
        nint previousFont = iconFont == 0
            ? 0
            : SelectObject(deviceContext, iconFont);
        _ = SetBkMode(deviceContext, TransparentBackgroundMode);
        _ = SetTextColor(deviceContext, ColorRef(255, 255, 255));
        int iconVerticalOffset = Math.Max(
            1,
            checked((int)Math.Round(0.5 * scale)));
        var iconBounds = new Rectangle(
            iconLeft,
            -iconVerticalOffset,
            iconRight,
            client.Bottom - iconVerticalOffset);
        _ = DrawText(
            deviceContext,
            "\uE706",
            -1,
            ref iconBounds,
            DrawTextCenter
                | DrawTextVerticalCenter
                | DrawTextSingleLine
                | DrawTextNoPrefix);

        if (previousFont != 0)
        {
            _ = SelectObject(deviceContext, previousFont);
        }
        if (iconFont != 0)
        {
            _ = DeleteObject(iconFont);
        }

        int trackWidth = trackRight - trackLeft;
        int progress = checked((int)Math.Round(trackWidth * brightness / 100d));
        DrawProgressTrack(
            deviceContext,
            trackLeft,
            trackTop,
            trackRight,
            trackTop + trackHeight,
            progress,
            ColorRef(159, 159, 159),
            GetFlyoutAccentColor());

        nint valueFont = CreateFont(
            -checked((int)Math.Round(14 * scale)),
            0,
            0,
            0,
            400,
            0,
            0,
            0,
            1,
            0,
            0,
            4,
            0,
            "Segoe UI Variable Text");
        nint previousValueFont = valueFont == 0
            ? 0
            : SelectObject(deviceContext, valueFont);
        int valueVerticalOffset = Math.Max(
            1,
            checked((int)Math.Round(2 * scale)));
        var valueBounds = new Rectangle(
            checked((int)Math.Round(155 * scale)),
            -valueVerticalOffset,
            checked((int)Math.Round(180.5 * scale)),
            client.Bottom - valueVerticalOffset);
        _ = DrawText(
            deviceContext,
            brightness.ToString(System.Globalization.CultureInfo.InvariantCulture),
            -1,
            ref valueBounds,
            DrawTextRight
                | DrawTextVerticalCenter
                | DrawTextSingleLine
                | DrawTextNoPrefix);
        if (previousValueFont != 0)
        {
            _ = SelectObject(deviceContext, previousValueFont);
        }
        if (valueFont != 0)
        {
            _ = DeleteObject(valueFont);
        }
    }

    private static void DrawProgressTrack(
        nint deviceContext,
        int left,
        int top,
        int right,
        int bottom,
        int progress,
        uint inactiveColor,
        uint activeColor)
    {
        int width = right - left;
        int height = bottom - top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int scaledWidth = checked(width * TrackSupersampleFactor);
        int scaledHeight = checked(height * TrackSupersampleFactor);
        nint buffer = CreateCompatibleDC(deviceContext);
        nint bitmap = buffer == 0
            ? 0
            : CreateCompatibleBitmap(deviceContext, scaledWidth, scaledHeight);
        nint previousBitmap = bitmap == 0
            ? 0
            : SelectObject(buffer, bitmap);
        try
        {
            if (buffer == 0 || bitmap == 0 || previousBitmap == 0)
            {
                DrawRoundedBarCore(
                    deviceContext,
                    left,
                    top,
                    right,
                    bottom,
                    inactiveColor);
                if (progress > 0)
                {
                    DrawRoundedBarCore(
                        deviceContext,
                        left,
                        top,
                        left + Math.Max(height, progress),
                        bottom,
                        activeColor);
                }
                return;
            }

            var scaledBounds = new Rectangle(0, 0, scaledWidth, scaledHeight);
            Fill(buffer, scaledBounds, ColorRef(50, 48, 49));
            DrawRoundedBarCore(
                buffer,
                0,
                0,
                scaledWidth,
                scaledHeight,
                inactiveColor);
            if (progress > 0)
            {
                int scaledProgress = checked(progress * TrackSupersampleFactor);
                DrawRoundedBarCore(
                    buffer,
                    0,
                    0,
                    Math.Max(scaledHeight, scaledProgress),
                    scaledHeight,
                    activeColor);
            }

            _ = SetStretchBltMode(deviceContext, HalftoneStretchMode);
            _ = SetBrushOrgEx(deviceContext, 0, 0, out _);
            _ = StretchBlt(
                deviceContext,
                left,
                top,
                width,
                height,
                buffer,
                0,
                0,
                scaledWidth,
                scaledHeight,
                SourceCopy);
        }
        finally
        {
            if (buffer != 0 && previousBitmap != 0)
            {
                _ = SelectObject(buffer, previousBitmap);
            }
            if (bitmap != 0)
            {
                _ = DeleteObject(bitmap);
            }
            if (buffer != 0)
            {
                _ = DeleteDC(buffer);
            }
        }
    }

    private static void DrawRoundedBarCore(
        nint deviceContext,
        int left,
        int top,
        int right,
        int bottom,
        uint color)
    {
        nint brush = CreateSolidBrush(color);
        nint oldBrush = SelectObject(deviceContext, brush);
        nint oldPen = SelectObject(deviceContext, GetStockObject(NullPen));
        int radius = Math.Max(2, bottom - top);
        _ = RoundRect(
            deviceContext,
            left,
            top,
            right,
            bottom,
            radius,
            radius);
        _ = SelectObject(deviceContext, oldPen);
        _ = SelectObject(deviceContext, oldBrush);
        _ = DeleteObject(brush);
    }

    private static void Fill(nint deviceContext, Rectangle rectangle, uint color)
    {
        nint brush = CreateSolidBrush(color);
        _ = FillRect(deviceContext, ref rectangle, brush);
        _ = DeleteObject(brush);
    }

    private static void ApplyWindowStyle(nint window)
    {
        int enabled = 1;
        _ = DwmSetWindowAttribute(
            window,
            DwmUseImmersiveDarkMode,
            ref enabled,
            sizeof(int));
        int corners = DwmRoundCorners;
        _ = DwmSetWindowAttribute(
            window,
            DwmWindowCornerPreference,
            ref corners,
            sizeof(int));
        if (!SetLayeredWindowAttributes(window, 0, 255, LayeredAlpha))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The display brightness OSD opacity could not be configured.");
        }
    }

    private static nint ProcessWindowMessage(
        nint window,
        uint message,
        nuint windowParameter,
        nint messageParameter)
    {
        if (!Instances.TryGetValue(window, out WindowsDisplayBrightnessOsd? osd))
        {
            return DefWindowProc(window, message, windowParameter, messageParameter);
        }

        try
        {
            return osd.ProcessMessage(
                window,
                message,
                windowParameter,
                messageParameter);
        }
        catch (Exception exception)
        {
            Report(osd.errorHandler, exception);
            return DefWindowProc(window, message, windowParameter, messageParameter);
        }
    }

    private static uint ColorRef(byte red, byte green, byte blue) =>
        (uint)(red | green << 8 | blue << 16);

    internal static uint BlendWithWhite(uint color, int whitePercent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(whitePercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(whitePercent, 100);
        int colorPercent = 100 - whitePercent;
        byte red = checked((byte)(
            ((color & 0xFF) * colorPercent + 255 * whitePercent) / 100));
        byte green = checked((byte)(
            (((color >> 8) & 0xFF) * colorPercent + 255 * whitePercent) / 100));
        byte blue = checked((byte)(
            (((color >> 16) & 0xFF) * colorPercent + 255 * whitePercent) / 100));
        return ColorRef(red, green, blue);
    }

    internal static uint ResolveFlyoutAccentColor(
        byte[]? accentPalette,
        uint fallbackHighlight)
    {
        if (accentPalette is { Length: >= 7 })
        {
            // Windows' flyouts use AccentLight2, stored as the second RGBA
            // entry in the current user's accent palette.
            return ColorRef(
                accentPalette[4],
                accentPalette[5],
                accentPalette[6]);
        }

        return BlendWithWhite(fallbackHighlight, whitePercent: 70);
    }

    private static uint GetFlyoutAccentColor()
    {
        uint fallbackHighlight = GetSysColor(ColorHighlight);
        return ResolveFlyoutAccentColor(
            WindowsRegistryReader.ReadCurrentUserBinary(
                AccentPaletteRegistryPath,
                "AccentPalette"),
            fallbackHighlight);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal Rectangle(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInformation
    {
        internal uint Size;
        internal Rectangle Monitor;
        internal Rectangle WorkArea;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStructure
    {
        internal nint DeviceContext;
        internal int Erase;
        internal Rectangle Paint;
        internal int Restore;
        internal int Update;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        internal byte[] Reserved;
    }

    private delegate nint WindowProcedure(
        nint window,
        uint message,
        nuint windowParameter,
        nint messageParameter);

    private delegate bool EnumWindowsProcedure(
        nint window,
        nint parameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);

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
    private static extern nint DefWindowProc(
        nint window,
        uint message,
        nuint windowParameter,
        nint messageParameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowRgn(
        nint window,
        nint region,
        bool redraw);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(
        EnumWindowsProcedure callback,
        nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        nint window,
        StringBuilder className,
        int maximumCount);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(
        nint monitor,
        ref MonitorInformation monitorInformation);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(
        nint window,
        uint colorKey,
        byte alpha,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nuint SetTimer(
        nint window,
        nuint timerId,
        uint intervalMilliseconds,
        nint timerProcedure);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(nint window, nuint timerId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InvalidateRect(
        nint window,
        nint rectangle,
        bool erase);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(
        nint window,
        out PaintStructure paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(
        nint window,
        ref PaintStructure paint);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(
        nint window,
        out Rectangle rectangle);

    [DllImport("user32.dll")]
    private static extern int FillRect(
        nint deviceContext,
        ref Rectangle rectangle,
        nint brush);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawText(
        nint deviceContext,
        string text,
        int count,
        ref Rectangle rectangle,
        uint format);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(
        nint deviceContext,
        int width,
        int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint deviceContext, nint value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int ellipseWidth,
        int ellipseHeight);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int objectIndex);

    [DllImport("gdi32.dll")]
    private static extern bool RoundRect(
        nint deviceContext,
        int left,
        int top,
        int right,
        int bottom,
        int ellipseWidth,
        int ellipseHeight);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFont(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint characterSet,
        uint outputPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint deviceContext, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(nint deviceContext, uint color);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        nint destination,
        int xDestination,
        int yDestination,
        int width,
        int height,
        nint source,
        int xSource,
        int ySource,
        uint rasterOperation);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(
        nint deviceContext,
        int stretchMode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(
        nint deviceContext,
        int x,
        int y,
        out Point oldPoint);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(
        nint destination,
        int xDestination,
        int yDestination,
        int widthDestination,
        int heightDestination,
        nint source,
        int xSource,
        int ySource,
        int widthSource,
        int heightSource,
        uint rasterOperation);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}

internal readonly record struct DisplayBrightnessOsdBounds(
    int X,
    int Y,
    int Width,
    int Height);
