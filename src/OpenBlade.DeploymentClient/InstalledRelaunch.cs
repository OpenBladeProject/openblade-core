using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;

namespace OpenBlade.DeploymentClient;

internal interface IClientIntegritySource
{
    bool IsElevated();
}

internal interface IClientSessionSource
{
    bool IsActiveLocalConsoleSession();
}

internal sealed class WindowsClientSessionSource : IClientSessionSource
{
    private static readonly Lazy<int> CachedCurrentProcessSessionId = new(ReadCurrentProcessSessionId);

    internal static int CurrentProcessSessionId => CachedCurrentProcessSessionId.Value;

    public bool IsActiveLocalConsoleSession()
    {
        uint active = WTSGetActiveConsoleSessionId();
        return active != uint.MaxValue
            && CurrentProcessSessionId == unchecked((int)active);
    }

    private static int ReadCurrentProcessSessionId()
    {
        using Process process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}

internal sealed class WindowsClientIntegritySource : IClientIntegritySource
{
    public bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        TokenElevation elevation = default;
        if (!GetTokenInformation(
                identity.AccessToken.DangerousGetHandle(),
                TokenInformationClass.TokenElevation,
                ref elevation,
                Marshal.SizeOf<TokenElevation>(),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return elevation.TokenIsElevated != 0;
    }

    private struct TokenElevation
    {
        internal int TokenIsElevated;
    }

    private enum TokenInformationClass
    {
        TokenElevation = 20,
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TokenInformationClass tokenInformationClass,
        ref TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}

internal sealed record InstalledPayloadPaths(
    string InstallDirectory,
    string SessionAgentExecutable,
    string TrayExecutable);

internal static class InstalledPayloadPathPolicy
{
    internal static InstalledPayloadPaths? GetExpectedPaths()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles) || !Path.IsPathFullyQualified(programFiles))
        {
            return null;
        }

        string installDirectory = Path.Combine(Path.GetFullPath(programFiles), "OpenBlade");
        return new InstalledPayloadPaths(
            installDirectory,
            Path.Combine(installDirectory, "OpenBlade.SessionAgent.exe"),
            Path.Combine(installDirectory, "OpenBlade.Tray.exe"));
    }

