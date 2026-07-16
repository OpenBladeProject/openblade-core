using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceLifecycleRefreshLoopTests
{
    [Fact]
    public async Task ControllerPresenceRefresh_WaitsForThreeSecondInterval()
    {
        var time = new ManualTimeProvider();
        var runtime = new RecordingRuntime(failFirstRefresh: false);
        var loop = new ServiceControllerPresenceRefreshLoop(
            runtime,
            time,
            NullLogger<ServiceControllerPresenceRefreshLoop>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task running = loop.RunAsync(cancellation.Token);
        await WaitUntilAsync(() => time.CreatedTimerCount == 1, cancellation.Token);

        time.Advance(TimeSpan.FromMilliseconds(2_999));
        await Task.Yield();
        Assert.Equal(0, runtime.ControllerPresenceRefreshCount);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await runtime.FirstRefresh.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await running;

        Assert.Equal(1, runtime.ControllerPresenceRefreshCount);
        Assert.Equal(0, runtime.ObserveCount);
        Assert.Equal(0, runtime.PowerSourceChangeCount);
    }

    [Fact]
    public async Task ControllerPresenceRefresh_ContinuesAfterTransientRefreshFailure()
    {
        var time = new ManualTimeProvider();
        var runtime = new RecordingRuntime(failFirstRefresh: true);
        var loop = new ServiceControllerPresenceRefreshLoop(
            runtime,
            time,
            NullLogger<ServiceControllerPresenceRefreshLoop>.Instance);
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

        Assert.Equal(2, runtime.ControllerPresenceRefreshCount);
        Assert.Equal(0, runtime.ObserveCount);
        Assert.Equal(0, runtime.PowerSourceChangeCount);
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
        Assert.Equal(0, runtime.TotalOperationCount);
    }

    [Theory]
    [InlineData(ServiceLifecycleTrigger.PowerChanged, 1, 0)]
    [InlineData(ServiceLifecycleTrigger.Resumed, 0, 1)]
    [InlineData(ServiceLifecycleTrigger.DeviceArrived, 0, 1)]
    public async Task Notification_RoutesToExactlyOneTypedRuntimeOperation(
        ServiceLifecycleTrigger trigger,
        int expectedPowerChanges,
        int expectedObservations)
    {
        var source = new FakeLifecycleEventSource();
        var runtime = new RecordingRuntime(failFirstRefresh: false);
        var loop = new ServiceLifecycleRefreshLoop(
            runtime,
            source,
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task running = loop.RunAsync(cancellation.Token);
        source.Publish(trigger);

        await runtime.FirstRefresh.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await running;

        Assert.Equal(expectedPowerChanges, runtime.PowerSourceChangeCount);
        Assert.Equal(expectedObservations, runtime.ObserveCount);
        Assert.Equal(0, runtime.ControllerPresenceRefreshCount);
        Assert.Equal(1, runtime.TotalOperationCount);
    }

    [Fact]
    public async Task Notifications_ProcessSequentiallyAndContinueAfterFailure()
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

        Assert.Equal(1, runtime.PowerSourceChangeCount);
        Assert.Equal(1, runtime.ObserveCount);
        Assert.Equal(0, runtime.ControllerPresenceRefreshCount);
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
        private int controllerPresenceRefreshCount;
        private int observeCount;
        private int powerSourceChangeCount;
        private int totalOperationCount;

        public int ControllerPresenceRefreshCount =>
            Volatile.Read(ref controllerPresenceRefreshCount);

        public int ObserveCount => Volatile.Read(ref observeCount);

        public int PowerSourceChangeCount => Volatile.Read(ref powerSourceChangeCount);

        public int TotalOperationCount => Volatile.Read(ref totalOperationCount);

        public int MaximumConcurrentRefreshes { get; private set; }

        public TaskCompletionSource TwoRefreshes { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstRefresh { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ObserveAsync(CancellationToken cancellationToken) =>
            RecordRefreshAsync(Operation.Observe);

        public Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken) =>
            RecordRefreshAsync(Operation.PowerSourceChange);

        public Task RefreshControllerPresenceAsync(CancellationToken cancellationToken) =>
            RecordRefreshAsync(Operation.ControllerPresence);

        private async Task RecordRefreshAsync(Operation operation)
        {
            int concurrent = Interlocked.Increment(ref concurrentRefreshes);
            MaximumConcurrentRefreshes = Math.Max(MaximumConcurrentRefreshes, concurrent);
            switch (operation)
            {
                case Operation.Observe:
                    Interlocked.Increment(ref observeCount);
                    break;
                case Operation.PowerSourceChange:
                    Interlocked.Increment(ref powerSourceChangeCount);
                    break;
                case Operation.ControllerPresence:
                    Interlocked.Increment(ref controllerPresenceRefreshCount);
                    break;
            }

            int current = Interlocked.Increment(ref totalOperationCount);
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

        private enum Operation
        {
            Observe,
            PowerSourceChange,
            ControllerPresence,
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
