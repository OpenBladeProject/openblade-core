using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.DeploymentClient.Tests;

public sealed class IndependentInstalledProcessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Launch_OutlivesCallerJobs_WithDisposableParentAndNoInheritedHandles(bool hidden)
    {
        DesktopProcessIdentity caller = DesktopProcessIdentity.ReadCaller();
        if (caller.IsNormal)
        {
            await RunPositiveFixtureAsync(hidden);
            return;
        }

        await CiNormalLaunchFixture.RunAsync(hidden);
    }

    internal static async Task RunPositiveFixtureAsync(bool hidden)
    {
        using FixtureScope fixture = new();
        await fixture.StartAsync(hidden);
        File.WriteAllText(Path.Combine(fixture.Root, "launch"), "launch");
        LaunchReport report = await fixture.ReadReportAsync();
        Assert.True(report.Error is null,
            $"Fixture launch failed: {report.Error}; {JsonSerializer.Serialize(report.Failure)}");
        using Process child = Process.GetProcessById(report.ProcessId);
        _ = child.Handle;
        try
        {
            Assert.False(LaunchFixture.IsInJob(child.SafeHandle, fixture.CallerOuterJob));
            Assert.False(LaunchFixture.IsInJob(child.SafeHandle, fixture.CallerInnerJob));
            Assert.True(LaunchFixture.IsInJob(child.SafeHandle, fixture.ParentJob));
            fixture.CloseCallerJobs();
            Assert.True(fixture.Coordinator!.WaitForExit(5000));
            await Task.Delay(250);
            Assert.False(child.HasExited, "The installed-process fixture must survive caller-job closure.");
            Assert.False(LaunchFixture.IsSameRemoteObject(child.SafeHandle,
                report.CallerHandleCandidate, fixture.CallerEvent.SafeWaitHandle));
            Assert.False(LaunchFixture.IsSameRemoteObject(child.SafeHandle,
                report.ParentHandleCandidate, fixture.ParentEvent.SafeWaitHandle));
            Assert.True(DesktopProcessIdentity.ReadCaller().IsSameNormalIdentity(report.Identity!));
            Assert.Equal(fixture.Root, report.WorkingDirectory);
        }
        finally
        {
            File.WriteAllText(Path.Combine(fixture.Root, "child.exit"), "exit");
            FixtureScope.StopExactFixture(child);
        }
    }

    [Fact]
    public async Task Launch_RejectsDeadRetainedParent_WithoutFallbackOrChildExecution()
    {
        using FixtureScope fixture = new();
        await fixture.StartAsync(hidden: true);
        File.WriteAllText(Path.Combine(fixture.Root, "parent.exit"), "exit");
        Assert.True(fixture.Parent!.WaitForExit(5000));
        File.WriteAllText(Path.Combine(fixture.Root, "launch"), "launch");
        LaunchReport report = await fixture.ReadReportAsync();
        Assert.Equal(nameof(UnauthorizedAccessException), report.Error);
        Assert.Equal(0, report.ProcessId);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "child.started")));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("logon")]
    [InlineData("session")]
    [InlineData("integrity")]
    [InlineData("elevated")]
    [InlineData("restricted")]
    [InlineData("appcontainer")]
    [InlineData("groups")]
    [InlineData("privileges")]
    [InlineData("uiaccess")]
    public void IdentityPolicy_RejectsDifferentOrRestrictedSecurityContext(string change)
    {
        DesktopProcessIdentity caller = new("S-1-5-21-1", 10, 1, "S-1-16-8192", false, false, false,
            ["S-1-5-11:00000007"], ["0000000000000017:00000003"]);
        DesktopProcessIdentity parent = change switch
        {
            "user" => caller with { UserSid = "S-1-5-21-2" },
            "logon" => caller with { AuthenticationId = 11 },
            "session" => caller with { SessionId = 2 },
            "integrity" => caller with { IntegritySid = "S-1-16-4096" },
            "elevated" => caller with { Elevated = true },
            "restricted" => caller with { Restricted = true },
            "appcontainer" => caller with { AppContainer = true },
            "groups" => caller with { Groups = ["S-1-5-11:00000010"] },
            "uiaccess" => caller with { UiAccess = true },
            _ => caller with { Privileges = [] },
        };
        Assert.True(caller.IsSameNormalIdentity(caller));
        Assert.False(caller.IsSameNormalIdentity(parent));
        Assert.False(parent.IsSameNormalIdentity(caller));
    }

    [Fact]
    public void LaunchFailure_RoundTripsExceptionAndSanitizedIdentityDetails()
    {
        DesktopProcessIdentity caller = new("private-user", 123, 4, "S-1-16-8192", false, false, false,
            ["private-group"], ["private-privilege"]);
        DesktopProcessIdentity parent = caller with { Elevated = true, AuthenticationId = 456, Groups = [] };
        Exception exception = Record.Exception((Action)(() => throw new UnauthorizedAccessException("synthetic rejection")))!;
        LaunchFailure failure = new("start", exception.Message, exception.HResult, exception.StackTrace,
            false, LaunchIdentityDiagnostics.From(caller, parent));
        LaunchReport original = new(0, 0, 0, "fixture", null, exception.GetType().Name, failure);
        string json = JsonSerializer.Serialize(original);
        LaunchReport copy = JsonSerializer.Deserialize<LaunchReport>(json)!;

        Assert.Equal(original, copy);
        Assert.NotNull(copy.Failure!.StackTrace);
        Assert.True(copy.Failure.Identity!.CallerNormal);
        Assert.False(copy.Failure.Identity.ParentNormal);
        Assert.True(copy.Failure.Identity.AuthenticationMismatch);
        Assert.True(copy.Failure.Identity.GroupsMismatch);
        Assert.False(copy.Failure.Identity.UserMismatch);
        Assert.DoesNotContain("private-user", json);
        Assert.DoesNotContain("private-group", json);
        Assert.DoesNotContain("private-privilege", json);
        Assert.DoesNotContain("S-1-16-8192", json);
    }

    [Theory]
    [InlineData("true", "github-hosted", "Windows", "eligible", true)]
    [InlineData(null, "github-hosted", "Windows", "eligible", false)]
    [InlineData("True", "github-hosted", "Windows", "eligible", false)]
    [InlineData("true", "self-hosted", "Windows", "eligible", false)]
    [InlineData("true", "github-hosted", "Linux", "eligible", false)]
    [InlineData("true", "github-hosted", "Windows", "normal", false)]
    [InlineData("true", "github-hosted", "Windows", "restricted", false)]
    [InlineData("true", "github-hosted", "Windows", "appcontainer", false)]
    [InlineData("true", "github-hosted", "Windows", "uiaccess", false)]
    [InlineData("true", "github-hosted", "Windows", "system", false)]
    [InlineData("true", "github-hosted", "Windows", "localsystem", false)]
    public void CiAccountGuardRequiresExactHostedContextAndUnrestrictedElevatedCaller(
        string? actions, string environment, string operatingSystem, string change, bool expected)
    {
        DesktopProcessIdentity caller = new("test-user", 1, 1, "S-1-16-12288", true, false, false, [], []);
        caller = change switch
        {
            "normal" => caller with { Elevated = false, IntegritySid = "S-1-16-8192" },
            "restricted" => caller with { Restricted = true },
            "appcontainer" => caller with { AppContainer = true },
            "uiaccess" => caller with { UiAccess = true },
            "system" => caller with { IntegritySid = "S-1-16-16384" },
            "localsystem" => caller with { UserSid = "S-1-5-18" },
            _ => caller,
        };
        Assert.Equal(expected, CiNormalLaunchFixture.IsEligible(caller, actions, environment, operatingSystem));
    }

    [Theory]
    [InlineData(true, true, true, "owned", "owned", true, true)]
    [InlineData(false, true, true, "owned", "owned", true, false)]
    [InlineData(true, false, true, "owned", "owned", true, false)]
    [InlineData(true, true, false, "owned", "owned", true, false)]
    [InlineData(true, true, true, null, null, true, false)]
    [InlineData(true, true, true, "owned", "reused", true, false)]
    [InlineData(true, true, true, "owned", "owned", false, false)]
    public void CiAccountDeletionRequiresTerminalJobAndExactCreationOwnership(bool created, bool terminal,
        bool drained, string? ownedSid, string? currentSid, bool markerMatches, bool expected)
    {
        Assert.Equal(expected, CiNormalLaunchFixture.CanDeleteAccount(created, terminal, drained,
            ownedSid, currentSid, markerMatches));
    }

    [Fact]
    public void CiBootstrapReportRequiresMatchingCaseAndCompletedAssertions()
    {
        var report = new CiNormalLaunchFixture.BootstrapReport("nonce", true, true, null);
        Assert.True(CiNormalLaunchFixture.IsSuccessfulReport(report, "nonce", true));
        Assert.False(CiNormalLaunchFixture.IsSuccessfulReport(report, "other", true));
        Assert.False(CiNormalLaunchFixture.IsSuccessfulReport(report, "nonce", false));
        Assert.False(CiNormalLaunchFixture.IsSuccessfulReport(report with { Succeeded = false }, "nonce", true));
        Assert.False(CiNormalLaunchFixture.IsSuccessfulReport(report with { Error = "failed" }, "nonce", true));
        Assert.False(CiNormalLaunchFixture.IsSuccessfulReport(null, "nonce", true));
    }

    private sealed class FixtureScope : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"OpenBlade.LaunchTest.{Guid.NewGuid():N}");
        private readonly string parentEventName = $"Local\\OpenBlade.ParentSentinel.{Guid.NewGuid():N}";
        internal EventWaitHandle CallerEvent { get; } = new(false, EventResetMode.ManualReset);
        internal EventWaitHandle ParentEvent { get; }
        internal SafeFileHandle ParentJob { get; } = LaunchFixture.CreateJob(killOnClose: false);
        internal SafeFileHandle CallerOuterJob { get; } = LaunchFixture.CreateJob(killOnClose: true);
        internal SafeFileHandle CallerInnerJob { get; } = LaunchFixture.CreateJob(killOnClose: true);
        internal Process? Parent { get; private set; }
        internal Process? Coordinator { get; private set; }

        internal FixtureScope()
        {
            Directory.CreateDirectory(Root);
            ParentEvent = new(false, EventResetMode.ManualReset, parentEventName);
        }

        internal async Task StartAsync(bool hidden)
        {
            Parent = LaunchFixture.StartFixture($"parent \"{Root}\" {parentEventName}", Root, ParentJob);
            await WaitForFileAsync("parent.ready", Parent);
            long parentHandle = long.Parse(File.ReadAllText(Path.Combine(Root, "parent.ready")));
            Assert.True(LaunchFixture.IsSameRemoteObject(Parent.SafeHandle, parentHandle, ParentEvent.SafeWaitHandle));
            Assert.True(LaunchFixture.SetHandleInformation(CallerEvent.SafeWaitHandle, 1, 1));
            Coordinator = LaunchFixture.StartFixture(
                $"launch \"{Root}\" {CallerEvent.SafeWaitHandle.DangerousGetHandle().ToInt64()} {hidden} {Parent.Id} {parentHandle}",
                Root, CallerOuterJob, CallerInnerJob);
            await WaitForFileAsync("coordinator.ready", Coordinator);
            Assert.True(LaunchFixture.IsSameRemoteObject(Coordinator.SafeHandle,
                CallerEvent.SafeWaitHandle.DangerousGetHandle().ToInt64(), CallerEvent.SafeWaitHandle));
        }

        internal async Task<LaunchReport> ReadReportAsync()
        {
            await WaitForFileAsync("report.json", Coordinator!);
            return JsonSerializer.Deserialize<LaunchReport>(File.ReadAllText(Path.Combine(Root, "report.json")))!;
        }

        private async Task WaitForFileAsync(string name, Process process)
        {
            string path = Path.Combine(Root, name);
            long start = Stopwatch.GetTimestamp();
            while (!File.Exists(path) && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(10))
            {
                if (process.HasExited)
                {
                    Assert.True(File.Exists(path), $"Fixture exited {process.ExitCode} before {name}.");
                    break;
                }

                await Task.Delay(25);
            }

            Assert.True(File.Exists(path), $"Fixture did not produce {name} in ten seconds.");
        }

        internal void CloseCallerJobs()
        {
            CallerInnerJob.Dispose();
            CallerOuterJob.Dispose();
        }

        internal static void StopExactFixture(Process process)
        {
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                Assert.True(process.WaitForExit(5000));
            }
        }

        public void Dispose()
        {
            try
            {
                File.WriteAllText(Path.Combine(Root, "parent.exit"), "exit");
                File.WriteAllText(Path.Combine(Root, "child.exit"), "exit");
                CloseCallerJobs();
                if (Coordinator is not null)
                {
                    StopExactFixture(Coordinator);
                    Coordinator.Dispose();
                }

                if (Parent is not null)
                {
                    StopExactFixture(Parent);
                    Parent.Dispose();
                }
            }
            finally
            {
                try
                {
                    // This exclusively fixture-owned job stays benign throughout
                    // the lifetime assertions. At teardown it also contains any
                    // child whose PID/report could not be read or parsed.
                    CloseCallerJobs();
                    LaunchFixture.TerminateAndDrainFixtureJob(ParentJob);
                }
                finally
                {
                    Coordinator?.Dispose();
                    Parent?.Dispose();
                    ParentJob.Dispose();
                    ParentEvent.Dispose();
                    CallerEvent.Dispose();
                }
            }
        }
    }
}

