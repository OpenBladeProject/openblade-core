using OpenBlade.Platform;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class GpuActivitySourceTests
{
    [Fact]
    public void IsActive_RefreshesAndPrimesBeforeEachDecisionAndObservesNewProcesses()
    {
        List<string> calls = [];
        Queue<WindowsGpuActivitySnapshot> snapshots = new(
        [
            new WindowsGpuActivitySnapshot(true, []),
            new WindowsGpuActivitySnapshot(true, []),
            new WindowsGpuActivitySnapshot(true, []),
            new WindowsGpuActivitySnapshot(
                true,
                [new WindowsGpuProcessActivity(42, 3.5)]),
        ]);
        var source = new WindowsGpuActivitySource(
            () =>
            {
                calls.Add("refresh");
                return true;
            },
            () =>
            {
                calls.Add("read");
                return snapshots.Dequeue();
            },
            (milliseconds, _) => calls.Add($"delay:{milliseconds}"));

        Assert.Equal(false, source.IsActive());
        Assert.Equal(true, source.IsActive());

        Assert.Equal(
            [
                "refresh",
                "read",
                "delay:150",
                "read",
                "refresh",
                "read",
                "delay:150",
                "read",
            ],
            calls);
        Assert.Empty(snapshots);
    }

    [Fact]
    public void IsActive_RefreshFailureReturnsUnknownWithoutReadingStaleSnapshot()
    {
        int readCalls = 0;
        int delayCalls = 0;
        var source = new WindowsGpuActivitySource(
            () => false,
            () =>
            {
                readCalls++;
                return new WindowsGpuActivitySnapshot(
                    true,
                    [new WindowsGpuProcessActivity(42, 100)]);
            },
            (_, _) => delayCalls++);

        Assert.Null(source.IsActive());

        Assert.Equal(0, readCalls);
        Assert.Equal(0, delayCalls);
    }

    [Fact]
    public void IsActive_UnavailableFinalSampleReturnsUnknown()
    {
        Queue<WindowsGpuActivitySnapshot> snapshots = new(
        [
            new WindowsGpuActivitySnapshot(
                true,
                [new WindowsGpuProcessActivity(42, 100)]),
            new WindowsGpuActivitySnapshot(
                false,
                [new WindowsGpuProcessActivity(42, 100)]),
        ]);
        var source = new WindowsGpuActivitySource(
            () => true,
            snapshots.Dequeue,
            (_, _) => { });

        Assert.Null(source.IsActive());

        Assert.Empty(snapshots);
    }

    [Fact]
    public void IsActive_CancellationDuringWaitSkipsFinalRead()
    {
        int readCalls = 0;
        using var cancellation = new CancellationTokenSource();
        var source = new WindowsGpuActivitySource(
            () => true,
            () =>
            {
                readCalls++;
                return new WindowsGpuActivitySnapshot(true, []);
            },
            (milliseconds, cancellationToken) =>
            {
                Assert.Equal(150, milliseconds);
                cancellation.Cancel();
                Assert.True(cancellationToken.IsCancellationRequested);
            });

        Assert.Throws<OperationCanceledException>(
            () => source.IsActive(cancellation.Token));

        Assert.Equal(1, readCalls);
    }

    [Fact]
    public async Task IsActive_SerializesRefreshAndBothSamplesAcrossConcurrentCallers()
    {
        object callsSync = new();
        List<string> calls = [];
        int delayCalls = 0;
        using var firstDelayEntered = new ManualResetEventSlim();
        using var releaseFirstDelay = new ManualResetEventSlim();
        using var secondCallStarted = new ManualResetEventSlim();
        var source = new WindowsGpuActivitySource(
            () =>
            {
                lock (callsSync)
                {
                    calls.Add("refresh");
                }
                return true;
            },
            () =>
            {
                lock (callsSync)
                {
                    calls.Add("read");
                }
                return new WindowsGpuActivitySnapshot(true, []);
            },
            (_, _) =>
            {
                lock (callsSync)
                {
                    calls.Add("delay");
                }
                if (Interlocked.Increment(ref delayCalls) == 1)
                {
                    firstDelayEntered.Set();
                    Assert.True(releaseFirstDelay.Wait(TimeSpan.FromSeconds(5)));
                }
            });

        Task<bool?> first = Task.Run(() => source.IsActive());
        Assert.True(firstDelayEntered.Wait(TimeSpan.FromSeconds(5)));
        Task<bool?> second = Task.Run(() =>
        {
            secondCallStarted.Set();
            return source.IsActive();
        });
        Assert.True(secondCallStarted.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            Task completed = await Task.WhenAny(second, Task.Delay(100));
            Assert.NotSame(second, completed);
            lock (callsSync)
            {
                Assert.Equal(["refresh", "read", "delay"], calls);
            }
        }
        finally
        {
            releaseFirstDelay.Set();
        }

        Assert.Equal([false, false], await Task.WhenAll(first, second));
        lock (callsSync)
        {
            Assert.Equal(
                [
                    "refresh",
                    "read",
                    "delay",
                    "read",
                    "refresh",
                    "read",
                    "delay",
                    "read",
                ],
                calls);
        }
    }

    [Fact]
    public async Task Dispose_WaitsForInFlightDecisionAndBlocksLaterReads()
    {
        int refreshCalls = 0;
        int readCalls = 0;
        using var delayEntered = new ManualResetEventSlim();
        using var releaseDelay = new ManualResetEventSlim();
        using var disposeStarted = new ManualResetEventSlim();
        var source = new WindowsGpuActivitySource(
            () =>
            {
                refreshCalls++;
                return true;
            },
            () =>
            {
                readCalls++;
                return new WindowsGpuActivitySnapshot(true, []);
            },
            (_, _) =>
            {
                delayEntered.Set();
                Assert.True(releaseDelay.Wait(TimeSpan.FromSeconds(5)));
            });

        Task<bool?> decision = Task.Run(() => source.IsActive());
        Assert.True(delayEntered.Wait(TimeSpan.FromSeconds(5)));
        Task dispose = Task.Run(() =>
        {
            disposeStarted.Set();
            source.Dispose();
        });
        Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            Task completed = await Task.WhenAny(dispose, Task.Delay(100));
            Assert.NotSame(dispose, completed);
        }
        finally
        {
            releaseDelay.Set();
        }

        Assert.Equal(false, await decision);
        await dispose;
        Assert.Null(source.IsActive());
        Assert.Equal(1, refreshCalls);
        Assert.Equal(2, readCalls);
    }

    [Fact]
    public void IsActive_IgnoresSystemProcessPagingUnlessAUserProcessIsActive()
    {
        Queue<WindowsGpuActivitySnapshot> snapshots = new(
        [
            new WindowsGpuActivitySnapshot(true, []),
            new WindowsGpuActivitySnapshot(
                true,
                [new WindowsGpuProcessActivity(4, 0.24)]),
            new WindowsGpuActivitySnapshot(true, []),
            new WindowsGpuActivitySnapshot(
                true,
                [
                    new WindowsGpuProcessActivity(4, 0.24),
                    new WindowsGpuProcessActivity(42, 0.5),
                ]),
        ]);
        var source = new WindowsGpuActivitySource(
            () => true,
            snapshots.Dequeue,
            (_, _) => { });

        Assert.Equal(false, source.IsActive());
        Assert.Equal(true, source.IsActive());
        Assert.Empty(snapshots);
    }

    [Fact]
    public void IsWorkloadProcess_RequiresActiveUtilizationFromANonSystemProcess()
    {
        Assert.False(WindowsGpuActivitySource.IsWorkloadProcess(
            new WindowsGpuProcessActivity(4, 5)));
        Assert.False(WindowsGpuActivitySource.IsWorkloadProcess(
            new WindowsGpuProcessActivity(42, 0.1)));
        Assert.True(WindowsGpuActivitySource.IsWorkloadProcess(
            new WindowsGpuProcessActivity(42, 0.2)));
    }
}
