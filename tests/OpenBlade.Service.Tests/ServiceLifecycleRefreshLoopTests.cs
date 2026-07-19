using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceLifecycleRefreshLoopTests
{
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
    [InlineData(ServiceLifecycleTrigger.PowerChanged, 1, 0, 0, 0, 0)]
    [InlineData(ServiceLifecycleTrigger.Suspending, 0, 0, 1, 0, 0)]
    [InlineData(ServiceLifecycleTrigger.Resumed, 1, 0, 1, 1, 0)]
    [InlineData(ServiceLifecycleTrigger.DisplayOff, 0, 0, 0, 0, 1)]
    [InlineData(ServiceLifecycleTrigger.DisplayOn, 0, 0, 0, 0, 1)]
    [InlineData(ServiceLifecycleTrigger.DeviceArrived, 0, 1, 0, 1, 0)]
    [InlineData(ServiceLifecycleTrigger.DeviceRemoved, 0, 1, 1, 0, 0)]
    public async Task Notification_RoutesToExpectedTypedRuntimeOperations(
        ServiceLifecycleTrigger trigger,
        int expectedPowerChanges,
        int expectedObservations,
        int expectedInvalidations,
        int expectedRestores,
        int expectedSuppressions)
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
        Assert.Equal(expectedInvalidations, runtime.LightingInvalidationCount);
        Assert.Equal(expectedRestores, runtime.LightingRestoreCount);
        Assert.Equal(expectedSuppressions, runtime.LightingSuppressionCount);
        Assert.Equal(0, runtime.ControllerPresenceRefreshCount);
        Assert.Equal(
            expectedPowerChanges + expectedObservations + expectedInvalidations
                + expectedRestores + expectedSuppressions,
            runtime.TotalOperationCount);
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
        private int lightingInvalidationCount;
        private int lightingRestoreCount;
        private int lightingSuppressionCount;
        private int totalOperationCount;

        public int ControllerPresenceRefreshCount =>
            Volatile.Read(ref controllerPresenceRefreshCount);

        public int ObserveCount => Volatile.Read(ref observeCount);

        public int PowerSourceChangeCount => Volatile.Read(ref powerSourceChangeCount);

        public int LightingInvalidationCount => Volatile.Read(ref lightingInvalidationCount);

        public int LightingRestoreCount => Volatile.Read(ref lightingRestoreCount);

        public int LightingSuppressionCount => Volatile.Read(ref lightingSuppressionCount);

        public int TotalOperationCount => Volatile.Read(ref totalOperationCount);

        public int MaximumConcurrentRefreshes { get; private set; }

        public TaskCompletionSource TwoRefreshes { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstRefresh { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ObserveAsync(bool includeLiveReadings, CancellationToken cancellationToken) =>
            RecordRefreshAsync(Operation.Observe);

        public Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken) =>
            RecordRefreshAsync(Operation.PowerSourceChange);

        public Task RefreshControllerPresenceAsync(CancellationToken cancellationToken) =>
            RecordRefreshAsync(Operation.ControllerPresence);

        public Task InvalidateLightingRendererAsync(CancellationToken cancellationToken) =>
            RecordRefreshAsync(Operation.InvalidateLighting);

        public Task RestoreLightingRendererAsync(CancellationToken cancellationToken) =>
            RecordRefreshAsync(Operation.RestoreLighting);

        public Task<ControlOperationResult> SetLightingSuppressionAsync(
            LightingSuppressionReason reason,
            bool suppressed,
            CancellationToken cancellationToken) =>
            RecordSuppressionAsync();

        private async Task<ControlOperationResult> RecordSuppressionAsync()
        {
            Interlocked.Increment(ref lightingSuppressionCount);
            await RecordRefreshAsync(Operation.LightingSuppression);
            return new ControlOperationResult(ControlOperationStatus.Applied, "Applied");
        }

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
                case Operation.InvalidateLighting:
                    Interlocked.Increment(ref lightingInvalidationCount);
                    break;
                case Operation.RestoreLighting:
                    Interlocked.Increment(ref lightingRestoreCount);
                    break;
                case Operation.LightingSuppression:
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
            InvalidateLighting,
            RestoreLighting,
            LightingSuppression,
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

}
