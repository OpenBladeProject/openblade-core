using System.Diagnostics;
using OpenBlade.Contracts;

namespace OpenBlade.SessionAgent;

public interface IUiAccessInputAgentSession : IDisposable
{
    Task Completion { get; }

    bool WaitUntilReady(TimeSpan timeout);
}

public interface IUiAccessInputAgentLauncher
{
    IUiAccessInputAgentSession? TryStart();
}

/// <summary>
/// Prefers the narrowly scoped signed UIAccess input process when it is present
/// and healthy. Development and unsigned bundles transparently retain the
/// existing session-local Raw Input implementation.
/// </summary>
public sealed class UiAccessInputKeyboardSourceFactory : IReactiveKeyboardSourceFactory
{
    internal static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(3);

    private readonly IReactiveKeyboardSourceFactory fallback;
    private readonly IUiAccessInputAgentLauncher launcher;
    private readonly TimeSpan readyTimeout;
    private readonly Action<Exception>? errorHandler;

    public UiAccessInputKeyboardSourceFactory(
        IReactiveKeyboardSourceFactory fallback,
        IUiAccessInputAgentLauncher launcher,
        TimeSpan? readyTimeout = null,
        Action<Exception>? errorHandler = null)
    {
        this.fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.readyTimeout = readyTimeout ?? DefaultReadyTimeout;
        if (this.readyTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(readyTimeout));
        }

        this.errorHandler = errorHandler;
    }

    public IReactiveKeyboardSource Create(Action<ReactiveKeyPulse> publish) =>
        Create(publish, _ => { });

    public IReactiveKeyboardSource Create(
        Action<ReactiveKeyPulse> publish,
        Action<PerformanceShortcutSource> publishPerformanceShortcut)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(publishPerformanceShortcut);

        IUiAccessInputAgentSession? session = null;
        try
        {
            session = launcher.TryStart();
            if (session is not null && session.WaitUntilReady(readyTimeout))
            {
                return new UiAccessInputKeyboardSource(session);
            }
        }
        catch (Exception exception)
        {
            Report(exception);
        }

        session?.Dispose();
        return fallback.Create(publish, publishPerformanceShortcut);
    }

    private void Report(Exception exception)
    {
        try
        {
            errorHandler?.Invoke(exception);
        }
        catch
        {
        }
    }

    private sealed class UiAccessInputKeyboardSource(IUiAccessInputAgentSession session)
        : IReactiveKeyboardSource
    {
        public Task Completion => session.Completion;

        public void Dispose() => session.Dispose();
    }
}

public static class UiAccessInputAgentArguments
{
    public const string ReadyEvent = "--ready-event";
    public const string ShutdownEvent = "--shutdown-event";
    public const string ParentProcessId = "--parent-pid";
    public const string ParentStartedAtUtcTicks = "--parent-start-ticks";
}

internal sealed class WindowsUiAccessInputAgentLauncher : IUiAccessInputAgentLauncher
{
    internal const string ExecutableName = "OpenBlade.InputAgent.exe";

    private readonly string baseDirectory;
    private readonly Func<ProcessStartInfo, Process?> startProcess;
    private readonly Func<Process> currentProcess;

    internal WindowsUiAccessInputAgentLauncher(
        string? baseDirectory = null,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        Func<Process>? currentProcess = null)
    {
        this.baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        this.startProcess = startProcess ?? Process.Start;
        this.currentProcess = currentProcess ?? Process.GetCurrentProcess;
    }

    public IUiAccessInputAgentSession? TryStart()
    {
        string executable = Path.Combine(baseDirectory, ExecutableName);
        if (!File.Exists(executable))
        {
            return null;
        }

        string nonce = Guid.NewGuid().ToString("N");
        string readyName = $@"Local\OpenBlade.InputAgent.Ready.{nonce}";
        string shutdownName = $@"Local\OpenBlade.InputAgent.Shutdown.{nonce}";
        var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        var shutdown = new EventWaitHandle(false, EventResetMode.ManualReset, shutdownName);
        Process? parent = null;
        Process? child = null;
        try
        {
            parent = currentProcess();
            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = baseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add(UiAccessInputAgentArguments.ReadyEvent);
            start.ArgumentList.Add(readyName);
            start.ArgumentList.Add(UiAccessInputAgentArguments.ShutdownEvent);
            start.ArgumentList.Add(shutdownName);
            start.ArgumentList.Add(UiAccessInputAgentArguments.ParentProcessId);
            start.ArgumentList.Add(parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(UiAccessInputAgentArguments.ParentStartedAtUtcTicks);
            start.ArgumentList.Add(
                parent.StartTime.ToUniversalTime().Ticks.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));

            child = startProcess(start);
            if (child is null)
            {
                return null;
            }

            var session = new WindowsUiAccessInputAgentSession(
                child,
                ready,
                shutdown);
            child = null;
            ready = null!;
            shutdown = null!;
            return session;
        }
        catch
        {
            child?.Dispose();
            throw;
        }
        finally
        {
            parent?.Dispose();
            ready?.Dispose();
            shutdown?.Dispose();
        }
    }

    private sealed class WindowsUiAccessInputAgentSession : IUiAccessInputAgentSession
    {
        private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(2);

        private readonly Process process;
        private readonly EventWaitHandle ready;
        private readonly EventWaitHandle shutdown;
        private readonly ManualResetEvent processExited = new(false);
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int disposed;

        internal WindowsUiAccessInputAgentSession(
            Process process,
            EventWaitHandle ready,
            EventWaitHandle shutdown)
        {
            this.process = process;
            this.ready = ready;
            this.shutdown = shutdown;
            process.EnableRaisingEvents = true;
            process.Exited += OnExited;
            if (process.HasExited)
            {
                OnExited(process, EventArgs.Empty);
            }
        }

        public Task Completion => completion.Task;

        public bool WaitUntilReady(TimeSpan timeout)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return false;
            }

            int signaled = WaitHandle.WaitAny([ready, processExited], timeout);
            return signaled == 0 && !completion.Task.IsCompleted;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _ = shutdown.Set();
                if (!process.HasExited)
                {
                    _ = process.WaitForExit(checked((int)GracefulShutdownTimeout.TotalMilliseconds));
                }
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or ObjectDisposedException)
            {
            }
            finally
            {
                process.Exited -= OnExited;
                completion.TrySetResult();
                processExited.Set();
                process.Dispose();
                ready.Dispose();
                shutdown.Dispose();
                processExited.Dispose();
            }
        }

        private void OnExited(object? sender, EventArgs args)
        {
            completion.TrySetResult();
            try
            {
                processExited.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