internal sealed record LaunchReport(int ProcessId, long CallerHandleCandidate, long ParentHandleCandidate,
    string WorkingDirectory, DesktopProcessIdentity? Identity, string? Error = null, LaunchFailure? Failure = null);

internal sealed record LaunchFailure(string Stage, string Message, int HResult, string? StackTrace,
    bool? ParentExited, LaunchIdentityDiagnostics? Identity, string? DiagnosticError = null);

internal sealed record LaunchIdentityDiagnostics(bool CallerNormal, bool CallerElevated, bool CallerRestricted,
    bool CallerAppContainer, bool CallerUiAccess, bool? ParentNormal, bool? ParentElevated, bool? ParentRestricted,
    bool? ParentAppContainer, bool? ParentUiAccess, bool? UserMismatch, bool? AuthenticationMismatch,
    bool? SessionMismatch, bool? IntegrityMismatch, bool? GroupsMismatch, bool? PrivilegesMismatch)
{
    internal static LaunchIdentityDiagnostics From(DesktopProcessIdentity caller, DesktopProcessIdentity? parent) =>
        new(caller.IsNormal, caller.Elevated, caller.Restricted, caller.AppContainer, caller.UiAccess,
            parent?.IsNormal, parent?.Elevated, parent?.Restricted, parent?.AppContainer, parent?.UiAccess,
            parent is null ? null : caller.UserSid != parent.UserSid,
            parent is null ? null : caller.AuthenticationId != parent.AuthenticationId,
            parent is null ? null : caller.SessionId != parent.SessionId,
            parent is null ? null : caller.IntegritySid != parent.IntegritySid,
            parent is null ? null : !caller.Groups.SequenceEqual(parent.Groups),
            parent is null ? null : !caller.Privileges.SequenceEqual(parent.Privileges));
}

