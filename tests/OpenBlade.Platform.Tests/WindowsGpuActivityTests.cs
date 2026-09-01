using System.Diagnostics.CodeAnalysis;
using System.Security.Principal;
using OpenBlade.Platform;

namespace OpenBlade.Platform.Tests;

public sealed class WindowsGpuActivityTests
{
    private const string NvidiaLuid = "luid_0x00000000_0x0000abcd";

    [Theory]
    [InlineData("pid_42_luid_0x00000000_0x0000abcd_phys_0_eng_0_engtype_3D", 42)]
    [InlineData("PID_987_LUID_0X00000000_0X0000ABCD_phys_0_eng_1_engtype_Copy", 987)]
    public void InstanceParser_ReturnsNvidiaProcessId(string instance, int expected)
    {
        Assert.True(WindowsNvidiaGpuActivityReader.TryParseNvidiaProcessId(
            instance,
            NvidiaLuid,
            out int actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("pid_0_luid_0x00000000_0x0000abcd_phys_0_eng_0")]
    [InlineData("pid_bad_luid_0x00000000_0x0000abcd_phys_0_eng_0")]
    [InlineData("pid_42_luid_0x00000000_0x00001234_phys_0_eng_0")]
    [InlineData("luid_0x00000000_0x0000abcd_pid_42_phys_0_eng_0")]
    public void InstanceParser_RejectsInvalidOrDifferentAdapter(string instance)
    {
        Assert.False(WindowsNvidiaGpuActivityReader.TryParseNvidiaProcessId(
            instance,
            NvidiaLuid,
            out _));
    }

    [Theory]
    [InlineData("OpenBlade.Tray")]
    [InlineData("OpenBlade.SessionAgent")]
    [InlineData("dwm")]
    [InlineData("Explorer")]
    [InlineData("SearchHost")]
    public void ClosePolicy_ProtectsOpenBladeAndWindowsShellProcesses(string name)
    {
        Assert.True(WindowsGpuAppManager.IsProtectedProcessName(name));
    }

    [Fact]
    public void OwningApplication_WalksGpuHelperToTopLevelAppButStopsBeforeExplorer()
    {
        const int sessionId = 5;
        WindowsGpuAppManager.ProcessEntry explorer = Entry(
            10,
            0,
            sessionId,
            "explorer");
        WindowsGpuAppManager.ProcessEntry browser = Entry(
            20,
            10,
            sessionId,
            "browser");
        WindowsGpuAppManager.ProcessEntry helper = Entry(
            30,
            20,
            sessionId,
            "browser");
        Dictionary<int, WindowsGpuAppManager.ProcessEntry> table = new()
        {
            [10] = explorer,
            [20] = browser,
            [30] = helper,
        };

        WindowsGpuAppManager.ProcessEntry actual =
            WindowsGpuAppManager.SelectOwningApplication(helper, table);

        Assert.Equal(browser, actual);
    }

    [Fact]
    public void OwningApplication_DoesNotCrossSessionOrUserBoundary()
    {
        WindowsGpuAppManager.ProcessEntry helper = Entry(
            30,
            20,
            5,
            "browser");
        WindowsGpuAppManager.ProcessEntry differentUserParent = Entry(
            20,
            10,
            5,
            "browser") with
        {
            SameUser = false,
        };
        Dictionary<int, WindowsGpuAppManager.ProcessEntry> table = new()
        {
            [20] = differentUserParent,
            [30] = helper,
        };

        Assert.Equal(
            helper,
            WindowsGpuAppManager.SelectOwningApplication(helper, table));
    }

    [Fact]
    public void OwningApplication_DoesNotAttributeDifferentProcessNameToParent()
    {
        WindowsGpuAppManager.ProcessEntry terminal = Entry(
            20,
            10,
            5,
            "terminal");
        WindowsGpuAppManager.ProcessEntry compute = Entry(
            30,
            20,
            5,
            "python");
        Dictionary<int, WindowsGpuAppManager.ProcessEntry> table = new()
        {
            [20] = terminal,
            [30] = compute,
        };

        Assert.Equal(
            compute,
            WindowsGpuAppManager.SelectOwningApplication(compute, table));
    }

    [Fact]
    public void OwningApplication_DoesNotClimbToNewerReusedParentProcessId()
    {
        WindowsGpuAppManager.ProcessEntry helper = Entry(
            30,
            20,
            5,
            "browser") with
        {
            StartTimeUtcTicks = 100,
        };
        WindowsGpuAppManager.ProcessEntry reusedParent = Entry(
            20,
            10,
            5,
            "browser") with
        {
            StartTimeUtcTicks = 200,
        };
        Dictionary<int, WindowsGpuAppManager.ProcessEntry> table = new()
        {
            [20] = reusedParent,
            [30] = helper,
        };

        Assert.Equal(
            helper,
            WindowsGpuAppManager.SelectOwningApplication(helper, table));
    }

    [Fact]
    public void AppProjection_UsesPopulatedParentChainForGpuHelper()
    {
        WindowsGpuAppManager.ProcessEntry browser = Entry(
            20,
            10,
            5,
            "browser");
        WindowsGpuAppManager.ProcessEntry helper = Entry(
            30,
            20,
            5,
            "browser");
        Dictionary<int, WindowsGpuAppManager.ProcessEntry> table = new()
        {
            [20] = browser,
            [30] = helper,
        };

        WindowsGpuApp app = Assert.Single(WindowsGpuAppManager.CreateApps(
            [new WindowsGpuProcessActivity(30, 4.5)],
            table,
            currentSessionId: 5));

        Assert.Equal(browser.ProcessId, app.ProcessId);
        Assert.Equal(browser.Name, app.Name);
        Assert.Equal(4.5, app.UtilizationPercent);
    }

    [Fact]
    public void ActivitySampling_RetriesTransientStartupFailureAndPrimesRateCounter()
    {
        var first = new WindowsGpuActivitySnapshot(
            true,
            [new WindowsGpuProcessActivity(42, 3.5)]);
        var second = new WindowsGpuActivitySnapshot(
            true,
            [new WindowsGpuProcessActivity(42, 2.5)]);
        Queue<WindowsGpuActivitySnapshot> snapshots = new(
        [
            new WindowsGpuActivitySnapshot(false, []),
            new WindowsGpuActivitySnapshot(true, []),
            first,
            second,
        ]);
        List<int> delays = [];

        WindowsGpuActivitySnapshot actual = WindowsGpuAppManager.ReadActivitySample(
            snapshots.Dequeue,
            delays.Add,
            CancellationToken.None);

        WindowsGpuProcessActivity process = Assert.Single(actual.Processes);
        Assert.Equal(42, process.ProcessId);
        Assert.Equal(2.5, process.UtilizationPercent);
        Assert.Equal([150, 150, 150], delays);
    }

    [Fact]
    public void ActivitySampling_RefreshFailureReturnsBeforeReadingOrWaiting()
    {
        int readCalls = 0;
        List<int> delays = [];

        WindowsGpuActivitySnapshot actual =
            WindowsGpuAppManager.RefreshAndReadActivitySample(
                () => false,
                () =>
                {
                    readCalls++;
                    return new WindowsGpuActivitySnapshot(true, []);
                },
                delays.Add,
                CancellationToken.None);

        Assert.False(actual.IsAvailable);
        Assert.Empty(actual.Processes);
        Assert.Equal(0, readCalls);
        Assert.Empty(delays);
    }

    [Fact]
    public void StableProcessIntersection_IncludesIdleHoldersAndUsesLatestValue()
    {
        WindowsGpuProcessActivity[] actual = WindowsGpuAppManager.IntersectStableProcesses(
        [
            new WindowsGpuProcessActivity(40, 8.5),
            new WindowsGpuProcessActivity(41, 0),
        ],
        [
            new WindowsGpuProcessActivity(40, 0),
            new WindowsGpuProcessActivity(41, 0),
        ]);

        Assert.Collection(
            actual,
            process =>
            {
                Assert.Equal(40, process.ProcessId);
                Assert.Equal(0, process.UtilizationPercent);
            },
            process =>
            {
                Assert.Equal(41, process.ProcessId);
                Assert.Equal(0, process.UtilizationPercent);
            });
    }

    [Fact]
    public void StableProcessIntersection_OmitsTransientProcesses()
    {
        WindowsGpuProcessActivity[] actual = WindowsGpuAppManager.IntersectStableProcesses(
        [
            new WindowsGpuProcessActivity(40, 1),
            new WindowsGpuProcessActivity(41, 2),
        ],
        [
            new WindowsGpuProcessActivity(41, 3),
            new WindowsGpuProcessActivity(42, 4),
        ]);

        WindowsGpuProcessActivity process = Assert.Single(actual);
        Assert.Equal(41, process.ProcessId);
        Assert.Equal(3, process.UtilizationPercent);
    }

    [Fact]
    public void StableProcessIntersection_DeduplicatesLatestSnapshotByProcessId()
    {
        WindowsGpuProcessActivity[] actual = WindowsGpuAppManager.IntersectStableProcesses(
            [new WindowsGpuProcessActivity(42, 1)],
            [
                new WindowsGpuProcessActivity(42, 2),
                new WindowsGpuProcessActivity(42, 3),
            ]);

        WindowsGpuProcessActivity process = Assert.Single(actual);
        Assert.Equal(42, process.ProcessId);
        Assert.Equal(3, process.UtilizationPercent);
    }

    [Fact]
    public void ReaderRefresh_ReplacesQueryAndAdapterLuidBeforeCleanupScan()
    {
        int locateCalls = 0;
        Queue<WindowsNvidiaGpuActivityReader.QueryHandles> handles = new(
        [
            new WindowsNvidiaGpuActivityReader.QueryHandles((nint)11, (nint)12),
            new WindowsNvidiaGpuActivityReader.QueryHandles((nint)21, (nint)22),
        ]);
        List<nint> closedQueries = [];
        var reader = new WindowsNvidiaGpuActivityReader(
            () =>
            {
                locateCalls++;
                return NvidiaLuid;
            },
            () => handles.Dequeue(),
            closedQueries.Add);

        Assert.True(reader.Refresh());
        reader.Dispose();

        Assert.Equal(2, locateCalls);
        Assert.Equal([(nint)11, (nint)21], closedQueries);
    }

    [Fact]
    public void ReaderRefresh_RecoversAfterAdapterLuidIsTemporarilyUnavailable()
    {
        Queue<string?> luidTokens = new([NvidiaLuid, null, NvidiaLuid]);
        Queue<WindowsNvidiaGpuActivityReader.QueryHandles> handles = new(
        [
            new WindowsNvidiaGpuActivityReader.QueryHandles((nint)11, (nint)12),
            new WindowsNvidiaGpuActivityReader.QueryHandles((nint)21, (nint)22),
        ]);
        List<nint> closedQueries = [];
        var reader = new WindowsNvidiaGpuActivityReader(
            luidTokens.Dequeue,
            () => handles.Dequeue(),
            closedQueries.Add);

        Assert.False(reader.Refresh());
        Assert.True(reader.Refresh());
        reader.Dispose();

        Assert.Empty(luidTokens);
        Assert.Empty(handles);
        Assert.Equal([(nint)11, (nint)21], closedQueries);
    }

    [Fact]
    public void ReaderSizingResult_DistinguishesErrorsFromEmptyAndReadyArrays()
    {
        Assert.Equal(
            WindowsNvidiaGpuActivityReader.CounterArraySizingResult.Failed,
            WindowsNvidiaGpuActivityReader.ClassifyCounterArraySizingResult(
                0,
                512));
        Assert.Equal(
            WindowsNvidiaGpuActivityReader.CounterArraySizingResult.Empty,
            WindowsNvidiaGpuActivityReader.ClassifyCounterArraySizingResult(
                0x800007D2,
                0));
        Assert.Equal(
            WindowsNvidiaGpuActivityReader.CounterArraySizingResult.Ready,
            WindowsNvidiaGpuActivityReader.ClassifyCounterArraySizingResult(
                0x800007D2,
                512));
    }

    [Fact]
    public void ProcessFailurePolicy_ContainsTreeKillAggregateWithoutHidingCodeBugs()
    {
        Assert.True(WindowsGpuAppManager.IsRecoverableProcessActionException(
            new AggregateException(new InvalidOperationException("partial tree failure"))));
        Assert.False(WindowsGpuAppManager.IsRecoverableProcessActionException(
            new NullReferenceException("programming defect")));
        Assert.False(WindowsGpuAppManager.IsRecoverableProcessActionException(
            new OperationCanceledException("cancellation remains control flow")));
    }

    [Fact]
    public void GracefulClose_ValidatesAndActsOnSingleRetainedTarget()
    {
        var target = new FakeProcessActionTarget();
        var recycled = new FakeProcessActionTarget();
        var factory = new FakeProcessActionTargetFactory(target, recycled);
        using WindowsGpuAppManager manager = CreateManager(factory);

        WindowsGpuAppActionResult result = Assert.Single(manager.CloseGracefully([App()]));

        Assert.True(result.Succeeded);
        Assert.Equal(1, factory.OpenCalls);
        Assert.Equal([42], factory.RequestedProcessIds);
        Assert.True(target.IdentityReadCalls > 0);
        Assert.Equal(1, target.CloseMainWindowCalls);
        Assert.Equal(1, target.DisposeCalls);
        Assert.Equal(0, recycled.IdentityReadCalls);
        Assert.Equal(0, recycled.CloseMainWindowCalls);
        Assert.Equal(0, recycled.DisposeCalls);
    }

    [Fact]
    public void ForceClose_KillsEntireTreeOnRetainedTarget()
    {
        var target = new FakeProcessActionTarget();
        var factory = new FakeProcessActionTargetFactory(target);
        using WindowsGpuAppManager manager = CreateManager(factory);

        WindowsGpuAppActionResult result = Assert.Single(manager.ForceClose([App()]));

        Assert.True(result.Succeeded);
        Assert.Equal([true], target.KillEntireProcessTreeArguments);
        Assert.Equal(1, target.DisposeCalls);
    }

    [Theory]
    [InlineData("exited")]
    [InlineData("process-id")]
    [InlineData("start-time")]
    [InlineData("session")]
    [InlineData("user")]
    [InlineData("name")]
    public void Close_IdentityMismatchDoesNotActAndDisposes(string mismatch)
    {
        var target = new FakeProcessActionTarget();
        switch (mismatch)
        {
            case "exited":
                target.HasExitedValue = true;
                break;
            case "process-id":
                target.ProcessIdValue++;
                break;
            case "start-time":
                target.StartTimeUtcTicksValue++;
                break;
            case "session":
                target.SessionIdValue++;
                break;
            case "user":
                target.SameUserValue = false;
                break;
            case "name":
                target.NameValue = "recycled";
                break;
        }
        var factory = new FakeProcessActionTargetFactory(target);
        using WindowsGpuAppManager manager = CreateManager(factory);

        WindowsGpuAppActionResult result = Assert.Single(manager.CloseGracefully([App()]));

        Assert.False(result.Succeeded);
        Assert.Contains("identity changed", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, target.CloseMainWindowCalls);
        Assert.Empty(target.KillEntireProcessTreeArguments);
        Assert.Equal(1, target.DisposeCalls);
    }

    [Fact]
    public void Close_ProtectedRetainedTargetDoesNotActAndDisposes()
    {
        var target = new FakeProcessActionTarget
        {
            NameValue = "OpenBlade.Tray",
        };
        var factory = new FakeProcessActionTargetFactory(target);
        using WindowsGpuAppManager manager = CreateManager(factory);
        WindowsGpuApp app = App() with { Name = target.NameValue };

        WindowsGpuAppActionResult result = Assert.Single(manager.CloseGracefully([app]));

        Assert.False(result.Succeeded);
        Assert.Contains("protected", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, target.CloseMainWindowCalls);
        Assert.Empty(target.KillEntireProcessTreeArguments);
        Assert.Equal(1, target.DisposeCalls);
    }

    [Fact]
    public void Close_CancellationAfterValidationDoesNotActAndDisposes()
    {
        using var cancellation = new CancellationTokenSource();
        var target = new FakeProcessActionTarget
        {
            OnNameRead = cancellation.Cancel,
        };
        var factory = new FakeProcessActionTargetFactory(target);
        using WindowsGpuAppManager manager = CreateManager(factory);

        IReadOnlyList<WindowsGpuAppActionResult> results = manager.CloseGracefully(
            [App()],
            cancellation.Token);

        Assert.Empty(results);
        Assert.Equal(0, target.CloseMainWindowCalls);
        Assert.Empty(target.KillEntireProcessTreeArguments);
        Assert.Equal(1, target.DisposeCalls);
    }

    [Fact]
    public void Close_RecoverableActionFailureDisposesRetainedTarget()
    {
        var target = new FakeProcessActionTarget
        {
            CloseMainWindowException = new InvalidOperationException("process exited"),
        };
        var factory = new FakeProcessActionTargetFactory(target);
        using WindowsGpuAppManager manager = CreateManager(factory);

        WindowsGpuAppActionResult result = Assert.Single(manager.CloseGracefully([App()]));

        Assert.False(result.Succeeded);
        Assert.Equal("process exited", result.Error);
        Assert.Equal(1, target.CloseMainWindowCalls);
        Assert.Equal(1, target.DisposeCalls);
    }

    private static WindowsGpuAppManager CreateManager(
        WindowsGpuAppManager.IWindowsProcessActionTargetFactory factory)
    {
        var reader = new WindowsNvidiaGpuActivityReader(
            () => null,
            () => null,
            _ => { });
        var userSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        return new WindowsGpuAppManager(reader, userSid, currentSessionId: 7, factory);
    }

    private static WindowsGpuApp App() => new(
        ProcessId: 42,
        StartTimeUtcTicks: 1234,
        Name: "browser",
        UtilizationPercent: 5,
        CanClose: true,
        BlockReason: null);

    private static WindowsGpuAppManager.ProcessEntry Entry(
        int processId,
        int parentProcessId,
        int sessionId,
        string name) => new(
            processId,
            parentProcessId,
            sessionId,
            DateTime.UtcNow.Ticks,
            name,
            SameUser: true);

    private sealed class FakeProcessActionTargetFactory(
        params FakeProcessActionTarget[] targets) :
        WindowsGpuAppManager.IWindowsProcessActionTargetFactory
    {
        private readonly Queue<FakeProcessActionTarget> targets = new(targets);

        internal int OpenCalls { get; private set; }

        internal List<int> RequestedProcessIds { get; } = [];

        public bool TryOpen(
            int processId,
            [NotNullWhen(true)]
            out WindowsGpuAppManager.IWindowsProcessActionTarget? target)
        {
            OpenCalls++;
            RequestedProcessIds.Add(processId);
            target = targets.Count == 0 ? null : targets.Dequeue();
            return target is not null;
        }
    }

    private sealed class FakeProcessActionTarget :
        WindowsGpuAppManager.IWindowsProcessActionTarget
    {
        internal bool HasExitedValue { get; set; }

        internal int ProcessIdValue { get; set; } = 42;

        internal int SessionIdValue { get; set; } = 7;

        internal long StartTimeUtcTicksValue { get; set; } = 1234;

        internal string NameValue { get; set; } = "browser";

        internal bool SameUserValue { get; set; } = true;

        internal Action? OnNameRead { get; set; }

        internal Exception? CloseMainWindowException { get; set; }

        internal int IdentityReadCalls { get; private set; }

        internal int CloseMainWindowCalls { get; private set; }

        internal List<bool> KillEntireProcessTreeArguments { get; } = [];

        internal int DisposeCalls { get; private set; }

        public bool HasExited
        {
            get
            {
                IdentityReadCalls++;
                return HasExitedValue;
            }
        }

        public int ProcessId
        {
            get
            {
                IdentityReadCalls++;
                return ProcessIdValue;
            }
        }

        public int SessionId
        {
            get
            {
                IdentityReadCalls++;
                return SessionIdValue;
            }
        }

        public long StartTimeUtcTicks
        {
            get
            {
                IdentityReadCalls++;
                return StartTimeUtcTicksValue;
            }
        }

        public string Name
        {
            get
            {
                IdentityReadCalls++;
                OnNameRead?.Invoke();
                return NameValue;
            }
        }

        public bool SameUser
        {
            get
            {
                IdentityReadCalls++;
                return SameUserValue;
            }
        }

        public bool CloseMainWindow()
        {
            CloseMainWindowCalls++;
            if (CloseMainWindowException is not null)
            {
                throw CloseMainWindowException;
            }
            return true;
        }

        public void Kill(bool entireProcessTree) =>
            KillEntireProcessTreeArguments.Add(entireProcessTree);

        public void Dispose() => DisposeCalls++;
    }
}
