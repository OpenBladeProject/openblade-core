using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.DeploymentClient.Tests;

// Only the two positive launch cases use this branch, and only on an elevated
// GitHub-hosted Windows runner. No installed product process or policy is changed.
internal static class CiNormalLaunchFixture
{
    private static readonly List<IDisposable> RetainedUncertainHandles = [];

    internal sealed record BootstrapReport(string Nonce, bool Hidden, bool Succeeded, string? Error);

    internal static bool IsEligible(DesktopProcessIdentity caller, string? actions, string? environment,
        string? operatingSystem) => actions == "true" && environment == "github-hosted" && operatingSystem == "Windows"
        && caller.Elevated && !caller.Restricted && !caller.AppContainer && !caller.UiAccess
        && caller.IntegritySid == "S-1-16-12288" && caller.UserSid != "S-1-5-18";

    internal static bool CanDeleteAccount(bool created, bool processTerminal, bool jobDrained,
        string? ownedSid, string? currentSid, bool markerMatches) => created && processTerminal && jobDrained
        && !string.IsNullOrEmpty(ownedSid) && ownedSid == currentSid && markerMatches;

    internal static bool IsSuccessfulReport(BootstrapReport? report, string nonce, bool hidden) =>
        report is { Succeeded: true, Error: null } && report.Nonce == nonce && report.Hidden == hidden;