    internal static bool IsExact(InstalledPayloadPaths actual, InstalledPayloadPaths expected)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expected);
        try
        {
            return string.Equals(
                    Path.GetFullPath(actual.InstallDirectory),
                    Path.GetFullPath(expected.InstallDirectory),
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    Path.GetFullPath(actual.SessionAgentExecutable),
                    Path.GetFullPath(expected.SessionAgentExecutable),
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    Path.GetFullPath(actual.TrayExecutable),
                    Path.GetFullPath(expected.TrayExecutable),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

internal interface IInstalledPayloadVerifier
{
    InstalledPayloadPaths? GetExpectedPaths();

    InstalledPayloadPaths? GetVerifiedPaths();
}

internal sealed record InstalledPayloadSecurityEvidence(
    bool InstallDirectoryExists,
    bool SessionAgentExists,
    bool TrayExists,
    bool InstallDirectoryIsReparsePoint,
    bool SessionAgentIsReparsePoint,
    bool TrayIsReparsePoint,
    bool InstallDirectoryCallerWritable,
    bool SessionAgentCallerWritable,
    bool TrayCallerWritable);

internal static class InstalledPayloadVerificationPolicy
{
    internal static bool IsTrusted(
        InstalledPayloadPaths actual,
        InstalledPayloadPaths expected,
        InstalledPayloadSecurityEvidence evidence) =>
        InstalledPayloadPathPolicy.IsExact(actual, expected)
        && evidence.InstallDirectoryExists
        && evidence.SessionAgentExists
        && evidence.TrayExists
        && !evidence.InstallDirectoryIsReparsePoint
        && !evidence.SessionAgentIsReparsePoint
        && !evidence.TrayIsReparsePoint
        && !evidence.InstallDirectoryCallerWritable
        && !evidence.SessionAgentCallerWritable
        && !evidence.TrayCallerWritable;
}

internal sealed class WindowsInstalledPayloadVerifier : IInstalledPayloadVerifier
{
    private const FileSystemRights DangerousWriteRights = FileSystemRights.WriteData
        | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    public InstalledPayloadPaths? GetExpectedPaths() =>
        InstalledPayloadPathPolicy.GetExpectedPaths();

    public InstalledPayloadPaths? GetVerifiedPaths()
    {
        InstalledPayloadPaths? expected = InstalledPayloadPathPolicy.GetExpectedPaths();
        if (expected is null)
        {
            return null;
        }

        bool directoryExists = Directory.Exists(expected.InstallDirectory);
        bool sessionAgentExists = File.Exists(expected.SessionAgentExecutable);
        bool trayExists = File.Exists(expected.TrayExecutable);
        bool directoryReparse = directoryExists && HasReparsePoint(expected.InstallDirectory);
        bool sessionAgentReparse = sessionAgentExists
            && HasReparsePoint(expected.SessionAgentExecutable);
        bool trayReparse = trayExists && HasReparsePoint(expected.TrayExecutable);
        if (!directoryExists
            || !sessionAgentExists
            || !trayExists
            || directoryReparse
            || sessionAgentReparse
            || trayReparse)
        {
            return null;
        }

        InstalledPayloadSecurityEvidence evidence = new(
            directoryExists,
            sessionAgentExists,
            trayExists,
            directoryReparse,
            sessionAgentReparse,
            trayReparse,
            IsCallerWritable(new DirectoryInfo(expected.InstallDirectory)),
            IsCallerWritable(new FileInfo(expected.SessionAgentExecutable)),
            IsCallerWritable(new FileInfo(expected.TrayExecutable)));
        if (!InstalledPayloadVerificationPolicy.IsTrusted(expected, expected, evidence))
        {
            return null;
        }

        return expected;
    }

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    internal static bool IsCallerWritable(FileSystemInfo path)
    {
        FileSystemSecurity security = path switch
        {
            DirectoryInfo directory => directory.GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner),
            FileInfo file => file.GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner),
            _ => throw new InvalidOperationException("Unsupported installed payload path type."),
        };
        HashSet<SecurityIdentifier> effectiveIdentities = GetEffectiveCallerIdentities();

        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
            && effectiveIdentities.Contains(owner))
        {
            return true;
        }

        FileSystemRights allowed = 0;
        FileSystemRights denied = 0;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is not SecurityIdentifier sid
                || !effectiveIdentities.Contains(sid))
            {
                continue;
            }

            if (rule.AccessControlType == AccessControlType.Deny)
            {
                denied |= rule.FileSystemRights;
            }
            else
            {
                allowed |= rule.FileSystemRights;
            }
        }

        return HasDangerousWriteAccess(allowed, denied);
    }

    internal static bool HasDangerousWriteAccess(
        FileSystemRights allowed,
        FileSystemRights denied)
        => (allowed & DangerousWriteRights & ~denied) != 0;

    internal static HashSet<SecurityIdentifier> GetEffectiveCallerIdentities()
    {
        // WindowsPrincipal must duplicate a primary token before checking group
        // membership. Query-only access makes that documented check fail with
        // AccessDenied instead of reporting the filtered-token group state.
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        WindowsPrincipal principal = new(identity);
        HashSet<SecurityIdentifier> effectiveIdentities = [];
        if (identity.User is not null)
        {
            effectiveIdentities.Add(identity.User);
        }

        if (identity.Groups is not null)
        {
            foreach (IdentityReference group in identity.Groups)
            {
                if (group is SecurityIdentifier sid && principal.IsInRole(sid))
                {
                    effectiveIdentities.Add(sid);
                }
            }
        }

        return effectiveIdentities;
    }
}

internal sealed record InstalledProcessLaunchPlan(
    string FileName,
    string WorkingDirectory,
    bool CreateNoWindow,
    ProcessWindowStyle WindowStyle)
{
    internal static InstalledProcessLaunchPlan SessionAgent(InstalledPayloadPaths paths) => new(
        paths.SessionAgentExecutable,
        paths.InstallDirectory,
        CreateNoWindow: true,
        ProcessWindowStyle.Hidden);

    internal static InstalledProcessLaunchPlan Tray(InstalledPayloadPaths paths) => new(
        paths.TrayExecutable,
        paths.InstallDirectory,
        CreateNoWindow: false,
        ProcessWindowStyle.Normal);
}

internal sealed record InstalledProcessInventory(
    bool IsUnambiguous,
    int SessionAgentCount,
    int TrayCount)
{
    internal bool IsEmpty => IsUnambiguous
        && SessionAgentCount == 0
        && TrayCount == 0;

    internal bool HasOnlySessionAgent => IsUnambiguous
        && SessionAgentCount == 1
        && TrayCount == 0;

    internal bool HasExactFinalState => IsUnambiguous
        && SessionAgentCount == 1
        && TrayCount == 1;

    internal bool TryGetRestorableShape(out InstalledProcessShape shape)
    {
        shape = (SessionAgentCount, TrayCount) switch
        {
            (0, 0) => InstalledProcessShape.None,
            (1, 0) => InstalledProcessShape.SessionAgentOnly,
            (1, 1) => InstalledProcessShape.SessionAgentAndTray,
            _ => default,
        };
        return IsUnambiguous
            && (SessionAgentCount, TrayCount) is (0, 0) or (1, 0) or (1, 1);
    }
}

