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

    internal static string BackendArgument(KeyboardInputBackend backend) => backend switch
    {
        KeyboardInputBackend.WindowsHook => "windows-hook",
        KeyboardInputBackend.RazerDriver => "razer-driver",
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    internal static bool TryParseArguments(IReadOnlyList<string> args, out int parentId,
        out KeyboardInputBackend backend) => TryParseArguments(args, out parentId, out backend, out _);

    internal static string FeaturesArgument(KeyboardDriverFeatures features) => features switch
    {
        KeyboardDriverFeatures.SnapTap => "snap-tap",
        KeyboardDriverFeatures.FunctionRow => "function-row",
        KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow => "snap-tap-and-function-row",
        _ => throw new ArgumentOutOfRangeException(nameof(features)),
    };

    private static KeyboardDriverFeatures ParseFeatures(string value) => value switch
    {
        "snap-tap" => KeyboardDriverFeatures.SnapTap,
        "function-row" => KeyboardDriverFeatures.FunctionRow,
        "snap-tap-and-function-row" => KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow,
        _ => KeyboardDriverFeatures.None,
    };

    internal static bool TryParseArguments(IReadOnlyList<string> args, out int parentId,
        out KeyboardInputBackend backend, out KeyboardDriverFeatures features)
    {
        parentId = 0;
        backend = KeyboardInputBackend.RazerDriver;
        features = KeyboardDriverFeatures.SnapTap;
        if (!IsRequested(args) || args.Count is < 2 or > 4
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out parentId)
            || parentId <= 0 || parentId == Environment.ProcessId)
        {
            return false;
        }
        if (args[0] == SnapTapDriverSupervisor.WorkerMode)
        {
            if (args.Count > 3) { return false; }
            if (args.Count == 3) { features = ParseFeatures(args[2]); }
            return features != KeyboardDriverFeatures.None;
        }
        if (args.Count >= 3)
        {
            backend = args[2] switch
            {
                "windows-hook" => KeyboardInputBackend.WindowsHook,
                "razer-driver" => KeyboardInputBackend.RazerDriver,
                _ => KeyboardInputBackend.Unknown,
            };
        }
        if (args.Count == 4)
        {
            if (backend != KeyboardInputBackend.RazerDriver) { return false; }
            features = ParseFeatures(args[3]);
        }
        return backend != KeyboardInputBackend.Unknown && features != KeyboardDriverFeatures.None;
    }

    internal static DiagnosticFailure? InspectWindowsDriverOwnership(string directory, Func<string> readBootIdentity)
    {
        try
        {
            return DriverOwnershipJournalReader.AllowsWindowsBackend(directory, KeyboardDriverFeatures.SnapTap, readBootIdentity)
                ? null : SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Worker,
                    "SelectBackend", "OwnershipJournal", "DriverOwnershipRecoveryRequired", true);
        }
        catch (Exception exception) when (DriverOwnershipJournalReader.IsObservationException(exception))
        {
            return SnapTapDiagnosticFailure.FromException(SnapTapDiagnosticFailure.Worker,
                "SelectBackend", "OwnershipJournal", "DriverOwnershipObservationFailed", exception, true);
        }
    }

    internal static int Run(string[] args)
    {
        if (!TryParseArguments(args, out int parentId, out KeyboardInputBackend backend, out KeyboardDriverFeatures features))
        {
            return 2;
        }
        try
        {
            if (backend == KeyboardInputBackend.RazerDriver) { KeyboardDriverStatusStore.ConfigureScope(features); }
            string preferencesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenBlade", "preferences.json");
            int? PreferenceRejection()
            {
                UiInputPreferences preferences = new UiPreferenceStore(preferencesPath)
                    .LoadInputPreferencesAsync().GetAwaiter().GetResult();
                return SnapTapInputHost.PreferenceRejection(preferences, backend, features);
            }
            if (PreferenceRejection() is { } initialRejection)
            {
                return initialRejection;
            }
            using SafeProcessHandle parent = Native.OpenProcess(0x00101000, false, parentId);
            if (parent.IsInvalid || !ValidateParent(parent, parentId))
            {
                return 2;
            }
            using EventWaitHandle stop = EventWaitHandle.OpenExisting(
                SnapTapInputProcess.StopEventName(parentId, backend));
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
                if (PreferenceRejection() is { } workerRejection) { return workerRejection; }
                return SnapTapDriverLoop.Run(parentId, () => ShouldStop()
                    || !WindowsRazerSnapTapDriverDiscovery.HasActiveConsoleSession(), features);
            }
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string sid = identity.User!.Value;
            string userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16];
            using Process current = Process.GetCurrentProcess();
            using var mutex = new Mutex(false, $"Local\\OpenBlade.SnapTap.{BackendArgument(backend)}.{current.SessionId}.{userKey}");
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
                using var pairMutex = (features & KeyboardDriverFeatures.SnapTap) != 0
                    ? new Mutex(false, $"Local\\OpenBlade.SnapTapPair.{current.SessionId}.{userKey}") : null;
                bool pairAcquired = false;
                try
                {
                    if (pairMutex is not null)
                    {
                        try { pairAcquired = pairMutex.WaitOne(0); }
                        catch (AbandonedMutexException) { pairAcquired = true; return 1; }
                        if (!pairAcquired) { return 1; }
                    }
                    if (ShouldStop()) { return 0; }
                    if (PreferenceRejection() is { } rejection) { return rejection; }
                    if (backend == KeyboardInputBackend.WindowsHook)
                    {
                        DiagnosticFailure? driverOwnershipFailure = InspectWindowsDriverOwnership(
                            Path.GetDirectoryName(preferencesPath)!, SnapTapDriverSupervisor.ReadBootIdentity);
                        if (driverOwnershipFailure is not null)
                        {
                            using var publisher = new SnapTapStatusPublisher(backend: backend);
                            publisher.Publish(SnapTapRuntimeState.RecoveryRequired, driverOwnershipFailure);
                            return 4;
                        }
                        return SnapTapWindowsHookLoop.Run(ShouldStop);
                    }
                    if ((features & KeyboardDriverFeatures.SnapTap) != 0
                        && !SnapTapWindowsOwnershipJournal.ReconcilePrior(SnapTapWindowsOwnershipJournal.DefaultPath,
                            SnapTapDriverSupervisor.ReadBootIdentity, out DiagnosticFailure? ownershipFailure))
                    {
                        using var publisher = new SnapTapStatusPublisher(backend: backend);
                        publisher.Publish(SnapTapRuntimeState.RecoveryRequired, ownershipFailure);
                        return 4;
                    }
                    return SnapTapDriverSupervisor.Run(ShouldStop, features);
                }
                finally { if (pairAcquired) { pairMutex!.ReleaseMutex(); } }
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

    internal static int? PreferenceRejection(UiInputPreferences preferences, KeyboardInputBackend backend,
        KeyboardDriverFeatures features)
    {
        if (backend == KeyboardInputBackend.WindowsHook)
        {
            return !preferences.SnapTapEnabled ? 3
                : preferences.SnapTapBackend != KeyboardInputBackend.WindowsHook ? 3 : null;
        }
        KeyboardDriverFeatures current = RazerKeyboardDriverPreferenceSource.SelectFeatures(
            preferences.SnapTapEnabled, preferences.SnapTapBackend, preferences.FunctionRowBackend);
        return current == KeyboardDriverFeatures.None || current != features ? 3 : null;
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
