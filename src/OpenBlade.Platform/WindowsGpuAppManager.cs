using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Platform;

public sealed record WindowsGpuApp(
    int ProcessId,
    long StartTimeUtcTicks,
    string Name,
    double UtilizationPercent,
    bool CanClose,
    string? BlockReason);

public sealed record WindowsGpuAppScan(
    bool IsAvailable,
    string? Error,
    IReadOnlyList<WindowsGpuApp> Apps);

public sealed record WindowsGpuAppActionResult(
    int ProcessId,
    long StartTimeUtcTicks,
    bool Succeeded,
    string? Error);

public interface IWindowsGpuAppManager
{
    WindowsGpuAppScan Scan(CancellationToken cancellationToken = default);

    IReadOnlyList<WindowsGpuAppActionResult> CloseGracefully(
        IReadOnlyList<WindowsGpuApp> apps,
        CancellationToken cancellationToken = default);

    IReadOnlyList<WindowsGpuAppActionResult> ForceClose(
        IReadOnlyList<WindowsGpuApp> apps,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Finds and closes current-user applications holding NVIDIA WDDM engine contexts.
/// The caller remains responsible for showing the destructive confirmation.
/// </summary>
public sealed class WindowsGpuAppManager : IWindowsGpuAppManager, IDisposable
{
    private const string ProcessActionFailure = "Windows could not close this process.";
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int ActivitySampleAttempts = 20;
    private const int ActivitySampleIntervalMilliseconds = 150;
    private static readonly HashSet<string> ProtectedProcessNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost",
        "csrss",
        "dwm",
        "explorer",
        "fontdrvhost",
        "idle",
        "lsass",
        "registry",
        "searchhost",
        "secure system",
        "services",
        "shellexperiencehost",
        "sihost",
        "smss",
        "startmenuexperiencehost",
        "system",
        "taskhostw",
        "textinputhost",
        "wininit",
        "winlogon",
    };
    private static readonly HashSet<string> IgnoredGpuProcessNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "nvidia overlay",
    };

    private readonly WindowsNvidiaGpuActivityReader reader;
    private readonly SecurityIdentifier currentUserSid;
    private readonly int currentSessionId;
    private readonly IWindowsProcessActionTargetFactory processActionTargetFactory;

    public WindowsGpuAppManager()
        : this(CreateDefaultContext())
    {
    }

    private WindowsGpuAppManager(DefaultContext context)
        : this(
            context.Reader,
            context.UserSid,
            context.SessionId,
            new WindowsProcessActionTargetFactory(context.UserSid))
    {
    }

    internal WindowsGpuAppManager(
        WindowsNvidiaGpuActivityReader reader,
        SecurityIdentifier currentUserSid,
        int currentSessionId)
        : this(
            reader,
            currentUserSid,
            currentSessionId,
            new WindowsProcessActionTargetFactory(currentUserSid))
    {
    }