internal enum InstalledProcessShape
{
    None,
    SessionAgentOnly,
    SessionAgentAndTray,
}

internal interface IInstalledProcessInventory
{
    InstalledProcessInventory Inspect(InstalledPayloadPaths paths);
}

internal sealed class WindowsInstalledProcessInventory : IInstalledProcessInventory
{
    private const uint TokenQuery = 0x0008;

    public InstalledProcessInventory Inspect(InstalledPayloadPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        int currentSession = WindowsClientSessionSource.CurrentProcessSessionId;
        ProcessCount sessionAgent = CountExactNormalProcess(
            paths.SessionAgentExecutable,
            currentSession);
        ProcessCount tray = CountExactNormalProcess(paths.TrayExecutable, currentSession);
        return new InstalledProcessInventory(
            !sessionAgent.Ambiguous && !tray.Ambiguous,
            sessionAgent.Count,
            tray.Count);
    }

    private static ProcessCount CountExactNormalProcess(string expectedPath, int currentSession)
    {
        int count = 0;
        bool ambiguous = false;
        string processName = Path.GetFileNameWithoutExtension(expectedPath);
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    string? actualPath = process.MainModule?.FileName;
                    if (actualPath is null)
                    {
                        ambiguous = true;
                        continue;
                    }

                    if (!ShouldInspectIntegrity(
                            expectedPath,
                            actualPath,
                            currentSession,
                            process.SessionId,
                            out bool pathOrSessionAmbiguous))
                    {
                        ambiguous |= pathOrSessionAmbiguous;
                        continue;
                    }

                    if (IsElevated(process))
                    {
                        ambiguous = true;
                        continue;
                    }

                    count++;
                }
                catch (Exception exception) when (exception is
                    Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                {
                    ambiguous = true;
                }
            }
        }

        return new ProcessCount(count, ambiguous);
    }

    internal static bool ShouldInspectIntegrity(
        string expectedPath,
        string actualPath,
        int currentSession,
        int processSession,
        out bool ambiguous)
    {
        bool isExactPath = string.Equals(
            Path.GetFullPath(actualPath),
            Path.GetFullPath(expectedPath),
            StringComparison.OrdinalIgnoreCase);
        ambiguous = isExactPath && processSession != currentSession;
        return isExactPath && !ambiguous;
    }

    private static bool IsElevated(Process process)
    {
        if (!OpenProcessToken(process.Handle, TokenQuery, out SafeAccessTokenHandle token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using (token)
        {
            TokenElevation elevation = default;
            if (!GetTokenInformation(
                    token,
                    TokenInformationClass.TokenElevation,
                    ref elevation,
                    Marshal.SizeOf<TokenElevation>(),
                    out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return elevation.TokenIsElevated != 0;
        }
    }

    private readonly record struct ProcessCount(int Count, bool Ambiguous);

    private struct TokenElevation
    {
        internal int TokenIsElevated;
    }

    private enum TokenInformationClass
    {
        TokenElevation = 20,
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        ref TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}

internal sealed record InstalledShutdownEndpoints(string? SessionAgent, string? Tray)
{
    internal IReadOnlyList<string> InSignalOrder => (SessionAgent, Tray) switch
    {
        (not null, not null) => [Tray, SessionAgent],
        (not null, null) => [SessionAgent],
        _ => [],
    };
}

internal static class InstalledShutdownEndpointPolicy
{
    internal static InstalledShutdownEndpoints? Create(
        InstalledPayloadPaths paths,
        InstalledProcessShape shape)
    {
        ArgumentNullException.ThrowIfNull(paths);
        InstalledPayloadPaths? expected = InstalledPayloadPathPolicy.GetExpectedPaths();
        if (expected is null || !InstalledPayloadPathPolicy.IsExact(paths, expected))
        {
            return null;
        }

        return shape switch
        {
            InstalledProcessShape.None => new(null, null),
            InstalledProcessShape.SessionAgentOnly => new(
                SessionAgentShutdownEndpoint.ForExecutablePath(
                    expected.SessionAgentExecutable),
                null),
            InstalledProcessShape.SessionAgentAndTray => new(
                SessionAgentShutdownEndpoint.ForExecutablePath(
                    expected.SessionAgentExecutable),
                TrayShutdownEndpoint.ForExecutablePath(expected.TrayExecutable)),
            _ => null,
        };
    }
}

internal interface IInstalledShutdownEvent : IDisposable
{
    bool Signal();
}

internal interface IInstalledShutdownEventSource
{
    IInstalledShutdownEvent? TryOpen(string eventName);
}

internal sealed class WindowsInstalledShutdownEventSource : IInstalledShutdownEventSource
{
    public IInstalledShutdownEvent? TryOpen(string eventName) =>
        EventWaitHandle.TryOpenExisting(eventName, out EventWaitHandle? handle)
            ? new WindowsInstalledShutdownEvent(handle)
            : null;

    private sealed class WindowsInstalledShutdownEvent(EventWaitHandle handle)
        : IInstalledShutdownEvent
    {
        public bool Signal() => handle.Set();

        public void Dispose() => handle.Dispose();
    }
}

internal interface IInstalledProcessShutdown
{
    Task<bool> ShutdownAsync(
        InstalledPayloadPaths paths,
        InstalledProcessShape shape,
        CancellationToken cancellationToken);
}

internal sealed class WindowsInstalledProcessShutdown(
    IInstalledShutdownEventSource events,
    IInstalledProcessInventory inventory) : IInstalledProcessShutdown
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InventoryPollInterval = TimeSpan.FromMilliseconds(100);

    public async Task<bool> ShutdownAsync(
        InstalledPayloadPaths paths,
        InstalledProcessShape shape,
        CancellationToken cancellationToken)
    {
        InstalledShutdownEndpoints? endpoints = InstalledShutdownEndpointPolicy.Create(
            paths,
            shape);
        if (endpoints is null || shape == InstalledProcessShape.None)
        {
            return false;
        }

        List<IInstalledShutdownEvent> opened = [];
        try
        {
            foreach (string endpoint in endpoints.InSignalOrder)
            {
                IInstalledShutdownEvent? shutdownEvent = events.TryOpen(endpoint);
                if (shutdownEvent is null)
                {
                    return false;
                }

                opened.Add(shutdownEvent);
            }

            foreach (IInstalledShutdownEvent shutdownEvent in opened)
            {
                if (!shutdownEvent.Signal())
                {
                    return false;
                }
            }

            long started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < ExitTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (inventory.Inspect(paths).IsEmpty)
                {
                    return true;
                }

                await Task.Delay(InventoryPollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }

            return inventory.Inspect(paths).IsEmpty;
        }
        finally
        {
            opened.ForEach(handle => handle.Dispose());
        }
    }
}

internal static class InstalledProcessLaunchPolicy
{
    internal static bool IsAllowed(
        InstalledProcessLaunchPlan plan,
        InstalledPayloadPaths expected)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(expected);
        try
        {
            bool workingDirectoryMatches = string.Equals(
                Path.GetFullPath(plan.WorkingDirectory),
                Path.GetFullPath(expected.InstallDirectory),
                StringComparison.OrdinalIgnoreCase);
            bool isSessionAgent = string.Equals(
                    Path.GetFullPath(plan.FileName),
                    Path.GetFullPath(expected.SessionAgentExecutable),
                    StringComparison.OrdinalIgnoreCase)
                && plan.CreateNoWindow
                && plan.WindowStyle == ProcessWindowStyle.Hidden;
            bool isTray = string.Equals(
                    Path.GetFullPath(plan.FileName),
                    Path.GetFullPath(expected.TrayExecutable),
                    StringComparison.OrdinalIgnoreCase)
                && !plan.CreateNoWindow
                && plan.WindowStyle == ProcessWindowStyle.Normal;
            return workingDirectoryMatches && (isSessionAgent || isTray);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

internal interface ILaunchedDeploymentProcess : IDisposable
{
    bool HasExited { get; }

    Task<bool> RemainsAliveAsync(TimeSpan duration);
}

internal interface IDeploymentProcessLauncher
{
    IDeploymentLaunchContext Prepare();
}

internal interface IDeploymentLaunchContext : IDisposable
{
    void Validate();

    ILaunchedDeploymentProcess? Launch(InstalledProcessLaunchPlan plan);
}

internal sealed class WindowsDeploymentProcessLauncher : IDeploymentProcessLauncher
{
    public IDeploymentLaunchContext Prepare() => WindowsDesktopLaunchContext.Prepare();
}

internal sealed class WindowsLaunchedDeploymentProcess(Process process) : ILaunchedDeploymentProcess
{
    public bool HasExited => process.HasExited;

    public async Task<bool> RemainsAliveAsync(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (process.HasExited)
        {
            return false;
        }

        Task exit = process.WaitForExitAsync();
        Task completed = await Task.WhenAny(exit, Task.Delay(duration)).ConfigureAwait(false);
        return completed != exit && !process.HasExited;
    }

    public void Dispose() => process.Dispose();
}
