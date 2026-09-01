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
    [InlineData(0u, 128u, false, false)]
    [InlineData(0x800007D2u, 0u, true, false)]
    [InlineData(0x800007D2u, 128u, true, true)]
    public void CounterArraySizing_RejectsErrorsAndDistinguishesAvailableEmpty(
        uint status,
        uint bufferSize,
        bool expectedAvailable,
        bool expectedHasData)
    {
        bool available = WindowsNvidiaGpuActivityReader.TryClassifyCounterArraySizing(
            status,
            bufferSize,
            out bool hasData);

        Assert.Equal(expectedAvailable, available);
        Assert.Equal(expectedHasData, hasData);
    }

    [Fact]
    public void CounterArrayFill_RetriesOneResizeAndFreesBothBuffers()
    {
        const uint moreData = 0x800007D2;
        int fillCalls = 0;
        List<int> allocatedSizes = [];
        List<nint> freedBuffers = [];
        var expected = new WindowsGpuActivitySnapshot(
            true,
            [new WindowsGpuProcessActivity(42, 3.5)]);

        WindowsGpuActivitySnapshot actual =
            WindowsNvidiaGpuActivityReader.ReadFormattedCounterArrayWithRetry(
                bufferSize: 64,
                (ref uint bufferSize, out uint itemCount, nint buffer) =>
                {
                    fillCalls++;
                    if (fillCalls == 1)
                    {
                        Assert.Equal((nint)100, buffer);
                        Assert.Equal(64u, bufferSize);
                        bufferSize = 128;
                        itemCount = 0;
                        return moreData;
                    }

                    Assert.Equal((nint)200, buffer);
                    Assert.Equal(128u, bufferSize);
                    itemCount = 2;
                    return 0;
                },
                (buffer, itemCount) =>
                {
                    Assert.Equal((nint)200, buffer);
                    Assert.Equal(2u, itemCount);
                    return expected;
                },
                size =>
                {
                    allocatedSizes.Add(size);
                    return (nint)(allocatedSizes.Count * 100);
                },
                freedBuffers.Add);

        Assert.Same(expected, actual);
        Assert.Equal(2, fillCalls);
        Assert.Equal([64, 128], allocatedSizes);
        Assert.Equal([(nint)100, (nint)200], freedBuffers);
    }

    [Fact]
    public void CounterArrayFill_SecondResizeRequestFailsUnavailableWithoutLeaking()
    {
        const uint moreData = 0x800007D2;
        int fillCalls = 0;
        int projectCalls = 0;
        List<int> allocatedSizes = [];
        List<nint> freedBuffers = [];

        WindowsGpuActivitySnapshot actual =
            WindowsNvidiaGpuActivityReader.ReadFormattedCounterArrayWithRetry(
                bufferSize: 64,
                (ref uint bufferSize, out uint itemCount, nint _) =>
                {
                    fillCalls++;
                    bufferSize = fillCalls == 1 ? 128u : 256u;
                    itemCount = 0;
                    return moreData;
                },
                (_, _) =>
                {
                    projectCalls++;
                    return new WindowsGpuActivitySnapshot(true, []);
                },
                size =>
                {
                    allocatedSizes.Add(size);
                    return (nint)(allocatedSizes.Count * 100);
                },
                freedBuffers.Add);

        Assert.False(actual.IsAvailable);
        Assert.Equal(2, fillCalls);
        Assert.Equal(0, projectCalls);
        Assert.Equal([64, 128], allocatedSizes);
        Assert.Equal([(nint)100, (nint)200], freedBuffers);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void CounterArrayFill_InvalidRetrySizeFailsBeforeSecondAllocation(
        uint retrySize)
    {
        const uint moreData = 0x800007D2;
        int fillCalls = 0;
        int projectCalls = 0;
        List<int> allocatedSizes = [];
        List<nint> freedBuffers = [];

        WindowsGpuActivitySnapshot actual =
            WindowsNvidiaGpuActivityReader.ReadFormattedCounterArrayWithRetry(
                bufferSize: 64,
                (ref uint bufferSize, out uint itemCount, nint _) =>
                {
                    fillCalls++;
                    bufferSize = retrySize;
                    itemCount = 0;
                    return moreData;
                },
                (_, _) =>
                {
                    projectCalls++;
                    return new WindowsGpuActivitySnapshot(true, []);
                },
                size =>
                {
                    allocatedSizes.Add(size);
                    return (nint)100;
                },
                freedBuffers.Add);

        Assert.False(actual.IsAvailable);
        Assert.Equal(1, fillCalls);
        Assert.Equal(0, projectCalls);
        Assert.Equal([64], allocatedSizes);
        Assert.Equal([(nint)100], freedBuffers);
    }

    [Fact]
    public void CounterArrayFill_NonSizingErrorFailsUnavailableWithoutRetry()
    {
        int fillCalls = 0;
        int projectCalls = 0;
        int freeCalls = 0;

        WindowsGpuActivitySnapshot actual =
            WindowsNvidiaGpuActivityReader.ReadFormattedCounterArrayWithRetry(
                bufferSize: 64,
                (ref uint bufferSize, out uint itemCount, nint _) =>
                {
                    fillCalls++;
                    itemCount = 0;
                    return 5;
                },
                (_, _) =>
                {
                    projectCalls++;
                    return new WindowsGpuActivitySnapshot(true, []);
                },
                _ => (nint)100,
                _ => freeCalls++);

        Assert.False(actual.IsAvailable);
        Assert.Equal(1, fillCalls);
        Assert.Equal(0, projectCalls);
        Assert.Equal(1, freeCalls);
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
        var expected = new WindowsGpuActivitySnapshot(
            true,
            [new WindowsGpuProcessActivity(42, 3.5)]);
        Queue<WindowsGpuActivitySnapshot> snapshots = new(
        [
            new WindowsGpuActivitySnapshot(false, []),
            new WindowsGpuActivitySnapshot(true, []),
            expected,
        ]);
        List<int> delays = [];

        WindowsGpuActivitySnapshot actual = WindowsGpuAppManager.ReadActivitySample(
            snapshots.Dequeue,
            delays.Add,
            CancellationToken.None);

        Assert.Same(expected, actual);
        Assert.Equal([150, 150], delays);
    }

    [Fact]
    public void ReaderRefresh_ReplacesQueryAndAdapterLuid()
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
        Assert.Equal([(nint)11], closedQueries);
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
    public void ReaderRefresh_FailedReplacementRetainsOldQueryUntilDispose()
    {
        Queue<WindowsNvidiaGpuActivityReader.QueryHandles?> handles = new(
        [
            new WindowsNvidiaGpuActivityReader.QueryHandles((nint)11, (nint)12),
            null,
        ]);
        List<nint> closedQueries = [];
        var reader = new WindowsNvidiaGpuActivityReader(
            () => NvidiaLuid,
            handles.Dequeue,
            closedQueries.Add);

        Assert.False(reader.Refresh());
        Assert.Empty(closedQueries);

        reader.Dispose();
        reader.Dispose();

        Assert.Empty(handles);
        Assert.Equal([(nint)11], closedQueries);
    }

    [Fact]
    public void ReaderDispose_MissingCloseEntryPointClearsQueryExactlyOnce()
    {
        int closeCalls = 0;
        var reader = new WindowsNvidiaGpuActivityReader(
            () => NvidiaLuid,
            () => new WindowsNvidiaGpuActivityReader.QueryHandles(
                (nint)11,
                (nint)12),
            _ =>
            {
                closeCalls++;
                throw new EntryPointNotFoundException();
            });

        reader.Dispose();
        reader.Dispose();

        Assert.Equal(1, closeCalls);
        Assert.False(reader.Refresh());
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