    internal WindowsGpuAppManager(
        WindowsNvidiaGpuActivityReader reader,
        SecurityIdentifier currentUserSid,
        int currentSessionId,
        IWindowsProcessActionTargetFactory processActionTargetFactory)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.currentUserSid = currentUserSid
            ?? throw new ArgumentNullException(nameof(currentUserSid));
        this.currentSessionId = currentSessionId;
        this.processActionTargetFactory = processActionTargetFactory
            ?? throw new ArgumentNullException(nameof(processActionTargetFactory));
    }

    public WindowsGpuAppScan Scan(CancellationToken cancellationToken = default)
    {
        WindowsGpuActivitySnapshot sample = RefreshAndReadActivitySample(
            reader.Refresh,
            reader.Read,
            Thread.Sleep,
            cancellationToken);
        if (!sample.IsAvailable)
        {
            return new WindowsGpuAppScan(
                false,
                "Windows GPU activity counters are unavailable.",
                []);
        }

        IReadOnlyList<WindowsGpuProcessActivity> stableProcesses = sample.Processes;
        Dictionary<int, int> parents = WindowsProcessTree.ReadParents();
        Dictionary<int, ProcessEntry> processTable = ReadProcessTable(
            stableProcesses,
            parents,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WindowsGpuApp> apps = CreateApps(
            stableProcesses,
            processTable,
            currentSessionId);
        return new WindowsGpuAppScan(true, null, apps);
    }

    public IReadOnlyList<WindowsGpuAppActionResult> CloseGracefully(
        IReadOnlyList<WindowsGpuApp> apps,
        CancellationToken cancellationToken = default) =>
        Act(apps, force: false, cancellationToken);

    public IReadOnlyList<WindowsGpuAppActionResult> ForceClose(
        IReadOnlyList<WindowsGpuApp> apps,
        CancellationToken cancellationToken = default) =>
        Act(apps, force: true, cancellationToken);

    public void Dispose() => reader.Dispose();

    internal static bool IsProtectedProcessName(string name) =>
        name.StartsWith("OpenBlade", StringComparison.OrdinalIgnoreCase)
        || ProtectedProcessNames.Contains(name);

    internal static bool IsIgnoredGpuProcessName(string name) =>
        IgnoredGpuProcessNames.Contains(name);

    internal static WindowsGpuActivitySnapshot ReadActivitySample(
        Func<WindowsGpuActivitySnapshot> read,
        Action<int> sleep,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(sleep);
        bool hasPrime = false;
        WindowsGpuActivitySnapshot? previous = null;
        for (int attempt = 0; attempt < ActivitySampleAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowsGpuActivitySnapshot snapshot = read();
            if (snapshot.IsAvailable)
            {
                if (!hasPrime)
                {
                    hasPrime = true;
                }
                else if (previous is not null)
                {
                    return new WindowsGpuActivitySnapshot(
                        true,
                        IntersectStableProcesses(previous.Processes, snapshot.Processes));
                }
                else
                {
                    previous = snapshot;
                }
            }
            else
            {
                hasPrime = false;
                previous = null;
            }

            if (attempt + 1 < ActivitySampleAttempts)
            {
                sleep(ActivitySampleIntervalMilliseconds);
            }
        }

        return new WindowsGpuActivitySnapshot(false, []);
    }

    internal static WindowsGpuActivitySnapshot RefreshAndReadActivitySample(
        Func<bool> refresh,
        Func<WindowsGpuActivitySnapshot> read,
        Action<int> sleep,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        cancellationToken.ThrowIfCancellationRequested();
        return refresh()
            ? ReadActivitySample(read, sleep, cancellationToken)
            : new WindowsGpuActivitySnapshot(false, []);
    }

    internal static WindowsGpuProcessActivity[] IntersectStableProcesses(
        IReadOnlyList<WindowsGpuProcessActivity> first,
        IReadOnlyList<WindowsGpuProcessActivity> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        HashSet<int> firstProcessIds = first
            .Select(process => process.ProcessId)
            .Where(processId => processId > 0)
            .ToHashSet();
        Dictionary<int, WindowsGpuProcessActivity> latestByProcess = [];
        foreach (WindowsGpuProcessActivity process in second)
        {
            if (process.ProcessId > 0 && firstProcessIds.Contains(process.ProcessId))
            {
                latestByProcess[process.ProcessId] = process;
            }
        }

        return latestByProcess.Values
            .OrderByDescending(process => process.UtilizationPercent)
            .ThenBy(process => process.ProcessId)
            .ToArray();
    }

    internal static IReadOnlyList<WindowsGpuApp> CreateApps(
        IReadOnlyList<WindowsGpuProcessActivity> activities,
        IReadOnlyDictionary<int, ProcessEntry> processTable,
        int currentSessionId)
    {
        Dictionary<int, WindowsGpuApp> apps = [];
        foreach (WindowsGpuProcessActivity activity in activities)
        {
            if (!processTable.TryGetValue(activity.ProcessId, out ProcessEntry? observed))
            {
                continue;
            }

            ProcessEntry target = SelectOwningApplication(observed, processTable);
            if (IsIgnoredGpuProcessName(target.Name))
            {
                continue;
            }
            WindowsGpuApp candidate = CreateApp(
                target,
                activity.UtilizationPercent,
                currentSessionId);
            if (apps.TryGetValue(candidate.ProcessId, out WindowsGpuApp? existing))
            {
                apps[candidate.ProcessId] = existing with
                {
                    UtilizationPercent = existing.UtilizationPercent
                        + candidate.UtilizationPercent,
                };
            }
            else
            {
                apps.Add(candidate.ProcessId, candidate);
            }
        }

        return apps.Values
            .OrderBy(app => app.CanClose ? 0 : 1)
            .ThenByDescending(app => app.UtilizationPercent)
            .ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(app => app.ProcessId)
            .ToArray();
    }

    internal static ProcessEntry SelectOwningApplication(
        ProcessEntry observed,
        IReadOnlyDictionary<int, ProcessEntry> processTable)
    {
        ProcessEntry target = observed;
        HashSet<int> visited = [observed.ProcessId];
        while (processTable.TryGetValue(target.ParentProcessId, out ProcessEntry? parent)
            && visited.Add(parent.ProcessId)
            && parent.SessionId == observed.SessionId
            && parent.SameUser
            && parent.StartTimeUtcTicks <= target.StartTimeUtcTicks
            && !IsProtectedProcessName(parent.Name)
            && string.Equals(parent.Name, target.Name, StringComparison.OrdinalIgnoreCase))
        {
            target = parent;
        }
        return target;
    }

    private IReadOnlyList<WindowsGpuAppActionResult> Act(
        IReadOnlyList<WindowsGpuApp> apps,
        bool force,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apps);
        List<WindowsGpuAppActionResult> results = [];
        foreach (WindowsGpuApp app in apps.DistinctBy(value => value.ProcessId))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return results;
            }
            if (!app.CanClose)
            {
                results.Add(Failed(app, app.BlockReason ?? "This process is protected."));
                continue;
            }

            try
            {
                if (!processActionTargetFactory.TryOpen(
                    app.ProcessId,
                    out IWindowsProcessActionTarget? target))
                {
                    results.Add(Failed(
                        app,
                        "The process exited or its identity changed after the GPU scan."));
                    continue;
                }

                using (target)
                {
                    if (target.HasExited
                        || target.ProcessId != app.ProcessId
                        || target.StartTimeUtcTicks != app.StartTimeUtcTicks
                        || target.SessionId != currentSessionId
                        || !target.SameUser
                        || !string.Equals(
                            target.Name,
                            app.Name,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(Failed(
                            app,
                            "The process exited or its identity changed after the GPU scan."));
                        continue;
                    }

                    if (IsProtectedProcessName(target.Name))
                    {
                        results.Add(Failed(
                            app,
                            "OpenBlade does not close protected processes."));
                        continue;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return results;
                    }

                    bool succeeded;
                    if (force)
                    {
                        KillProcessTreeWithExactFallback(target);
                        succeeded = true;
                    }
                    else
                    {
                        succeeded = target.CloseMainWindow();
                    }
                    results.Add(succeeded
                        ? new WindowsGpuAppActionResult(
                            app.ProcessId,
                            app.StartTimeUtcTicks,
                            true,
                            null)
                        : Failed(
                            app,
                            "The application has no window that can be closed gracefully."));
                }
            }
            catch (Exception exception) when (IsRecoverableProcessActionException(exception))
            {
                Trace.TraceWarning(
                    "OpenBlade could not act on GPU process {0}: {1}",
                    app.ProcessId,
                    exception);
                results.Add(Failed(app, ProcessActionFailure));
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return results;
            }
        }
        return results;
    }

    private static void KillProcessTreeWithExactFallback(
        IWindowsProcessActionTarget target)
    {
        try
        {
            target.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (IsRecoverableProcessActionException(exception))
        {
            // A tree-wide request can fail while the freshly revalidated process
            // itself remains terminable. Retrying only that exact process preserves
            // the attended action without transferring approval to another PID.
            target.Kill(entireProcessTree: false);
        }
    }

    internal static bool IsRecoverableProcessActionException(Exception exception) =>
        exception is AggregateException || IsRecoverableProcessInspectionException(exception);

    private static bool IsRecoverableProcessInspectionException(Exception exception) =>
        exception is ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or SecurityException
            or UnauthorizedAccessException
            or Win32Exception;

    private static WindowsGpuApp CreateApp(
        ProcessEntry process,
        double utilization,
        int currentSessionId)
    {
        string? blockReason = null;
        if (process.SessionId != currentSessionId)
        {
            blockReason = "The process belongs to another Windows session.";
        }
        else if (!process.SameUser)
        {
            blockReason = "The process belongs to another Windows account.";
        }
        else if (IsProtectedProcessName(process.Name))
        {
            blockReason = "OpenBlade does not close Windows or OpenBlade processes.";
        }

        return new WindowsGpuApp(
            process.ProcessId,
            process.StartTimeUtcTicks,
            process.Name,
            utilization,
            blockReason is null,
            blockReason);
    }

    private Dictionary<int, ProcessEntry> ReadProcessTable(
        IReadOnlyList<WindowsGpuProcessActivity> activities,
        IReadOnlyDictionary<int, int> parents,
        CancellationToken cancellationToken)
    {
        Dictionary<int, ProcessEntry> table = [];
        foreach (int processId in activities.Select(activity => activity.ProcessId).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            int currentProcessId = processId;
            HashSet<int> visited = [];
            while (currentProcessId > 0 && visited.Add(currentProcessId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!table.TryGetValue(currentProcessId, out ProcessEntry? process))
                {
                    if (!TryReadProcess(currentProcessId, out process))
                    {
                        break;
                    }
                    process = process with
                    {
                        ParentProcessId = parents.GetValueOrDefault(currentProcessId),
                    };
                    table[currentProcessId] = process;
                }
                currentProcessId = process.ParentProcessId;
            }
        }
        return table;
    }

    private bool TryReadProcess(
        int processId,
        [NotNullWhen(true)] out ProcessEntry? entry)
    {
        entry = null;
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            nint handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == 0)
            {
                return false;
            }
            try
            {
                entry = new ProcessEntry(
                    processId,
                    ParentProcessId: 0,
                    process.SessionId,
                    process.StartTime.ToUniversalTime().Ticks,
                    process.ProcessName,
                    IsSameUser(handle, currentUserSid));
                return true;
            }
            finally
            {
                _ = CloseHandle(handle);
            }
        }
        catch (Exception exception) when (IsRecoverableProcessInspectionException(exception))
        {
            return false;
        }
    }

    private static bool IsSameUser(
        nint processHandle,
        SecurityIdentifier expectedUserSid)
    {
        if (!OpenProcessToken(processHandle, TokenQuery, out nint token))
        {
            return false;
        }
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User is not null && expectedUserSid.Equals(identity.User);
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private static WindowsGpuAppActionResult Failed(WindowsGpuApp app, string error) =>
        new(app.ProcessId, app.StartTimeUtcTicks, false, error);

    private static DefaultContext CreateDefaultContext()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier userSid = identity.User
            ?? throw new InvalidOperationException("The current user SID is unavailable.");
        using Process process = Process.GetCurrentProcess();
        return new DefaultContext(
            new WindowsNvidiaGpuActivityReader(),
            userSid,
            process.SessionId);
    }

    private sealed record DefaultContext(
        WindowsNvidiaGpuActivityReader Reader,
        SecurityIdentifier UserSid,
        int SessionId);

    internal sealed record ProcessEntry(
        int ProcessId,
        int ParentProcessId,
        int SessionId,
        long StartTimeUtcTicks,
        string Name,
        bool SameUser);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        nint processHandle,
        uint desiredAccess,
        out nint tokenHandle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    internal interface IWindowsProcessActionTargetFactory
    {
        bool TryOpen(
            int processId,
            [NotNullWhen(true)] out IWindowsProcessActionTarget? target);
    }

    internal interface IWindowsProcessActionTarget : IDisposable
    {
        bool HasExited { get; }

        int ProcessId { get; }

        int SessionId { get; }

        long StartTimeUtcTicks { get; }

        string Name { get; }

        bool SameUser { get; }

        bool CloseMainWindow();

        void Kill(bool entireProcessTree);
    }

    private sealed class WindowsProcessActionTargetFactory(
        SecurityIdentifier currentUserSid) : IWindowsProcessActionTargetFactory
    {
        private readonly SecurityIdentifier currentUserSid = currentUserSid
            ?? throw new ArgumentNullException(nameof(currentUserSid));

        public bool TryOpen(
            int processId,
            [NotNullWhen(true)] out IWindowsProcessActionTarget? target)
        {
            Process? process = null;
            target = null;
            try
            {
                process = Process.GetProcessById(processId);
                SafeProcessHandle processHandle = process.SafeHandle;
                if (processHandle.IsClosed || processHandle.IsInvalid)
                {
                    return false;
                }

                target = new WindowsProcessActionTarget(
                    process,
                    processHandle,
                    currentUserSid);
                process = null;
                return true;
            }
            finally
            {
                process?.Dispose();
            }
        }
    }

    private sealed class WindowsProcessActionTarget(
        Process process,
        SafeProcessHandle processHandle,
        SecurityIdentifier currentUserSid) : IWindowsProcessActionTarget
    {
        private readonly Process process = process
            ?? throw new ArgumentNullException(nameof(process));
        private readonly SafeProcessHandle processHandle = processHandle
            ?? throw new ArgumentNullException(nameof(processHandle));
        private readonly SecurityIdentifier currentUserSid = currentUserSid
            ?? throw new ArgumentNullException(nameof(currentUserSid));

        public bool HasExited => process.HasExited;

        public int ProcessId => process.Id;

        public int SessionId => process.SessionId;

        public long StartTimeUtcTicks => process.StartTime.ToUniversalTime().Ticks;

        public string Name => process.ProcessName;

        public bool SameUser => IsSameUser(
            processHandle.DangerousGetHandle(),
            currentUserSid);

        public bool CloseMainWindow() => process.CloseMainWindow();

        public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);

        public void Dispose() => process.Dispose();
    }
}

internal static class WindowsProcessTree
{
    private const uint SnapshotProcesses = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);

    internal static Dictionary<int, int> ReadParents()
    {
        Dictionary<int, int> parents = [];
        nint snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandleValue)
        {
            return parents;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                Size = checked((uint)Marshal.SizeOf<ProcessEntry32>()),
            };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return parents;
            }

            do
            {
                if (entry.ProcessId > 0)
                {
                    parents[checked((int)entry.ProcessId)] =
                        checked((int)entry.ParentProcessId);
                }
            }
            while (Process32NextW(snapshot, ref entry));
            return parents;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
