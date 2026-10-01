using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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

    internal static async Task RunPositiveFixtureAsync(bool hidden, Action<string>? reportPhase = null)
    {
        reportPhase?.Invoke("core-enter");
        using FixtureScope fixture = new();
        reportPhase?.Invoke("fixture-created");
        await fixture.StartAsync(hidden);
        reportPhase?.Invoke("fixture-ready");
        File.WriteAllText(Path.Combine(fixture.Root, "launch"), "launch");
        LaunchReport report = await fixture.ReadReportAsync();
        reportPhase?.Invoke("launch-report-read");
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
            reportPhase?.Invoke("assertions-completed");
        }
        finally
        {
            File.WriteAllText(Path.Combine(fixture.Root, "child.exit"), "exit");
            FixtureScope.StopExactFixture(child);
            reportPhase?.Invoke("child-stopped");
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

    [Fact]
    public void CiBootstrapDiagnosticsDistinguishUnconfirmedDrainageAndMissingEvidence()
    {
        string root = Path.Combine(Path.GetTempPath(), "OpenBlade.CiDiagnostics." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal("not collected: owned job drainage unconfirmed",
                CiNormalLaunchFixture.ReadBootstrapDiagnostics(root, jobDrained: false));
            string evidence = CiNormalLaunchFixture.ReadBootstrapDiagnostics(root, jobDrained: true);
            Assert.Contains("bootstrap-phases.txt: FileNotFoundException", evidence);
            Assert.Contains("host-trace:", evidence);
            Assert.Contains("NotFoundException", evidence[evidence.IndexOf("host-trace:", StringComparison.Ordinal)..]);
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public void CiBootstrapDiagnosticsBoundTraceEvidenceAndExcludeUnrelatedFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "OpenBlade.CiDiagnostics." + Guid.NewGuid().ToString("N"));
        string traces = Path.Combine(root, "host-trace");
        Directory.CreateDirectory(traces);
        List<string> files = [];
        try
        {
            Write(Path.Combine(root, "bootstrap-phases.txt"), "managed-enter\nidentity-verified\n");
            Write(Path.Combine(root, "owned-account.json"), "unrelated-account-secret");
            Write(Path.Combine(traces, "OpenBlade.DeploymentClient.Tests.log"), "malformed-name-secret");
            for (int index = 1; index <= 9; index++)
            {
                Write(Path.Combine(traces, $"OpenBlade.DeploymentClient.Tests.{index}.log"),
                    "omitted-prefix" + new string('x', 10000) + "host-tail-marker");
            }
            string evidence = CiNormalLaunchFixture.ReadBootstrapDiagnostics(root, jobDrained: true);
            Assert.InRange(evidence.Length, 1, 6000);
            Assert.Contains("managed-enter", evidence);
            Assert.Contains("host-tail-marker", evidence);
            Assert.DoesNotContain("omitted-prefix", evidence);
            Assert.DoesNotContain("unrelated-account-secret", evidence);
            Assert.DoesNotContain("malformed-name-secret", evidence);
        }
        finally
        {
            foreach (string path in files) { File.Delete(path); }
            Directory.Delete(traces);
            Directory.Delete(root);
        }

        void Write(string path, string text)
        {
            files.Add(path);
            File.WriteAllText(path, text);
        }
    }

    [Theory]
    [InlineData("ancestry", 1)]
    [InlineData("phase", 1)]
    [InlineData("report", 4)]
    public void CiBootstrapSetupFailuresReturnTerminalDiagnosticsWithoutEnteringCore(string failure, int expected)
    {
        string nonce = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "OpenBlade.CiNormalLaunch." + nonce);
        Directory.CreateDirectory(root);
        int ancestryCalls = 0, coreCalls = 0, phaseCalls = 0;
        CiNormalLaunchFixture.BootstrapReport? report = null;
        try
        {
            int result = CiNormalLaunchFixture.RunBootstrapWithDiagnostics(root, true, nonce,
                () =>
                {
                    ancestryCalls++;
                    throw new UnauthorizedAccessException("synthetic ancestry denial");
                },
                _ => coreCalls++,
                phase =>
                {
                    phaseCalls++;
                    Assert.Equal("managed-enter", phase);
                    if (failure == "phase") { throw new IOException("synthetic phase write failure"); }
                },
                value =>
                {
                    if (failure == "report") { throw new IOException("synthetic report write failure"); }
                    report = value;
                });
            Assert.Equal(expected, result);
            Assert.Equal(0, coreCalls);
            Assert.Equal(1, phaseCalls);
            Assert.Equal(failure == "phase" ? 0 : 1, ancestryCalls);
            if (failure != "report")
            {
                Assert.NotNull(report);
                Assert.False(report.Succeeded);
                Assert.Contains(failure == "phase" ? "synthetic phase write failure" : "synthetic ancestry denial", report.Error);
            }
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public void CiBootstrapInvalidRootShapeDoesNotInvokeAnyOperationOrWriter()
    {
        int calls = 0;
        int result = CiNormalLaunchFixture.RunBootstrapWithDiagnostics("invalid-relative-root", true, "invalid-nonce",
            () => calls++, _ => calls++, _ => calls++, _ => calls++);
        Assert.Equal(2, result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ChildDiagnosticsReadOnlyReturnedPidAndBoundTraceTail()
    {
        string root = Path.Combine(Path.GetTempPath(), "OpenBlade.LaunchTest." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string missing = LaunchFixture.ReadChildDiagnostics(root);
            Assert.Contains("FileNotFoundException", missing);
            Assert.Contains("child trace unavailable", missing);
            File.WriteAllText(Path.Combine(root, "child-state.json"),
                "{\"ProcessId\":123,\"ElapsedMilliseconds\":9750,\"Exited\":true,\"ExitCode\":2,\"Error\":null}");
            File.WriteAllText(Path.Combine(root, "child-managed.txt"), "RootPresent=false");
            File.WriteAllText(Path.Combine(root, "child-trace-setup.txt"), "configured");
            string traces = Path.Combine(root, "child-host-trace");
            Directory.CreateDirectory(traces);
            File.WriteAllText(Path.Combine(traces, "OpenBlade.DeploymentClient.Tests.123.log"),
                "excluded-prefix" + new string('x', 8192) + "selected-child-tail");
            File.WriteAllText(Path.Combine(traces, "OpenBlade.DeploymentClient.Tests.456.log"), "unrelated-child-secret");
            string evidence = LaunchFixture.ReadChildDiagnostics(root);
            Assert.Contains("\"ExitCode\":2", evidence);
            Assert.Contains("RootPresent=false", evidence);
            Assert.Contains("selected-child-tail", evidence);
            Assert.DoesNotContain("excluded-prefix", evidence);
            Assert.DoesNotContain("unrelated-child-secret", evidence);
            Assert.True(evidence.Length < 6600);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative")]
    [InlineData("C:relative")]
    [InlineData("\\ProgramData")]
    public void CiFixtureRootRejectsMissingOrNonFullyQualifiedBase(string commonBase)
    {
        Assert.Throws<InvalidOperationException>(() => CiNormalLaunchFixture.GetFixtureRoot(
            commonBase, "0123456789abcdef0123456789abcdef"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("0123456789abcdef0123456789abcdef\\child")]
    public void CiFixtureRootRejectsInvalidOrEscapingNonce(string nonce)
    {
        Assert.Throws<InvalidOperationException>(() => CiNormalLaunchFixture.GetFixtureRoot(@"C:\ProgramData", nonce));
    }

    [Fact]
    public void CiFixtureRootIsOneDirectUniqueLeafUnderTheCommonBase()
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        string root = CiNormalLaunchFixture.GetFixtureRoot(@"C:\ProgramData\", nonce);
        Assert.Equal(@"C:\ProgramData", Path.GetDirectoryName(root));
        Assert.Equal("OpenBlade.CiNormalLaunch." + nonce, Path.GetFileName(root));
        Assert.NotEqual(root, CiNormalLaunchFixture.GetFixtureRoot(@"C:\ProgramData", "1123456789abcdef0123456789abcdef"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData(" 4")]
    [InlineData("+4")]
    [InlineData("9223372036854775808")]
    public void ParentSentinelHandleRejectsMalformedOrInvalidValues(string text)
    {
        Assert.False(LaunchFixture.TryParseSentinelHandle(text, out IntPtr handle));
        Assert.Equal(IntPtr.Zero, handle);
    }

    [Fact]
    public void ParentSentinelHandleParsesInvariantPositiveValueWithoutAdmittingAnObject()
    {
        Assert.True(LaunchFixture.TryParseSentinelHandle("4", out IntPtr handle));
        Assert.Equal(new IntPtr(4), handle);
        // The parent still requires a real inheritable handle, and the fixture
        // compares its actual kernel object before allowing the launch phase.
    }

    private sealed class FixtureScope : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"OpenBlade.LaunchTest.{Guid.NewGuid():N}");
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
            ParentEvent = new(false, EventResetMode.ManualReset);
        }

        internal async Task StartAsync(bool hidden)
        {
            // The sentinel needs object identity, not a session namespace entry.
            // Only the fixture parent receives this inheritable handle.
            Assert.True(LaunchFixture.SetHandleInformation(ParentEvent.SafeWaitHandle, 1, 1));
            try
            {
                string sentinelHandle = ParentEvent.SafeWaitHandle.DangerousGetHandle().ToInt64()
                    .ToString(CultureInfo.InvariantCulture);
                Parent = LaunchFixture.StartFixture($"parent \"{Root}\" {sentinelHandle}", Root, ParentJob);
            }
            finally
            {
                Assert.True(LaunchFixture.SetHandleInformation(ParentEvent.SafeWaitHandle, 1, 0));
            }

            await WaitForFileAsync("parent.ready", Parent);
            long parentHandle = long.Parse(File.ReadAllText(Path.Combine(Root, "parent.ready")), CultureInfo.InvariantCulture);
            Assert.True(LaunchFixture.IsSameRemoteObject(Parent.SafeHandle, parentHandle, ParentEvent.SafeWaitHandle));
            Assert.True(LaunchFixture.SetHandleInformation(CallerEvent.SafeWaitHandle, 1, 1));
            Coordinator = LaunchFixture.StartFixture(
                $"launch \"{Root}\" {CallerEvent.SafeWaitHandle.DangerousGetHandle().ToInt64()} {hidden} {Parent.Id} {parentHandle}",
                Root, CallerOuterJob, CallerInnerJob);
            await WaitForFileAsync("coordinator.ready", Coordinator);
            Assert.True(LaunchFixture.IsSameRemoteObject(Coordinator.SafeHandle,
                CallerEvent.SafeWaitHandle.DangerousGetHandle().ToInt64(), CallerEvent.SafeWaitHandle));
            Assert.False(LaunchFixture.IsSameRemoteObject(Coordinator.SafeHandle, parentHandle, ParentEvent.SafeWaitHandle));
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
                    bool produced = File.Exists(path);
                    Assert.True(produced, produced ? null
                        : $"Fixture exited {process.ExitCode} before {name}. {ReadFailureDiagnostics()}");
                    break;
                }

                await Task.Delay(25);
            }

            bool exists = File.Exists(path);
            Assert.True(exists, exists ? null
                : $"Fixture did not produce {name} in ten seconds. {ReadFailureDiagnostics()}");
        }

        private string ReadFailureDiagnostics()
        {
            try
            {
                string phasePath = Path.Combine(Root, "coordinator-phase.txt");
                string phase = File.Exists(phasePath) && new FileInfo(phasePath).Length <= 1024
                    ? File.ReadAllText(phasePath) : "missing or oversized";
                return $"Coordinator phase: {phase}; child.started: {File.Exists(Path.Combine(Root, "child.started"))}; "
                    + $"report.json: {File.Exists(Path.Combine(Root, "report.json"))}; "
                    + $"retained parent: {Describe(Parent)}; retained coordinator: {Describe(Coordinator)}. "
                    + LaunchFixture.ReadChildDiagnostics(Root);
            }
            catch (Exception exception)
            {
                return $"Fixture diagnostics unavailable: {exception.GetType().Name} 0x{exception.HResult:X8}.";
            }

            static string Describe(Process? retained)
            {
                if (retained is null) { return "not returned"; }
                return retained.HasExited ? $"exited {retained.ExitCode}" : "running";
            }
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
        if (args.Length == 0) { TryRecordChildManagedEntry(); }

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
            if (!TryParseSentinelHandle(args[2], out IntPtr handle)
                || !GetHandleInformation(handle, out uint handleFlags) || (handleFlags & 1) == 0)
            {
                return 3;
            }

            using SafeWaitHandle sentinel = new(handle, ownsHandle: true);
            WriteAtomic(args[1], "parent.ready", handle.ToInt64().ToString(CultureInfo.InvariantCulture));
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
                TryWriteCoordinatorPhase(root, "start-enter");
                TryConfigureChildTrace(root);
                using Process child = IndependentInstalledProcess.Start(new(Environment.ProcessPath!, root, hidden,
                    hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal), parent, identity);
                stage = "child-returned";
                long maintenanceStarted = Stopwatch.GetTimestamp();
                do
                {
                    TryRecordChildState(root, child, Stopwatch.GetElapsedTime(maintenanceStarted));
                    TimeSpan remaining = TimeSpan.FromSeconds(15) - Stopwatch.GetElapsedTime(maintenanceStarted);
                    if (remaining <= TimeSpan.Zero) { break; }
                    Thread.Sleep(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250));
                }
                while (Stopwatch.GetElapsedTime(maintenanceStarted) < TimeSpan.FromSeconds(15));
                TryRecordChildState(root, child, Stopwatch.GetElapsedTime(maintenanceStarted));
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

    private sealed record ChildState(int ProcessId, long ElapsedMilliseconds, bool? Exited, int? ExitCode, string? Error);

    private static void TryRecordChildState(string root, Process child, TimeSpan elapsed)
    {
        int id = 0;
        ChildState state;
        try
        {
            id = child.Id;
            child.Refresh();
            bool exited = child.HasExited;
            state = new(id, (long)elapsed.TotalMilliseconds, exited, exited ? child.ExitCode : null, null);
        }
        catch (Exception exception)
        {
            state = new(id, (long)elapsed.TotalMilliseconds, null, null, DiagnosticError(exception));
        }
        TryWriteDiagnostic(root, "child-state.json", JsonSerializer.Serialize(state));
        TryWriteCoordinatorPhase(root, "child-returned; " + JsonSerializer.Serialize(state));
    }

    private static void TryConfigureChildTrace(string root)
    {
        try
        {
            string directory = Path.Combine(root, "child-host-trace");
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            { throw new IOException("Child trace directory is reparsed."); }
            // .NET 10's existing directory mode names each trace by executable/PID.
            // Only subsequently created children receive this route; the running
            // parent and coordinator retain their already initialized trace sinks.
            Environment.SetEnvironmentVariable("DOTNET_HOST_TRACEFILE", directory);
            Environment.SetEnvironmentVariable("DOTNET_HOST_TRACE_VERBOSITY", "4");
            Environment.SetEnvironmentVariable("DOTNET_HOST_TRACE", "1");
            TryWriteDiagnostic(root, "child-trace-setup.txt", "configured");
        }
        catch (Exception exception)
        { TryWriteDiagnostic(root, "child-trace-setup.txt", DiagnosticError(exception)); }
    }

    private static void TryRecordChildManagedEntry()
    {
        string? root = null;
        try
        {
            // The admitted launch plan supplies this exact fixture working directory.
            // Do not route diagnostics through an unchecked environment-variable path.
            string current = Environment.CurrentDirectory;
            const string prefix = "OpenBlade.LaunchTest.";
            string name = Path.GetFileName(current);
            if (!Path.IsPathFullyQualified(current) || !name.StartsWith(prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(name[prefix.Length..], "N", out _)
                || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { return; }
            root = current;
            string? inheritedRoot = Environment.GetEnvironmentVariable(RootVariable);
            TryWriteDiagnostic(root, "child-managed.txt", JsonSerializer.Serialize(new
            {
                Entered = true,
                RootPresent = inheritedRoot is not null,
                RootMatchesWorkingDirectory = string.Equals(inheritedRoot, current, StringComparison.OrdinalIgnoreCase),
                CallerHandlePresent = Environment.GetEnvironmentVariable(CallerHandleVariable) is not null,
                ParentHandlePresent = Environment.GetEnvironmentVariable(ParentHandleVariable) is not null,
            }));
        }
        catch (Exception exception)
        {
            if (root is not null) { TryWriteDiagnostic(root, "child-managed.txt", DiagnosticError(exception)); }
        }
    }

    private static string DiagnosticError(Exception exception) =>
        $"{exception.GetType().Name} 0x{exception.HResult:X8}";

    private static void TryWriteDiagnostic(string root, string name, string content)
    {
        try
        {
            string destination = Path.Combine(root, name);
            string temporary = destination + ".tmp";
            File.WriteAllText(temporary, content);
            File.Move(temporary, destination, overwrite: true);
        }
        catch (Exception) { /* Best-effort diagnostics cannot change the launch outcome. */ }
    }

    internal static string ReadChildDiagnostics(string root)
    {
        // Failure-only, fixed file names and one exact returned PID; no inventory,
        // process adoption, arbitrary environment dump or successful-case trace reads.
        var result = new StringBuilder();
        string stateText = ReadTail("child-state.json", 1024);
        result.Append($"Child state: {stateText}; managed entry: {ReadTail("child-managed.txt", 1024)}; ");
        result.Append($"trace setup: {ReadTail("child-trace-setup.txt", 256)}; ");
        try
        {
            ChildState? state = JsonSerializer.Deserialize<ChildState>(stateText);
            if (state is not { ProcessId: > 0 }) { return result.Append("child trace: returned PID unavailable").ToString(); }
            string traceDirectory = Path.Combine(root, "child-host-trace");
            if ((File.GetAttributes(traceDirectory) & FileAttributes.ReparsePoint) != 0)
            { return result.Append("child trace: reparse directory refused").ToString(); }
            string traceName = $"OpenBlade.DeploymentClient.Tests.{state.ProcessId.ToString(CultureInfo.InvariantCulture)}.log";
            result.Append("child trace: " + ReadTail(Path.Combine("child-host-trace", traceName), 4096));
        }
        catch (Exception exception)
        { result.Append("child trace unavailable: " + DiagnosticError(exception)); }
        return result.ToString();

        string ReadTail(string relative, int bound)
        {
            try
            {
                string path = Path.Combine(root, relative);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { return "reparse file refused"; }
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                input.Position = Math.Max(0, input.Length - bound);
                byte[] bytes = new byte[bound];
                int count = input.Read(bytes);
                return Encoding.UTF8.GetString(bytes, 0, count);
            }
            catch (Exception exception) { return DiagnosticError(exception); }
        }
    }

    internal static bool TryParseSentinelHandle(string text, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            || value <= 0 || IntPtr.Size == 4 && value > int.MaxValue)
        {
            return false;
        }

        handle = new(value);
        return true;
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

    private static void TryWriteCoordinatorPhase(string root, string phase)
    {
        try
        {
            string path = Path.Combine(root, "coordinator-phase.txt");
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, phase);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception) { /* Diagnostics cannot replace the actual launch outcome. */ }
    }

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
            // Our unnamed event sentinels support cross-process duplication
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
