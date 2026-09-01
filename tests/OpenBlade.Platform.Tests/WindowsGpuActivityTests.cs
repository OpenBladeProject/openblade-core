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
            "explorer",
            hasWindow: true);
        WindowsGpuAppManager.ProcessEntry browser = Entry(
            20,
            10,
            sessionId,
            "browser",
            hasWindow: true);
        WindowsGpuAppManager.ProcessEntry helper = Entry(
            30,
            20,
            sessionId,
            "browser",
            hasWindow: false);
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
            "browser",
            hasWindow: false);
        WindowsGpuAppManager.ProcessEntry differentUserParent = Entry(
            20,
            10,
            5,
            "browser",
            hasWindow: true) with
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
    public void OwningApplication_DoesNotAttributeDifferentExecutableToWindowedParent()
    {
        WindowsGpuAppManager.ProcessEntry terminal = Entry(
            20,
            10,
            5,
            "terminal",
            hasWindow: true);
        WindowsGpuAppManager.ProcessEntry compute = Entry(
            30,
            20,
            5,
            "python",
            hasWindow: false);
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
    public void AppProjection_UsesPopulatedParentChainForGpuHelper()
    {
        WindowsGpuAppManager.ProcessEntry browser = Entry(
            20,
            10,
            5,
            "browser",
            hasWindow: true);
        WindowsGpuAppManager.ProcessEntry helper = Entry(
            30,
            20,
            5,
            "browser",
            hasWindow: false);
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
    public void ActiveProcessSelection_ExcludesIdleEngineOwnersAndNoise()
    {
        WindowsGpuProcessActivity[] active = WindowsGpuAppManager.SelectActiveProcesses(
        [
            new WindowsGpuProcessActivity(40, 0),
            new WindowsGpuProcessActivity(41, 0.1),
            new WindowsGpuProcessActivity(42, 0.1001),
        ]);

        WindowsGpuProcessActivity result = Assert.Single(active);
        Assert.Equal(42, result.ProcessId);
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
    public void ProcessFailurePolicy_ContainsTreeKillAggregateWithoutHidingCodeBugs()
    {
        Assert.True(WindowsGpuAppManager.IsRecoverableProcessActionException(
            new AggregateException(new InvalidOperationException("partial tree failure"))));
        Assert.False(WindowsGpuAppManager.IsRecoverableProcessActionException(
            new NullReferenceException("programming defect")));
        Assert.False(WindowsGpuAppManager.IsRecoverableProcessActionException(
            new OperationCanceledException("cancellation remains control flow")));
    }

    private static WindowsGpuAppManager.ProcessEntry Entry(
        int processId,
        int parentProcessId,
        int sessionId,
        string name,
        bool hasWindow) => new(
            processId,
            parentProcessId,
            sessionId,
            DateTime.UtcNow.Ticks,
            name,
            $@"C:\Apps\{name}.exe",
            hasWindow,
            SameUser: true);
}
