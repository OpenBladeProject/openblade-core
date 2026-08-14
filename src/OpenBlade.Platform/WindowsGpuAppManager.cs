using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace OpenBlade.Platform;

public sealed record WindowsGpuApp(
    int ProcessId,
    long StartTimeUtcTicks,
    string Name,
    string? ExecutablePath,
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
/// Finds and closes current-user applications actively using NVIDIA WDDM engines.
/// The caller remains responsible for showing the destructive confirmation.
/// </summary>
public sealed class WindowsGpuAppManager : IWindowsGpuAppManager, IDisposable
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int MaximumPathCharacters = 32768;
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

    private readonly WindowsNvidiaGpuActivityReader reader;
    private readonly SecurityIdentifier currentUserSid;
    private readonly int currentSessionId;

    public WindowsGpuAppManager()
        : this(CreateDefaultContext())
    {
    }

    private WindowsGpuAppManager(DefaultContext context)
        : this(context.Reader, context.UserSid, context.SessionId)
    {
    }

    internal WindowsGpuAppManager(
        WindowsNvidiaGpuActivityReader reader,
        SecurityIdentifier currentUserSid,
        int currentSessionId)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.currentUserSid = currentUserSid
            ?? throw new ArgumentNullException(nameof(currentUserSid));
        this.currentSessionId = currentSessionId;
    }

    public WindowsGpuAppScan Scan(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsGpuActivitySnapshot first = reader.Read();
        if (!first.IsAvailable)
        {
            return new WindowsGpuAppScan(
                false,
                "Windows GPU activity counters are unavailable.",
                []);
        }

        // Rate counters need a prior sample before their utilization is useful.
        Thread.Sleep(150);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsGpuActivitySnapshot sample = reader.Read();
        if (!sample.IsAvailable)
        {
            return new WindowsGpuAppScan(
                false,
                "Windows GPU activity counters could not be sampled.",
                []);
        }

        WindowsGpuProcessActivity[] activeProcesses = SelectActiveProcesses(
            sample.Processes);
        Dictionary<int, int> parents = WindowsProcessTree.ReadParents();
        Dictionary<int, ProcessEntry> processTable = ReadProcessTable(
            activeProcesses,
            parents,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WindowsGpuApp> apps = CreateApps(
            activeProcesses,
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

    internal static WindowsGpuProcessActivity[] SelectActiveProcesses(
        IReadOnlyList<WindowsGpuProcessActivity> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        return processes.Where(process => process.IsActive).ToArray();
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

            if (!TryReadProcess(app.ProcessId, out ProcessEntry? current)
                || current.StartTimeUtcTicks != app.StartTimeUtcTicks
                || current.SessionId != currentSessionId
                || !current.SameUser
                || !string.Equals(current.Name, app.Name, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(Failed(
                    app,
                    "The process exited or its identity changed after the GPU scan."));
                continue;
            }

            if (IsProtectedProcessName(current.Name))
            {
                results.Add(Failed(app, "OpenBlade does not close protected processes."));
                continue;
            }

            try
            {
                using Process process = Process.GetProcessById(app.ProcessId);
                bool succeeded;
                if (force)
                {
                    process.Kill(entireProcessTree: true);
                    succeeded = true;
                }
                else
                {
                    succeeded = process.CloseMainWindow();
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
            catch (Exception exception) when (IsRecoverableProcessActionException(exception))
            {
                results.Add(Failed(app, exception.Message));
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return results;
            }
        }
        return results;
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
            process.ExecutablePath,
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
                string? path = TryReadPath(handle);
                entry = new ProcessEntry(
                    processId,
                    ParentProcessId: 0,
                    process.SessionId,
                    process.StartTime.ToUniversalTime().Ticks,
                    process.ProcessName,
                    path,
                    process.MainWindowHandle != 0,
                    IsSameUser(handle));
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

    private bool IsSameUser(nint processHandle)
    {
        if (!OpenProcessToken(processHandle, TokenQuery, out nint token))
        {
            return false;
        }
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User is not null && currentUserSid.Equals(identity.User);
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private static string? TryReadPath(nint processHandle)
    {
        var path = new StringBuilder(MaximumPathCharacters);
        int length = path.Capacity;
        return QueryFullProcessImageNameW(processHandle, 0, path, ref length)
            ? path.ToString()
            : null;
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
        string? ExecutablePath,
        bool HasMainWindow,
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        nint processHandle,
        uint flags,
        StringBuilder executableName,
        ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
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
