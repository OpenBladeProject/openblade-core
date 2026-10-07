using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

// LocalSystem obtains the enrolled console user's existing full administrator
// token. It never manufactures a SYSTEM token in that session or invokes RunAs.
internal static class WindowsCpuVoltageHostLauncher
{
    private static readonly List<IDisposable> UnresolvedChildren = [];
    internal const string Confirmation = "RZ09-0528-CPU-SESSION-THREE-OBSERVATIONS-ONE-CYCLE-READONLY-CLEANUP";
    internal static string CommandLine(string image, string closure, OpenBladeReleaseManifest manifest)
        => string.Join(" ", new[] { image, "cpu-voltage-session-host", "--payload", closure,
            "--source", manifest.SourceRevision, "--manifest-sha256", manifest.CpuVoltageManifestSha256!.ToUpperInvariant(),
            "--expected-service-version", manifest.CpuVoltageServiceVersion!, "--confirm-target", Confirmation }
            .Select(Quote));

    private static string Quote(string value)
    {
        if (value.Contains('"') || value.EndsWith('\\') || value.IndexOf('\0') >= 0)
        { throw new InvalidDataException("Invalid fixed CPU launch argument."); }
        return "\"" + value + "\"";
    }

    internal static IDisposable Launch(string closure, OpenBladeReleaseManifest manifest, string userSid,
        Action<int, long, int, string> bindBeforeResume)
    {
        uint session = WTSGetActiveConsoleSessionId();
        RequireConsole(session);
        using SafeAccessTokenHandle console = ConsoleToken(session);
        TokenIdentity initial = ReadIdentity(console);
        if (initial.User != userSid || initial.Session != session)
        { throw new UnauthorizedAccessException("The enrolled console user changed."); }
        using SafeAccessTokenHandle full = FullToken(console);
        TokenIdentity authority = ReadIdentity(full);
        if (!CanLaunch(initial, authority, userSid, session))
        { throw new UnauthorizedAccessException("The enrolled user has no eligible full administrator token."); }
        if (!DuplicateTokenEx(full, 0x02000000, 0, 2, 1, out SafeAccessTokenHandle primary))
        { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        using (primary)
        {
            string image = Path.Combine(closure, "OpenBlade.Capture.exe");
            CpuVoltageHostState.RequireProtected(closure, true);
            CpuVoltageHostState.RequireProtected(image, false);
            string temporary = Path.Combine(Path.GetDirectoryName(closure)!, "host-temp");
            ProtectedBrokerStorage.EnsureDirectory(temporary);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string environmentText = string.Join('\0', new[] { "SystemRoot=" + windows, "WINDIR=" + windows,
                "PATH=" + Path.Combine(windows, "System32"), "TEMP=" + temporary, "TMP=" + temporary }) + "\0\0";
            nint environment = Marshal.StringToHGlobalUni(environmentText);
            ProcessInformation information = default;
            bool created = false, resumed = false;
            SafeProcessHandle? process = null;
            SafeWaitHandle? thread = null;
            try
            {
                Recheck(initial, session);
                StartupInfo startup = new() { Size = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default" };
                if (!CreateProcessAsUserW(primary, image, new StringBuilder(CommandLine(image, closure, manifest)),
                    0, 0, false, 0x00000004 | 0x00000400 | 0x08000000, environment, closure, ref startup, out information))
                { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                created = true;
                process = new SafeProcessHandle(information.Process, true);
                thread = new SafeWaitHandle(information.Thread, true);
                if (!GetProcessTimes(process, out long birth, out _, out _, out _)
                    || !OpenProcessToken(process, 8, out SafeAccessTokenHandle childToken))
                { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                using (childToken)
                {
                    if (ReadIdentity(childToken) != authority)
                    { throw new UnauthorizedAccessException("The created CPU host token differs from the enrolled authority."); }
                }
                StringBuilder actualImage = new(32768); int imageLength = actualImage.Capacity;
                if (!QueryFullProcessImageNameW(process, 0, actualImage, ref imageLength)
                    || !string.Equals(actualImage.ToString(), image, StringComparison.OrdinalIgnoreCase))
                { throw new IOException("The created CPU host image differs."); }
                bindBeforeResume(checked((int)information.ProcessId), birth, checked((int)session), image);
                Recheck(initial, session);
                // A resume attempt is irreversible even if its result is ambiguous.
                resumed = true;
                if (ResumeThread(thread) != 1)
                { throw new IOException("The CPU host resume result is unresolved; no retry is permitted."); }
                thread.Dispose(); thread = null;
                SafeProcessHandle retained = process; process = null;
                return retained;
            }
            finally
            {
                if (created && process is not null)
                {
                    bool terminal = false;
                    if (!resumed)
                    {
                        // Only this actual, never-resumed child can be retired here.
                        terminal = TerminateProcess(process, 3) && WaitForSingleObject(process, 5000) == 0;
                    }
                    if (!terminal)
                    {
                        lock (UnresolvedChildren)
                        {
                            UnresolvedChildren.Add(process); process = null;
                            if (thread is not null) { UnresolvedChildren.Add(thread); thread = null; }
                        }
                    }
                }
                thread?.Dispose(); process?.Dispose(); Marshal.FreeHGlobal(environment);
            }
        }
    }

    internal sealed record TokenIdentity(string User, long Authentication, uint Session, string Integrity,
        bool Elevated, bool Restricted, bool AppContainer, bool UiAccess, bool Administrator, bool Interactive);
    internal static bool CanLaunch(TokenIdentity console, TokenIdentity full, string enrolled, uint session)
        => session is > 0 and < uint.MaxValue && console.User == enrolled && full.User == enrolled
            && console.Session == session && full.Session == session && console.Interactive && full.Interactive
            && full.Elevated && full.Administrator && full.Integrity == "S-1-16-12288"
            && !console.Restricted && !console.AppContainer && !console.UiAccess
            && !full.Restricted && !full.AppContainer && !full.UiAccess;

    private static void Recheck(TokenIdentity initial, uint session)
    {
        RequireConsole(session);
        using var current = ConsoleToken(session);
        if (ReadIdentity(current) != initial)
        { throw new UnauthorizedAccessException("The enrolled console logon changed before launch."); }
    }
    private static void RequireConsole(uint session)
    {
        if (session is 0 or uint.MaxValue || WTSGetActiveConsoleSessionId() != session)
        { throw new UnauthorizedAccessException("An active local console is required."); }
        foreach ((int kind, bool shortValue) in new[] { (8, false), (16, true) })
        {
            if (!WTSQuerySessionInformationW(0, session, kind, out nint buffer, out int bytes))
            { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            try
            {
                if (bytes < (shortValue ? 2 : 4) || (shortValue ? Marshal.ReadInt16(buffer) : Marshal.ReadInt32(buffer)) != 0)
                { throw new UnauthorizedAccessException("The console session is inactive or remote."); }
            }
            finally { WTSFreeMemory(buffer); }
        }
    }
    private static SafeAccessTokenHandle ConsoleToken(uint session)
    {
        if (!WTSQueryUserToken(session, out SafeAccessTokenHandle token))
        { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return token;
    }
    private static SafeAccessTokenHandle FullToken(SafeAccessTokenHandle console)
    {
        int type = Information(console, 18, Marshal.ReadInt32);
        if (type == 3) { return Information(console, 19, p => new SafeAccessTokenHandle(Marshal.ReadIntPtr(p))); }
        if (type is not (1 or 2) || !DuplicateTokenEx(console, 0x02000000, 0, 2, 1, out var duplicate))
        { throw new UnauthorizedAccessException("The console token has no full administrator authority."); }
        return duplicate;
    }
    private static TokenIdentity ReadIdentity(SafeAccessTokenHandle token)
    {
        using WindowsIdentity identity = new(token.DangerousGetHandle());
        bool EnabledGroup(string sid) => Information(token, 2, p =>
        {
            int count = Marshal.ReadInt32(p), stride = Marshal.SizeOf<SidAttributes>();
            for (int i = 0; i < count; i++)
            {
                var group = Marshal.PtrToStructure<SidAttributes>(p + IntPtr.Size + i * stride);
                if (new SecurityIdentifier(group.Sid).Value == sid && (group.Attributes & 0x14) == 4) { return true; }
            }
            return false;
        });
        return new(identity.User?.Value ?? "", Information(token, 10, p => Marshal.ReadInt64(p, 8)),
            Information(token, 12, p => unchecked((uint)Marshal.ReadInt32(p))),
            Information(token, 25, p => new SecurityIdentifier(Marshal.ReadIntPtr(p)).Value),
            Information(token, 20, Marshal.ReadInt32) != 0, IsTokenRestricted(token),
            Information(token, 29, Marshal.ReadInt32) != 0, Information(token, 26, Marshal.ReadInt32) != 0,
            EnabledGroup("S-1-5-32-544"), EnabledGroup("S-1-5-4"));
    }
    private static T Information<T>(SafeAccessTokenHandle token, int kind, Func<nint, T> read)
    {
        _ = GetTokenInformation(token, kind, 0, 0, out int size);
        if (size is <= 0 or > 65536) { throw new IOException("CPU launch token information size rejected."); }
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, kind, buffer, size, out _)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            return read(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SidAttributes { internal nint Sid; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size; internal string? Reserved, Desktop, Title;
        internal uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        internal ushort Show, ReservedSize; internal nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal nint Process, Thread; internal uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WTSQuerySessionInformationW(nint server, uint session, int kind, out nint buffer, out int size);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint memory);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint buffer, int size, out int returned);
    [DllImport("advapi32.dll")] private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, nint attributes, int level, int type, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string application, StringBuilder command, nint processSecurity, nint threadSecurity, bool inherit, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