    internal static async Task RunAsync(bool hidden)
    {
        DesktopProcessIdentity caller = DesktopProcessIdentity.ReadCaller();
        RequireHostedCaller(caller);
        string nonce = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "OpenBlade.CiNormalLaunch." + nonce);
        RejectReparseAncestors(Path.GetDirectoryName(root)!);
        if (Directory.Exists(root) || File.Exists(root)) { throw new IOException("The unique fixture root already exists."); }
        Directory.CreateDirectory(root);
        SafeFileHandle job = LaunchFixture.CreateJob(killOnClose: true);
        SafeProcessHandle? process = null;
        SafeFileHandle? thread = null;
        bool terminal = true, drained = false;
        Exception? failure = null;
        try
        {
            string executable = Path.ChangeExtension(typeof(LaunchFixture).Assembly.Location, ".exe");
            var command = new StringBuilder($"\"{executable}\" ci-normal-orchestrator \"{root}\" {hidden} {nonce}");
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1, ShowWindow = 0 };
            IntPtr environment = BuildEnvironment(root, Environment.UserName, normalBootstrap: false);
            ProcessInformation information;
            try
            {
                if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                        0x08000000 | 0x400 | 4, environment, root, ref startup, out information))
                { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            }
            finally { Marshal.FreeHGlobal(environment); }
            terminal = false;
            process = new(information.Process, ownsHandle: true);
            thread = new(information.Thread, ownsHandle: true);
            if (!AssignProcessToJobObject(job, process)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            DesktopProcessIdentity actual = DesktopProcessIdentity.Read(process);
            RequireHostedCaller(actual);
            if (actual.UserSid != caller.UserSid || actual.AuthenticationId != caller.AuthenticationId
                || actual.SessionId != caller.SessionId || !actual.Groups.SequenceEqual(caller.Groups)
                || !actual.Privileges.SequenceEqual(caller.Privileges))
            { throw new InvalidOperationException("The fixture orchestrator identity differs from its retained caller."); }
            if (ResumeThread(thread) != 1) { throw new InvalidOperationException("The fixture orchestrator did not resume from its initial suspension."); }
            long started = Stopwatch.GetTimestamp();
            while (WaitForSingleObject(process, 0) == 258 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(120))
            { await Task.Delay(50); }
            terminal = WaitForSingleObject(process, 0) == 0;
            if (!terminal) { throw new TimeoutException("CI fixture orchestrator timed out; account cleanup remains unconfirmed."); }
            if (!GetExitCodeProcess(process, out uint exitCode)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            BootstrapReport? report = ReadReport(root, "orchestrator-result.json");
            Assert.True(exitCode == 0 && IsSuccessfulReport(report, nonce, hidden),
                $"CI fixture orchestrator exit {exitCode}: {JsonSerializer.Serialize(report)}");
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            try
            {
                if (process is not null)
                {
                    if (WaitForSingleObject(process, 0) != 0 && !TerminateProcess(process, 1))
                    { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                    terminal = WaitForSingleObject(process, 5000) == 0;
                    if (!terminal) { throw new InvalidOperationException("The retained orchestrator termination remains unconfirmed."); }
                }
                LaunchFixture.TerminateAndDrainFixtureJob(job);
                drained = true;
            }
            catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException(failure, cleanup); }
            finally
            {
                if (terminal && drained) { thread?.Dispose(); process?.Dispose(); job.Dispose(); }
                else
                {
                    lock (RetainedUncertainHandles)
                    {
                        if (thread is not null) { RetainedUncertainHandles.Add(thread); }
                        if (process is not null) { RetainedUncertainHandles.Add(process); }
                        RetainedUncertainHandles.Add(job);
                    }
                }
            }
        }
        if (failure is not null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
    }

    private static void RequireHostedCaller(DesktopProcessIdentity caller)
    {
        if (!OperatingSystem.IsWindows() || !IsEligible(caller,
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
                Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"),
                Environment.GetEnvironmentVariable("RUNNER_OS")))
        { throw new InvalidOperationException("The CI account branch requires an explicitly eligible elevated GitHub-hosted Windows runner."); }
    }

    internal static int RunOrchestrator(string root, bool hidden, string nonce)
    {
        RequireHostedCaller(DesktopProcessIdentity.ReadCaller());
        ValidateRoot(root, nonce);
        string? error = null;
        try { RunOwnedAccountAsync(root, hidden, nonce).GetAwaiter().GetResult(); }
        catch (Exception exception) { error = BoundedError(exception); }
        WriteResult(root, "orchestrator-result.json", new(nonce, hidden, error is null, error));
        return error is null ? 0 : 1;
    }

    private static async Task RunOwnedAccountAsync(string root, bool hidden, string nonce)
    {
        DesktopProcessIdentity caller = DesktopProcessIdentity.ReadCaller();
        RequireHostedCaller(caller);
        string username = "obltest_" + nonce[..12];
        string marker = "OpenBlade CI fixture " + nonce;
        string? ownedSid = null;
        bool created = false, processCreated = false, processTerminal = true, jobDrained = false;
        SafeFileHandle? job = null, thread = null;
        SafeProcessHandle? process = null;
        PrivateDesktop? desktop = null;
        Exception? failure = null;
        string stage = "create-cleanup-job";
        try
        {
            job = LaunchFixture.CreateJob(killOnClose: true);
            stage = "create-owned-account";
            string password = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var user = new UserInfo1
            {
                Name = username,
                Password = password,
                Privilege = 1, // USER_PRIV_USER
                Comment = marker,
                Flags = 0x201, // UF_SCRIPT | UF_NORMAL_ACCOUNT
            };
            CheckNet(NetUserAdd(null, 1, ref user, out _), stage);
            created = true;
            user.Password = null;
            var account = ReadAccount(username);
            if (account.Name != username || account.Comment != marker)
            {
                throw new InvalidOperationException("The newly created account ownership marker differs.");
            }
            ownedSid = account.Sid;
            WriteResult(root, "owned-account.json", new(nonce, hidden, false,
                $"Owned temporary account {username}; deletion is not yet confirmed."));
            stage = "add-owned-account-to-users";
            AddToUsers(ownedSid);
            stage = "prepare-owned-directory";
            PrepareDirectory(root, caller.UserSid, ownedSid);
            stage = "copy-fixture-output";
            string executable = CopyFixtureFiles(root);
            stage = "create-private-desktop";
            desktop = new PrivateDesktop();
            desktop.Open(nonce, caller.UserSid, ownedSid);
            stage = "create-normal-bootstrap";
            var command = new StringBuilder($"\"{executable}\" ci-normal-positive \"{root}\" {hidden} {nonce}");
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Flags = 1,
                ShowWindow = 0,
                Desktop = desktop.Name,
            };
            IntPtr environment = BuildEnvironment(root, username, normalBootstrap: true);
            ProcessInformation information;
            try
            {
                // No profile loading, handle inheritance, parent substitution or
                // breakaway. Only this fixed compiled fixture mode is executable.
                if (!CreateProcessWithLogonW(username, ".", password, 0, executable, command,
                        0x08000000 | 0x400 | 4, environment, root, ref startup, out information))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(environment);
                password = string.Empty;
            }
            processCreated = true;
            processTerminal = false;
            process = new(information.Process, ownsHandle: true);
            thread = new(information.Thread, ownsHandle: true);
            stage = "assign-owned-cleanup-job";
            if (!AssignProcessToJobObject(job, process)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            stage = "verify-suspended-normal-identity";
            DesktopProcessIdentity actual = DesktopProcessIdentity.Read(process);
            if (!actual.IsNormal || actual.UserSid != ownedSid || actual.SessionId != caller.SessionId)
            {
                throw new InvalidOperationException("The suspended bootstrap is not the owned normal user in the caller session.");
            }
            stage = "resume-normal-bootstrap";
            if (ResumeThread(thread) != 1) { throw new InvalidOperationException("The bootstrap did not resume from its initial suspension."); }
            stage = "wait-normal-bootstrap";
            long started = Stopwatch.GetTimestamp();
            while (WaitForSingleObject(process, 0) == 258 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(50);
            }
            processTerminal = WaitForSingleObject(process, 0) == 0;
            if (!processTerminal) { throw new TimeoutException("The retained normal bootstrap did not terminate in sixty seconds."); }
            if (!GetExitCodeProcess(process, out uint exitCode)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            stage = "read-normal-bootstrap-result";
            BootstrapReport? report = ReadReport(root, "bootstrap-result.json");
            Assert.True(exitCode == 0 && IsSuccessfulReport(report, nonce, hidden),
                $"Normal bootstrap exit {exitCode}: {JsonSerializer.Serialize(report)}");
        }
        catch (Exception exception)
        {
            failure = new InvalidOperationException($"CI normal launch fixture failed at {stage}.", exception);
        }
        finally
        {
            try
            {
                if (processCreated && process is not null)
                {
                    if (WaitForSingleObject(process, 0) != 0)
                    {
                        if (!TerminateProcess(process, 1)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                    }
                    processTerminal = WaitForSingleObject(process, 5000) == 0;
                    if (!processTerminal) { throw new InvalidOperationException("The owned bootstrap termination remains unconfirmed."); }
                }
                if (job is not null)
                {
                    LaunchFixture.TerminateAndDrainFixtureJob(job);
                    jobDrained = true;
                }
                // Only this isolated orchestrator changes its process station.
                // No user object is closed while an owned child may still use it.
                desktop?.Dispose();
                if (created)
                {
                    var current = ReadAccount(username);
                    if (!CanDeleteAccount(created, processTerminal, jobDrained, ownedSid, current.Sid,
                            current.Name == username && current.Comment == marker))
                    {
                        throw new InvalidOperationException("Account cleanup refused: exact ownership and terminal job were not confirmed.");
                    }
                    CheckNet(NetUserDel(null, username), "delete-owned-account");
                    WriteResult(root, "account-deleted.json", new(nonce, hidden, true, null));
                }
            }
            catch (Exception cleanup)
            {
                failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
                if (desktop is not null)
                {
                    lock (RetainedUncertainHandles) { RetainedUncertainHandles.Add(desktop); }
                }
            }
            finally
            {
                if (processTerminal && jobDrained)
                {
                    thread?.Dispose(); process?.Dispose(); job?.Dispose();
                }
                else
                {
                    // Keep actual handles and account ownership on uncertainty.
                    // The disposable hosted VM owns final cleanup; never adopt a PID.
                    lock (RetainedUncertainHandles)
                    {
                        if (thread is not null) { RetainedUncertainHandles.Add(thread); }
                        if (process is not null) { RetainedUncertainHandles.Add(process); }
                        if (job is not null) { RetainedUncertainHandles.Add(job); }
                    }
                }
            }
        }
        if (failure is not null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
    }

    internal static int RunBootstrap(string root, bool hidden, string nonce)
    {
        ValidateRoot(root, nonce);
        string? error = null;
        try
        {
            if (!DesktopProcessIdentity.ReadCaller().IsNormal)
            {
                throw new InvalidOperationException("The fixed normal bootstrap context is invalid.");
            }
            IndependentInstalledProcessTests.RunPositiveFixtureAsync(hidden).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            error = BoundedError(exception);
        }
        WriteResult(root, "bootstrap-result.json", new(nonce, hidden, error is null, error));
        return error is null ? 0 : 1;
    }

    private static string BoundedError(Exception exception)
    {
        string text = exception.ToString();
        return text.Length > 16000 ? text[..16000] : text;
    }

    private static void ValidateRoot(string root, string nonce)
    {
        if (!Guid.TryParseExact(nonce, "N", out _) || !Path.IsPathFullyQualified(root)
            || Path.GetFileName(root) != "OpenBlade.CiNormalLaunch." + nonce)
        { throw new InvalidOperationException("The fixed CI fixture root is invalid."); }
        RejectReparseAncestors(root);
    }

    private static BootstrapReport? ReadReport(string root, string name)
    {
        string path = Path.Combine(root, name);
        if (new FileInfo(path).Length > 32768) { throw new InvalidDataException("The fixture report exceeded its bound."); }
        return JsonSerializer.Deserialize<BootstrapReport>(File.ReadAllText(path));
    }

    private static void WriteResult(string root, string name, BootstrapReport report)
    {
        using var output = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(output, report);
        output.Flush(flushToDisk: true);
    }

    private static (string Name, string Comment, string Sid) ReadAccount(string username)
    {
        CheckNet(NetUserGetInfo(null, username, 23, out IntPtr buffer), "read-owned-account");
        try
        {
            var value = Marshal.PtrToStructure<UserInfo23>(buffer);
            return (value.Name, value.Comment, new SecurityIdentifier(value.Sid).Value);
        }
        finally { _ = NetApiBufferFree(buffer); }
    }

    private static void AddToUsers(string sid)
    {
        string group = ((NTAccount)new SecurityIdentifier("S-1-5-32-545").Translate(typeof(NTAccount))).Value;
        group = group[(group.LastIndexOf('\\') + 1)..];
        var identity = new SecurityIdentifier(sid);
        byte[] bytes = new byte[identity.BinaryLength];
        identity.GetBinaryForm(bytes, 0);
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            uint result = NetLocalGroupAddMembers(null, group, 0, ref pointer, 1);
            if (result != 1378) { CheckNet(result, "add-owned-account-to-users"); }
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static void PrepareDirectory(string root, string ownerSid, string userSid)
    {
        RejectReparseAncestors(root);
        string sddl = $"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;{ownerSid})(A;OICI;0x1301bf;;;{userSid})S:(ML;OICI;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out IntPtr descriptor, out _))
        { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            if (!GetSecurityDescriptorDacl(descriptor, out bool daclPresent, out IntPtr dacl, out _)
                || !daclPresent || !GetSecurityDescriptorSacl(descriptor, out bool saclPresent, out IntPtr sacl, out _)
                || !saclPresent) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            // DACL and medium mandatory label apply only to this new fixture root.
            CheckNet(SetNamedSecurityInfoW(root, 1, 0x80000014, IntPtr.Zero, IntPtr.Zero, dacl, sacl),
                "grant-owned-directory-access");
        }
        finally { _ = LocalFree(descriptor); }
        foreach (string child in new[] { "temp", "profile", "local", "roaming" })
        { Directory.CreateDirectory(Path.Combine(root, child)); }
    }

    private static void RejectReparseAncestors(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            { throw new IOException("The fixture root ancestry is missing or reparsed."); }
        }
    }

    private static string CopyFixtureFiles(string root)
    {
        string source = Path.GetDirectoryName(typeof(LaunchFixture).Assembly.Location)!;
        RejectReparseAncestors(source);
        string destination = Path.Combine(root, "fixture");
        Directory.CreateDirectory(destination);
        int count = 0;
        long total = 0;
        // This apphost runs Main directly, not testhost/MSBuild. Its managed
        // dependencies are alongside it; no NuGet or source-directory ACL changes.
        foreach (string path in Directory.EnumerateFiles(source))
        {
            if (++count > 128 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            { throw new InvalidDataException("The fixture output is reparsed or exceeds its file bound."); }
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 16 * 1024 * 1024 || (total += input.Length) > 64 * 1024 * 1024)
            { throw new InvalidDataException("The fixture output exceeds its byte bound."); }
            using var output = new FileStream(Path.Combine(destination, Path.GetFileName(path)), FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
        foreach (string name in new[]
        {
            "OpenBlade.DeploymentClient.Tests.exe", "OpenBlade.DeploymentClient.Tests.dll",
            "OpenBlade.DeploymentClient.Tests.deps.json", "OpenBlade.DeploymentClient.Tests.runtimeconfig.json",
            "OpenBlade.DeploymentClient.dll", "OpenBlade.Contracts.dll",
            "xunit.assert.dll", "xunit.core.dll", "xunit.abstractions.dll",
        })
        {
            if (!File.Exists(Path.Combine(destination, name)))
            { throw new InvalidDataException("The fixed fixture output is incomplete."); }
        }
        return Path.Combine(destination, "OpenBlade.DeploymentClient.Tests.exe");
    }

    private static IntPtr BuildEnvironment(string root, string username, bool normalBootstrap)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string runtimeRoot = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!.FullName;
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["WINDIR"] = windows,
            ["PATH"] = Path.Combine(windows, "System32"),
            ["TEMP"] = normalBootstrap ? Path.Combine(root, "temp") : root,
            ["TMP"] = normalBootstrap ? Path.Combine(root, "temp") : root,
            ["USERPROFILE"] = normalBootstrap ? Path.Combine(root, "profile") : root,
            ["LOCALAPPDATA"] = normalBootstrap ? Path.Combine(root, "local") : root,
            ["APPDATA"] = normalBootstrap ? Path.Combine(root, "roaming") : root,
            ["USERNAME"] = username,
            ["USERDOMAIN"] = Environment.MachineName,
            ["DOTNET_ROOT"] = runtimeRoot,
            ["DOTNET_ROOT_X64"] = runtimeRoot,
            ["GITHUB_ACTIONS"] = "true",
            ["RUNNER_ENVIRONMENT"] = "github-hosted",
            ["RUNNER_OS"] = "Windows",
        };
        return Marshal.StringToHGlobalUni(string.Join('\0', values.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
    }

    private static void CheckNet(uint result, string operation)
    {
        if (result != 0) { throw new Win32Exception(unchecked((int)result), $"{operation} failed with code {result}."); }
    }

    private sealed class PrivateDesktop : IDisposable
    {
        private IntPtr originalStation, station, desktop;
        internal string Name { get; private set; } = string.Empty;

        internal void Open(string nonce, string ownerSid, string userSid)
        {
            string stationName = "OpenBladeCi_" + nonce;
            string sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;{ownerSid})(A;;GA;;;{userSid})S:(ML;;NW;;;ME)";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out IntPtr descriptor, out _))
            { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            try
            {
                var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
                originalStation = GetProcessWindowStation();
                if (originalStation == IntPtr.Zero) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                // CWF_CREATE_ONLY refuses collision; explicit noninheritable SD
                // grants access only to SYSTEM, the creator and this new account.
                station = CreateWindowStationW(stationName, 1, 0x000F037F, ref security);
                if (station == IntPtr.Zero || !SetProcessWindowStation(station))
                { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                desktop = CreateDesktopW("fixture", IntPtr.Zero, IntPtr.Zero, 0, 0x000F01FF, ref security);
                if (desktop == IntPtr.Zero) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                Name = stationName + "\\fixture";
            }
            finally { _ = LocalFree(descriptor); }
        }

        public void Dispose()
        {
            if (originalStation != IntPtr.Zero && !SetProcessWindowStation(originalStation))
            { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            if (desktop != IntPtr.Zero)
            {
                if (!CloseDesktop(desktop)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                desktop = IntPtr.Zero;
            }
            if (station != IntPtr.Zero)
            {
                if (!CloseWindowStation(station)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                station = IntPtr.Zero;
            }
            originalStation = IntPtr.Zero; // Borrowed handle; never closed here.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Size;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1
    {
        public string Name;
        public string? Password;
        public uint PasswordAge, Privilege;
        public string? HomeDirectory;
        public string Comment;
        public uint Flags;
        public string? ScriptPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo23
    {
        public string Name, FullName, Comment;
        public uint Flags;
        public IntPtr Sid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint NetUserAdd(string? server, uint level, ref UserInfo1 user, out uint parameterError);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint NetUserGetInfo(string? server, string user, uint level, out IntPtr buffer);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint NetUserDel(string? server, string user);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint NetLocalGroupAddMembers(string? server, string group, uint level, ref IntPtr buffer, uint count);
    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern uint NetApiBufferFree(IntPtr buffer);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithLogonW(string username, string domain, string password, uint logonFlags,
        string application, StringBuilder command, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateWindowStationW(string name, uint flags, uint access, ref SecurityAttributes security);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode, uint flags, uint access,
        ref SecurityAttributes security);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWindowStation(IntPtr station);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseWindowStation(IntPtr station);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision,
        out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present,
        out IntPtr dacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present,
        out IntPtr sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint SetNamedSecurityInfoW(string name, uint type, uint information, IntPtr owner,
        IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr value);
}
