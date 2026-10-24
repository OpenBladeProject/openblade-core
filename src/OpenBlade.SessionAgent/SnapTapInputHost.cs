using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>Runs without the Session Agent's Raw Input source in a separate process.</summary>
internal static class SnapTapInputHost
{
    internal const string ModeArgument = "--snap-tap-input";

    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] is ModeArgument or SnapTapDriverSupervisor.WorkerMode;

    internal static int Run(string[] args)
    {
        if (!IsRequested(args) || args.Length != 2
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parentId)
            || parentId <= 0 || parentId == Environment.ProcessId)
        {
            return 2;
        }
        try
        {
            string preferencesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenBlade", "preferences.json");
            if (!new UiPreferenceStore(preferencesPath).LoadInputPreferencesAsync().GetAwaiter().GetResult().SnapTapEnabled)
            {
                return 0;
            }
            using SafeProcessHandle parent = Native.OpenProcess(0x00101000, false, parentId);
            if (parent.IsInvalid || !ValidateParent(parent, parentId))
            {
                return 2;
            }
            using EventWaitHandle stop = EventWaitHandle.OpenExisting(
                SnapTapInputProcess.StopEventName(parentId));
            using var shutdown = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                SessionAgentShutdownEndpoint.ForExecutablePath(Environment.ProcessPath!));
            bool ShouldStop() => stop.WaitOne(0) || shutdown.WaitOne(0)
                || Native.WaitForSingleObject(parent, 0) != 258;
            if (args[0] == SnapTapDriverSupervisor.WorkerMode)
            {
                using EventWaitHandle startGate = EventWaitHandle.OpenExisting(SnapTapDriverSupervisor.StartGateName(parentId));
                long startedAt = Environment.TickCount64;
                while (!startGate.WaitOne(50))
                {
                    if (ShouldStop() || Environment.TickCount64 - startedAt > 10000)
                    {
                        return 1;
                    }
                }
                if (ShouldStop())
                {
                    return 1;
                }
                return SnapTapDriverLoop.Run(parentId, () => ShouldStop()
                    || !WindowsRazerSnapTapDriverDiscovery.HasActiveConsoleSession());
            }
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string sid = identity.User!.Value;
            string userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16];
            using Process current = Process.GetCurrentProcess();
            using var mutex = new Mutex(false, $"Local\\OpenBlade.SnapTap.{current.SessionId}.{userKey}");
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // An abandoned helper might have submitted input. Never rearm automatically.
                return 1;
            }
            if (!acquired)
            {
                return 1;
            }
            try
            {
                return SnapTapDriverSupervisor.Run(ShouldStop);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or InvalidOperationException or WaitHandleCannotBeOpenedException)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.SnapTapInputHost",
                "Run",
                exception);
            return 1;
        }
    }

    private static bool ValidateParent(SafeProcessHandle parent, int parentId)
    {
        if (Native.WaitForSingleObject(parent, 0) != 258
            || !Native.ProcessIdToSessionId((uint)parentId, out uint parentSession)
            || !Native.ProcessIdToSessionId((uint)Environment.ProcessId, out uint currentSession)
            || parentSession != currentSession)
        {
            return false;
        }
        var image = new StringBuilder(32768);
        uint length = (uint)image.Capacity;
        if (!Native.QueryFullProcessImageName(parent, 0, image, ref length)
            || !string.Equals(Path.GetFullPath(image.ToString()),
                Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase)
            || !Native.OpenProcessToken(parent, 8, out SafeAccessTokenHandle token))
        {
            return false;
        }
        using (token)
        using (var parentIdentity = new WindowsIdentity(token.DangerousGetHandle()))
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            return parentIdentity.User is not null && parentIdentity.User == identity.User;
        }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
        [DllImport("kernel32.dll")]
        internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ProcessIdToSessionId(uint id, out uint session);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    }
}
