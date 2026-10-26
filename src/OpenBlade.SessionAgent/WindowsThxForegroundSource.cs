using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

/// <summary>
/// WinEvent callbacks publish only window handles to a capacity-one channel.
/// Executable resolution runs off the callback and never inspects window text,
/// audio sessions, or unrelated processes. Unresolved/hosted apps remain unknown.
/// </summary>
internal sealed class WindowsThxForegroundSource : IThxForegroundSource
{
    public async IAsyncEnumerable<ThxForegroundObservation> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var updates = Channel.CreateBounded<nint>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        using var subscription = new Subscription(updates.Writer);
        await subscription.Started.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
        using var outputSubscription = WindowsThxActiveOutput.Subscribe(
            () => updates.Writer.TryWrite(GetForegroundWindow()));
        await foreach (nint window in updates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            ThxForegroundObservation observation = await Task.Run(
                () => Resolve(window), cancellationToken).ConfigureAwait(false);
            // A newer foreground notification supersedes this lookup. Do not
            // briefly restore the general profile for an obsolete window.
            if (updates.Reader.TryPeek(out _))
            {
                continue;
            }
            yield return observation;
        }
    }

    private static ThxForegroundObservation Resolve(nint window)
    {
        try
        {
            if (!IsActiveInputSession() || window == 0 || GetForegroundWindow() != window
                || GetWindowThreadProcessId(window, out uint processId) == 0
                || !ProcessIdToSessionId(processId, out uint foregroundSession)
                || !ProcessIdToSessionId((uint)Environment.ProcessId, out uint ownSession)
                || foregroundSession != ownSession)
            {
                return ThxForegroundObservation.Unknown;
            }
            using SafeProcessHandle process = OpenProcess(0x1000, false, processId);
            if (process.IsInvalid)
            {
                return ThxForegroundObservation.Unknown;
            }
            var path = new StringBuilder(32768);
            uint length = (uint)path.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, path, ref length)
                || GetForegroundWindow() != window || !IsActiveInputSession())
            {
                return ThxForegroundObservation.Unknown;
            }
            string executablePath = ApplicationProfileDefinition.NormalizeExecutablePath(
                path.ToString());
            string name = Path.GetFileName(executablePath);
            // ApplicationFrameHost does not identify the hosted application.
            // No package identity is inferred from its process or window title.
            return string.IsNullOrWhiteSpace(name)
                || name.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
                ? ThxForegroundObservation.Unknown
                : new ThxForegroundObservation(
                    name,
                    WindowsThxActiveOutput.Read(),
                    executablePath,
                    checked((int)processId));
        }
        catch
        {
            return ThxForegroundObservation.Unknown;
        }
    }

    internal static bool IsActiveInputSession()
    {
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out uint session) || session == 0
            || !WTSQuerySessionInformationW(0, session, 8, out nint buffer, out uint size))
        {
            return false;
        }
        try
        {
            if (size < sizeof(int) || Marshal.ReadInt32(buffer) != 0)
            {
                return false;
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
        nint desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0)
        {
            return false;
        }
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformationW(desktop, 2, name,
                    (uint)(name.Capacity * sizeof(char)), out _)
                && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _ = CloseDesktop(desktop);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ChannelWriter<nint> writer;
        private readonly WinEventCallback callback;
        private readonly Thread thread;
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private uint threadId;
        private int disposed;

        internal Subscription(ChannelWriter<nint> writer)
        {
            this.writer = writer;
            callback = OnEvent;
            thread = new Thread(Run) { IsBackground = true, Name = "OpenBlade THX foreground" };
            thread.Start();
        }

        internal Task Started => started.Task;

        private void OnEvent(nint hook, uint eventType, nint window,
            int objectId, int childId, uint eventThread, uint eventTime)
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                writer.TryWrite(eventType == 3 ? window : GetForegroundWindow());
            }
        }

        private void Run()
        {
            nint foregroundHook = 0;
            nint desktopHook = 0;
            try
            {
                threadId = GetCurrentThreadId();
                _ = PeekMessageW(out _, 0, 0, 0, 0);
                foregroundHook = SetWinEventHook(3, 3, 0, callback, 0, 0, 0);
                desktopHook = SetWinEventHook(0x20, 0x20, 0, callback, 0, 0, 0);
                if (foregroundHook == 0 || desktopHook == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                writer.TryWrite(GetForegroundWindow());
                started.TrySetResult();
                while (Volatile.Read(ref disposed) == 0)
                {
                    int result = GetMessageW(out Message message, 0, 0, 0);
                    if (result == 0)
                    {
                        break;
                    }
                    if (result < 0)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }
                    _ = TranslateMessage(ref message);
                    _ = DispatchMessageW(ref message);
                }
            }
            catch (Exception exception)
            {
                started.TrySetException(exception);
                writer.TryComplete(exception);
            }
            finally
            {
                if (foregroundHook != 0) { _ = UnhookWinEvent(foregroundHook); }
                if (desktopHook != 0) { _ = UnhookWinEvent(desktopHook); }
                writer.TryComplete();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            uint id = Volatile.Read(ref threadId);
            if (id != 0)
            {
                _ = PostThreadMessageW(id, 0x12, 0, 0);
            }
            if (!thread.Join(TimeSpan.FromSeconds(3)))
            {
                Trace.TraceWarning("The THX foreground subscription did not stop promptly.");
            }
        }
    }

    private delegate void WinEventCallback(nint hook, uint eventType, nint window,
        int objectId, int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X;
        internal int Y;
        internal uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint minimum, uint maximum, nint module,
        WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process,
        uint flags, StringBuilder path, ref uint size);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(nint server, uint session,
        int information, out nint buffer, out uint size);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint buffer);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(nint handle, int index,
        StringBuilder information, uint length, out uint needed);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageW(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Message message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
}
