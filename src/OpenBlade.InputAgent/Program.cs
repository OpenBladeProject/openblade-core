using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.InputAgent;

internal static class Program
{
    private const int UiAccessUnavailableExitCode = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch
        {
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (!WindowsUiAccessState.IsEnabled())
        {
            // The normal SessionAgent will retain Raw Input ownership. This is
            // expected for development and unsigned community builds.
            return UiAccessUnavailableExitCode;
        }

        InputAgentLaunchArguments launch = InputAgentLaunchArguments.Parse(args);
        using EventWaitHandle ready = EventWaitHandle.OpenExisting(launch.ReadyEventName);
        using EventWaitHandle shutdown = EventWaitHandle.OpenExisting(launch.ShutdownEventName);
        using Process parent = Process.GetProcessById(launch.ParentProcessId);
        if (parent.StartTime.ToUniversalTime().Ticks != launch.ParentStartedAtUtcTicks)
        {
            return 1;
        }

        using var lifetime = new CancellationTokenSource();
        RegisteredWaitHandle shutdownRegistration = ThreadPool.RegisterWaitForSingleObject(
            shutdown,
            static (state, _) => CancelBestEffort((CancellationTokenSource)state!),
            lifetime,
            Timeout.Infinite,
            executeOnlyOnce: true);
        try
        {
            Action<Exception> report = exception => Trace.TraceWarning(
                "OpenBlade UIAccess input failed: {0}",
                exception.Message);
            var sourceFactory = new ReadySignalingKeyboardSourceFactory(
                WindowsReactiveKeyboardSourceFactory.Instance,
                ready);
            using IPerformanceShortcutNotificationSink notifications =
                WindowsPerformanceShortcutNotificationSink.CreateBestEffort(report);
            await using var input = new ServiceReactiveLightingLoop(
                new ServiceClient(),
                sourceFactory,
                report,
                notifications);

            Task inputTask = input.RunAsync(lifetime.Token);
            Task parentExitTask = parent.WaitForExitAsync(lifetime.Token);
            Task completed = await Task.WhenAny(inputTask, parentExitTask).ConfigureAwait(false);
            if (completed == parentExitTask)
            {
                CancelBestEffort(lifetime);
            }

            try
            {
                await inputTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }

            return inputTask.IsFaulted ? 1 : 0;
        }
        finally
        {
            CancelBestEffort(lifetime);
            using var callbacksCompleted = new EventWaitHandle(
                false,
                EventResetMode.ManualReset);
            if (shutdownRegistration.Unregister(callbacksCompleted))
            {
                _ = callbacksCompleted.WaitOne(TimeSpan.FromSeconds(2));
            }
        }
    }

    private static void CancelBestEffort(CancellationTokenSource lifetime)
    {
        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class ReadySignalingKeyboardSourceFactory(
        IReactiveKeyboardSourceFactory inner,
        EventWaitHandle ready) : IReactiveKeyboardSourceFactory
    {
        public IReactiveKeyboardSource Create(Action<ReactiveKeyPulse> publish) =>
            Create(publish, _ => { });

        public IReactiveKeyboardSource Create(
            Action<ReactiveKeyPulse> publish,
            Action<PerformanceShortcutSource> publishPerformanceShortcut)
        {
            IReactiveKeyboardSource source = inner.Create(
                publish,
                publishPerformanceShortcut);
            try
            {
                _ = ready.Set();
                return source;
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }
    }
}

internal sealed record InputAgentLaunchArguments(
    string ReadyEventName,
    string ShutdownEventName,
    int ParentProcessId,
    long ParentStartedAtUtcTicks)
{
    internal static InputAgentLaunchArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 8)
        {
            throw new ArgumentException("The UIAccess input launch contract is invalid.", nameof(args));
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (!values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException("A UIAccess input launch argument is duplicated.", nameof(args));
            }
        }

        string ready = RequiredEventName(
            values,
            UiAccessInputAgentArguments.ReadyEvent,
            @"Local\OpenBlade.InputAgent.Ready.");
        string shutdown = RequiredEventName(
            values,
            UiAccessInputAgentArguments.ShutdownEvent,
            @"Local\OpenBlade.InputAgent.Shutdown.");
        if (!int.TryParse(
                Required(values, UiAccessInputAgentArguments.ParentProcessId),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parentProcessId)
            || parentProcessId <= 0)
        {
            throw new ArgumentException("The parent process ID is invalid.", nameof(args));
        }

        if (!long.TryParse(
                Required(values, UiAccessInputAgentArguments.ParentStartedAtUtcTicks),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long parentStartedAtUtcTicks)
            || parentStartedAtUtcTicks <= 0)
        {
            throw new ArgumentException("The parent start time is invalid.", nameof(args));
        }

        return new InputAgentLaunchArguments(
            ready,
            shutdown,
            parentProcessId,
            parentStartedAtUtcTicks);
    }

    private static string RequiredEventName(
        IReadOnlyDictionary<string, string> values,
        string key,
        string prefix)
    {
        string value = Required(values, key);
        string nonce = value.StartsWith(prefix, StringComparison.Ordinal)
            ? value[prefix.Length..]
            : string.Empty;
        if (nonce.Length != 32 || !nonce.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A UIAccess input event name is invalid.", nameof(values));
        }

        return value;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("A UIAccess input launch argument is missing.", nameof(values));
}

internal static partial class WindowsUiAccessState
{
    private const uint TokenQuery = 0x0008;
    private const int TokenUiAccess = 26;

    internal static bool IsEnabled()
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenQuery, out SafeFileHandle token))
        {
            return false;
        }

        using (token)
        {
            int value = 0;
            return GetTokenInformation(
                    token,
                    TokenUiAccess,
                    ref value,
                    sizeof(int),
                    out int returnedLength)
                && returnedLength == sizeof(int)
                && value != 0;
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        nint processHandle,
        uint desiredAccess,
        out SafeFileHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeFileHandle tokenHandle,
        int tokenInformationClass,
        ref int tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
