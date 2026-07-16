using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceLifecycleRefreshLoopTests
{
    [Fact]
    public async Task ConflictRefresh_WaitsForThreeSecondInterval()
    {
        var time = new ManualTimeProvider();
        var runtime = new RecordingRuntime(failFirstRefresh: false);
        var loop = new ServiceConflictRefreshLoop(
            runtime,
            time,
            NullLogger<ServiceConflictRefreshLoop>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task running = loop.RunAsync(cancellation.Token);
        await WaitUntilAsync(() => time.CreatedTimerCount == 1, cancellation.Token);

        time.Advance(TimeSpan.FromMilliseconds(2_999));
        await Task.Yield();
        Assert.Equal(0, runtime.ConflictRefreshCount);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await runtime.FirstRefresh.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await running;

        Assert.Equal(1, runtime.ConflictRefreshCount);
        Assert.Equal(0, runtime.RefreshCount);
    }

    [Fact]
    public async Task ConflictRefresh_ContinuesAfterTransientRefreshFailure()
    {
        var time = new ManualTimeProvider();
        var runtime = new RecordingRuntime(failFirstRefresh: true);
        var loop = new ServiceConflictRefreshLoop(
            runtime,
            time,
            NullLogger<ServiceConflictRefreshLoop>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task running = loop.RunAsync(cancellation.Token);
        await WaitUntilAsync(() => time.CreatedTimerCount == 1, cancellation.Token);
        time.Advance(TimeSpan.FromSeconds(3));
        await runtime.FirstRefresh.Task.WaitAsync(cancellation.Token);
        await WaitUntilAsync(() => time.CreatedTimerCount == 2, cancellation.Token);

        time.Advance(TimeSpan.FromSeconds(3));
        await runtime.TwoRefreshes.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await running;

        Assert.Equal(2, runtime.ConflictRefreshCount);
        Assert.Equal(0, runtime.RefreshCount);
        Assert.Equal(1, runtime.MaximumConcurrentRefreshes);
    }

    [Fact]
    public async Task BackgroundTaskGroup_PropagatesFailureAndCancelsPeers()
    {
        var expected = new InvalidOperationException("lifecycle failed");
        var peerCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ServiceBackgroundTaskGroup.RunAsync(
                [
                    _ => Task.FromException(expected),
                    async token =>
                    {
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            peerCancelled.TrySetResult();
                        }
                    },
                ],
                CancellationToken.None));

        Assert.Same(expected, actual);
        await peerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RegistrationFailure_PropagatesFromLoop()
    {
        var expected = new InvalidOperationException("registration failed");
        var source = new ThrowingLifecycleEventSource(expected);
        var runtime = new RecordingRuntime(failFirstRefresh: false);
        var loop = new ServiceLifecycleRefreshLoop(
            runtime,
            source,
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.RunAsync(CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal(0, runtime.RefreshCount);
    }

    [Fact]
    public async Task Notifications_RefreshSequentiallyAndContinueAfterFailure()
    {
        var source = new FakeLifecycleEventSource();
        var runtime = new RecordingRuntime(failFirstRefresh: true);
        var loop = new ServiceLifecycleRefreshLoop(
            runtime,
            source,
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task running = loop.RunAsync(cancellation.Token);
        source.Publish(ServiceLifecycleTrigger.PowerChanged);
        source.Publish(ServiceLifecycleTrigger.DeviceArrived);

        await runtime.TwoRefreshes.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await running;

        Assert.Equal(2, runtime.RefreshCount);
        Assert.Equal(1, runtime.MaximumConcurrentRefreshes);
    }

    private sealed class FakeLifecycleEventSource : IServiceLifecycleEventSource
    {
        private readonly Channel<ServiceLifecycleTrigger> notifications =
            Channel.CreateUnbounded<ServiceLifecycleTrigger>();

        public void Publish(ServiceLifecycleTrigger trigger) =>
            notifications.Writer.TryWrite(trigger);

        public IAsyncEnumerable<ServiceLifecycleTrigger> WatchAsync(
            CancellationToken cancellationToken) =>
            notifications.Reader.ReadAllAsync(cancellationToken);
    }

    private sealed class ThrowingLifecycleEventSource(Exception exception)
        : IServiceLifecycleEventSource
    {
        public async IAsyncEnumerable<ServiceLifecycleTrigger> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.FromException(exception);
            yield break;
        }
    }

    private sealed class RecordingRuntime(bool failFirstRefresh) : IServiceControlRuntime
    {
        private int concurrentRefreshes;
        private int conflictRefreshCount;
        private int refreshCount;

        public int ConflictRefreshCount => Volatile.Read(ref conflictRefreshCount);

        public int RefreshCount => Volatile.Read(ref refreshCount);

        public int MaximumConcurrentRefreshes { get; private set; }

        public TaskCompletionSource TwoRefreshes { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstRefresh { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RefreshAsync(CancellationToken token)
        {
            await RecordRefreshAsync(isConflictRefresh: false);
        }

        public async Task RefreshConflictsAsync(CancellationToken token)
        {
            await RecordRefreshAsync(isConflictRefresh: true);
        }

        public Task ObserveAsync(CancellationToken cancellationToken) =>
            RefreshAsync(cancellationToken);

        public Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken) =>
            RefreshAsync(cancellationToken);

        public Task RefreshControllerPresenceAsync(CancellationToken cancellationToken) =>
            RefreshConflictsAsync(cancellationToken);

        private async Task RecordRefreshAsync(bool isConflictRefresh)
        {
            int concurrent = Interlocked.Increment(ref concurrentRefreshes);
            MaximumConcurrentRefreshes = Math.Max(MaximumConcurrentRefreshes, concurrent);
            int current = isConflictRefresh
                ? Interlocked.Increment(ref conflictRefreshCount)
                : Interlocked.Increment(ref refreshCount);
            if (current == 1)
            {
                FirstRefresh.TrySetResult();
            }

            try
            {
                await Task.Yield();
                if (current == 1 && failFirstRefresh)
                {
                    throw new IOException("transient refresh failure");
                }

                if (current == 2)
                {
                    TwoRefreshes.TrySetResult();
                }
            }
            finally
            {
                Interlocked.Decrement(ref concurrentRefreshes);
            }
        }

        public ServiceStatusSnapshot GetStatus() => throw new NotSupportedException();

        public Task<ControlOperationResult> UpdateProfilesAsync(
            ProfileSet profiles,
            CancellationToken token) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetPerformanceOverrideAsync(
            ManualOverride<PerformancePreset>? value,
            CancellationToken token) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetFanOverrideAsync(
            ManualOverride<FanMode>? value,
            CancellationToken token) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetLightingOverrideAsync(
            ManualOverride<LightingSettings>? value,
            CancellationToken token) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetBatteryPolicyAsync(
            BatteryPolicy value,
            CancellationToken token) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetFullChargeOverrideAsync(
            bool enabled,
            CancellationToken token) => throw new NotSupportedException();
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        private int createdTimerCount;

        public int CreatedTimerCount => Volatile.Read(ref createdTimerCount);

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ManualTimer timer;
            lock (sync)
            {
                timer = new ManualTimer(callback, state, now + dueTime, period);
                timers.Add(timer);
                Interlocked.Increment(ref createdTimerCount);
            }

            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            ManualTimer[] snapshot;
            lock (sync)
            {
                now += amount;
                snapshot = timers.ToArray();
            }

            foreach (ManualTimer timer in snapshot)
            {
                timer.FireIfDue(now);
            }
        }

        private sealed class ManualTimer(
            TimerCallback callback,
            object? state,
            DateTimeOffset due,
            TimeSpan period) : ITimer
        {
            private bool disposed;
            private DateTimeOffset next = due;

            public void FireIfDue(DateTimeOffset now)
            {
                if (disposed || now < next)
                {
                    return;
                }

                callback(state);
                if (period == Timeout.InfiniteTimeSpan)
                {
                    disposed = true;
                }
                else
                {
                    next = now + period;
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                throw new NotSupportedException();

            public void Dispose() => disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
