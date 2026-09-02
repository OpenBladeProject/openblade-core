using OpenBlade.Platform;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class GpuActivitySourceTests
{
    [Fact]
    public async Task IsActive_RefreshesAndPrimesBeforeEachDecisionAndObservesNewProcesses()
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
            (milliseconds, _) =>
            {
                calls.Add($"delay:{milliseconds}");
                return Task.CompletedTask;
            });

        Assert.Equal(false, await source.IsActiveAsync());
        Assert.Equal(true, await source.IsActiveAsync());

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
    public async Task IsActive_RefreshFailureReturnsUnknownWithoutReadingStaleSnapshot()
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
            (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        Assert.Null(await source.IsActiveAsync());

        Assert.Equal(0, readCalls);
        Assert.Equal(0, delayCalls);
    }

    [Fact]
    public async Task IsActive_UnavailableFinalSampleReturnsUnknown()
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
            (_, _) => Task.CompletedTask);

        Assert.Null(await source.IsActiveAsync());

        Assert.Empty(snapshots);
    }

    [Fact]
    public async Task IsActive_CancellationDuringWaitSkipsFinalRead()
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
                return Task.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.IsActiveAsync(cancellation.Token).AsTask());

        Assert.Equal(1, readCalls);
    }

    [Fact]
    public async Task IsActive_WaitsAsynchronouslyInsteadOfBlockingTheCaller()
    {
        var waitStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWait = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new WindowsGpuActivitySource(
            () => true,
            () => new WindowsGpuActivitySnapshot(true, []),
            (_, _) =>
            {
                waitStarted.SetResult();
                return releaseWait.Task;
            });

        ValueTask<bool?> decision = source.IsActiveAsync();
        await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(decision.IsCompleted);
        releaseWait.SetResult();

        Assert.Equal(false, await decision.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task IsActive_SerializesRefreshAndBothSamplesAcrossConcurrentCallers()
    {
        object callsSync = new();
        List<string> calls = [];
        int delayCalls = 0;
        var firstDelayEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDelay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
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
                    firstDelayEntered.TrySetResult();
                    return releaseFirstDelay.Task;
                }

                return Task.CompletedTask;
            });

        Task<bool?> first = source.IsActiveAsync().AsTask();
        await firstDelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool?> second = source.IsActiveAsync().AsTask();

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
            releaseFirstDelay.SetResult();
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
        var delayEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
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
                delayEntered.TrySetResult();
                return releaseDelay.Task;
            });

        Task<bool?> decision = source.IsActiveAsync().AsTask();
        await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
            releaseDelay.SetResult();
        }

        Assert.Equal(false, await decision);
        await dispose;
        Assert.Null(await source.IsActiveAsync());
        Assert.Equal(1, refreshCalls);
        Assert.Equal(2, readCalls);
    }

    [Fact]
    public async Task IsActive_IgnoresSystemProcessPagingUnlessAUserProcessIsActive()
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
            (_, _) => Task.CompletedTask);

        Assert.Equal(false, await source.IsActiveAsync());
        Assert.Equal(true, await source.IsActiveAsync());
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