// This WinExe apphost runs only bounded fixture processes. It never locates or
// designates Explorer, and cannot touch an installed product process.
internal static class LaunchFixture
{
    private const string RootVariable = "OPENBLADE_TEST_LAUNCH_ROOT";
    private const string CallerHandleVariable = "OPENBLADE_TEST_CALLER_HANDLE";
    private const string ParentHandleVariable = "OPENBLADE_TEST_PARENT_HANDLE";

    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "ci-normal-orchestrator")
        {
            return CiNormalLaunchFixture.RunOrchestrator(args[1], bool.Parse(args[2]), args[3]);
        }

        if (args.Length == 4 && args[0] == "ci-normal-positive")
        {
            return CiNormalLaunchFixture.RunBootstrap(args[1], bool.Parse(args[2]), args[3]);
        }

        // Explicitly attended, read-only admission check. This mode never starts
        // an installed application, calls the broker, or creates a shell child.
        if (args.Length == 2 && args[0] == "verify-desktop-context")
        {
            string? error = null;
            try
            {
                using WindowsDesktopLaunchContext context = WindowsDesktopLaunchContext.Prepare();
                context.Validate();
            }
            catch (Exception exception)
            {
                error = $"{exception.GetType().Name}: {exception.Message}";
            }

            File.WriteAllText(args[1], JsonSerializer.Serialize(new
            {
                Succeeded = error is null,
                Error = error,
                Revision = typeof(LaunchFixture).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            }));
            return error is null ? 0 : 1;
        }

        if (args.Length == 3 && args[0] == "parent")
        {
            using EventWaitHandle sentinel = EventWaitHandle.OpenExisting(args[2]);
            if (!SetHandleInformation(sentinel.SafeWaitHandle, 1, 1))
            {
                return 3;
            }

            WriteAtomic(args[1], "parent.ready", sentinel.SafeWaitHandle.DangerousGetHandle().ToInt64().ToString());
            WaitForMarker(args[1], "parent.exit", 25);
            return 0;
        }

        if (args.Length == 6 && args[0] == "launch")
        {
            string root = args[1];
            string stage = "caller-sentinel";
            SafeProcessHandle? parent = null;
            DesktopProcessIdentity? identity = null;
            try
            {
                if (!GetHandleInformation(new(long.Parse(args[2])), out uint handleFlags) || (handleFlags & 1) == 0)
                {
                    throw new InvalidOperationException("The coordinator did not inherit its event sentinel.");
                }

                stage = "open-parent";
                parent = OpenProcess(0x00101080, false, uint.Parse(args[4]));
                if (parent.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                stage = "read-caller-identity";
                identity = DesktopProcessIdentity.ReadCaller();
                stage = "coordinator-ready";
                WriteAtomic(root, "coordinator.ready", "ready");
                stage = "wait-launch";
                WaitForMarker(root, "launch", 10);
                stage = "prepare-child-environment";
                Environment.SetEnvironmentVariable(RootVariable, root);
                Environment.SetEnvironmentVariable(CallerHandleVariable, args[2]);
                Environment.SetEnvironmentVariable(ParentHandleVariable, args[5]);
                bool hidden = bool.Parse(args[3]);
                stage = "start";
                using Process child = IndependentInstalledProcess.Start(new(Environment.ProcessPath!, root, hidden,
                    hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal), parent, identity);
                stage = "child-returned";
                Thread.Sleep(15000);
            }
            catch (Exception exception)
            {
                bool? parentExited = null;
                LaunchIdentityDiagnostics? diagnostic = identity is null ? null : LaunchIdentityDiagnostics.From(identity, null);
                string? diagnosticError = null;
                try
                {
                    // Failure-only snapshot of the retained fixture parent, not
                    // the rejected child's token or the original gate instant.
                    if (parent is { IsInvalid: false, IsClosed: false })
                    {
                        parentExited = IndependentInstalledProcess.HasExited(parent);
                        if (parentExited == false && identity is not null)
                        {
                            diagnostic = LaunchIdentityDiagnostics.From(identity, DesktopProcessIdentity.Read(parent));
                        }
                    }
                }
                catch (Exception diagnosticException)
                {
                    // Diagnostics must never replace the original launch failure.
                    diagnosticError = $"{diagnosticException.GetType().Name}: 0x{diagnosticException.HResult:X8}";
                }

                WriteReport(root, new(0, 0, 0, root, null, exception.GetType().Name,
                    new(stage, exception.Message, exception.HResult, exception.StackTrace,
                        parentExited, diagnostic, diagnosticError)));
            }
            finally
            {
                parent?.Dispose();
            }

            return 0;
        }

        string? childRoot = Environment.GetEnvironmentVariable(RootVariable);
        if (childRoot is null)
        {
            return 2;
        }

        File.WriteAllText(Path.Combine(childRoot, "child.started"), "started");
        // Numeric slots may be reused by the runtime. Report candidates without
        // touching their objects; the test compares duplicated kernel handles.
        long callerCandidate = long.Parse(Environment.GetEnvironmentVariable(CallerHandleVariable)!);
        long parentCandidate = long.Parse(Environment.GetEnvironmentVariable(ParentHandleVariable)!);
        WriteReport(childRoot, new(Environment.ProcessId, callerCandidate, parentCandidate,
            Environment.CurrentDirectory, DesktopProcessIdentity.ReadCaller()));
        WaitForMarker(childRoot, "child.exit", 15);
        return 0;
    }

    private static void WaitForMarker(string root, string name, int seconds)
    {
        long start = Stopwatch.GetTimestamp();
        while (!File.Exists(Path.Combine(root, name)) && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(seconds))
        {
            Thread.Sleep(25);
        }
    }

    private static void WriteReport(string root, LaunchReport report) =>
        WriteAtomic(root, "report.json", JsonSerializer.Serialize(report));

    private static void WriteAtomic(string root, string name, string content)
    {
        string temporary = Path.Combine(root, name + ".tmp");
        File.WriteAllText(temporary, content);
        File.Move(temporary, Path.Combine(root, name));
    }

    internal static SafeFileHandle CreateJob(bool killOnClose)
    {
        SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        Assert.False(job.IsInvalid);
        JobExtendedLimits limits = new();
        limits.Basic.LimitFlags = killOnClose ? 0x2000u : 0;
        Assert.True(SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobExtendedLimits>()));
        return job;
    }

    internal static Process StartFixture(string arguments, string root, params SafeFileHandle[] jobs)
    {
        string executable = Path.ChangeExtension(typeof(LaunchFixture).Assembly.Location, ".exe");
        StartupInfo startup = new() { Size = Marshal.SizeOf<StartupInfo>() };
        StringBuilder command = new($"\"{executable}\" {arguments}");
        // No breakaway and no desktop-shell parent: all fixtures stay within the
        // execution host's original containment and inherit its OS-managed jobs.
        if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000000 | 4,
                IntPtr.Zero, root, ref startup, out ProcessInformation info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using SafeProcessHandle processHandle = new(info.Process, true);
        using SafeFileHandle thread = new(info.Thread, true);
        Process? process = null;
        try
        {
            foreach (SafeFileHandle job in jobs)
            {
                Assert.True(AssignProcessToJobObject(job, processHandle));
            }

            process = Process.GetProcessById(info.ProcessId);
            _ = process.Handle;
            Assert.Equal(1u, ResumeThread(thread));
            return process;
        }
        catch
        {
            process?.Dispose();
            TerminateProcess(processHandle, 1);
            WaitForSingleObject(processHandle, 5000);
            throw;
        }
    }

    internal static bool IsInJob(SafeProcessHandle process, SafeFileHandle job)
    {
        Assert.True(IsProcessInJob(process, job, out bool inJob));
        return inJob;
    }

    internal static bool IsSameRemoteObject(SafeProcessHandle process, long candidate, SafeWaitHandle expected)
    {
        using Process current = Process.GetCurrentProcess();
        if (!DuplicateHandle(process, new(candidate), current.SafeHandle, out SafeFileHandle duplicate,
                0, false, 2))
        {
            int error = Marshal.GetLastWin32Error();
            duplicate.Dispose();
            // Our CreateEvent/OpenEvent sentinels support cross-process duplication
            // (also proved by the parent/coordinator positive controls). A missing
            // slot (6) or a reused, nonduplicable object (50) cannot be that event.
            // Permission failures and every other error remain test failures.
            Assert.True(error is 6 or 50, $"Unexpected DuplicateHandle error: {error}.");
            return false;
        }

        using (duplicate)
        {
            return CompareObjectHandles(duplicate, expected);
        }
    }

    internal static void TerminateAndDrainFixtureJob(SafeFileHandle job)
    {
        Assert.True(TerminateJobObject(job, 1));
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            Assert.True(QueryInformationJobObject(job, 1, out JobAccounting accounting,
                Marshal.SizeOf<JobAccounting>(), IntPtr.Zero));
            if (accounting.ActiveProcesses == 0)
            {
                return;
            }

            Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(5),
                "The exclusively fixture-owned parent job did not empty after termination.");
            Thread.Sleep(25);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        internal long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
        internal uint PageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcesses;
        internal UIntPtr Affinity;
        internal uint Priority, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        internal JobBasicLimits Basic;
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedSize;
        internal IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process, Thread;
        internal int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobExtendedLimits information, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetHandleInformation(SafeWaitHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(SafeProcessHandle sourceProcess, IntPtr sourceHandle,
        SafeProcessHandle targetProcess, out SafeFileHandle targetHandle, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernelbase.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CompareObjectHandles(SafeFileHandle first, SafeWaitHandle second);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass,
        out JobAccounting information, int size, IntPtr returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);
}
