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
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.RunAsync(CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal(0, runtime.TotalOperationCount);
    }

    [Theory]
    [InlineData(ServiceLifecycleTrigger.PowerChanged, 1, 0, 0, 0, 0, 0, 0)]
    [InlineData(ServiceLifecycleTrigger.Suspending, 0, 0, 1, 0, 0, 1, 0)]
    [InlineData(ServiceLifecycleTrigger.Resumed, 1, 0, 1, 1, 0, 0, 0)]
    [InlineData(ServiceLifecycleTrigger.DisplayOff, 0, 0, 0, 0, 1, 0, 1)]
    [InlineData(ServiceLifecycleTrigger.DisplayOn, 0, 0, 0, 0, 1, 0, 0)]
    [InlineData(ServiceLifecycleTrigger.DeviceArrived, 0, 1, 0, 1, 0, 0, 0)]
    [InlineData(ServiceLifecycleTrigger.DeviceRemoved, 0, 1, 1, 0, 0, 0, 0)]
    public async Task Notification_RoutesToExpectedTypedRuntimeOperations(
        ServiceLifecycleTrigger trigger,
        int expectedPowerChanges,
        int expectedObservations,
        int expectedInvalidations,
        int expectedRestores,
        int expectedSuppressions,
        int expectedLowPowerPreparations,
        int expectedDisplayOffHyperBoostPreparations)
    {
        var source = new FakeLifecycleEventSource();
        var runtime = new RecordingRuntime(failFirstRefresh: false);
        var loop = new ServiceLifecycleRefreshLoop(
            runtime,
            source,
            new LowPowerGate(),
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
        Assert.Equal(expectedLowPowerPreparations, runtime.LowPowerPreparationCount);
        Assert.Equal(
            expectedDisplayOffHyperBoostPreparations,
            runtime.DisplayOffHyperBoostPreparationCount);
        Assert.Equal(0, runtime.ControllerPresenceRefreshCount);
        Assert.Equal(
            expectedPowerChanges + expectedObservations + expectedInvalidations
                + expectedRestores + expectedSuppressions + expectedLowPowerPreparations
                + expectedDisplayOffHyperBoostPreparations,
            runtime.TotalOperationCount);
    }

    [Fact]
    public async Task DeviceArrived_SkipsObservationAndRestoreWhileLowPowerGateIsActive()
    {
        var source = new FakeLifecycleEventSource();
        var runtime = new RecordingRuntime(failFirstRefresh: false);
        var gate = new LowPowerGate();
        gate.Enter();
        var loop = new ServiceLifecycleRefreshLoop(
            runtime,
            source,
            gate,
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task running = loop.RunAsync(cancellation.Token);
        source.Publish(ServiceLifecycleTrigger.DeviceArrived);
        await source.FirstTriggerProcessed.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await running;

        Assert.Equal(0, runtime.ObserveCount);
        Assert.Equal(0, runtime.LightingRestoreCount);
    }

    [Fact]
    public async Task PowerChanged_SkipsTransitionWhileLowPowerGateIsActive()
    {
        var source = new FakeLifecycleEventSource();
        var runtime = new RecordingRuntime(failFirstRefresh: false);
        var gate = new LowPowerGate();
        gate.Enter();
        var loop = new ServiceLifecycleRefreshLoop(
            runtime,
            source,
            gate,
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task running = loop.RunAsync(cancellation.Token);
        source.Publish(ServiceLifecycleTrigger.PowerChanged);
        await source.FirstTriggerProcessed.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await running;

        Assert.Equal(0, runtime.PowerSourceChangeCount);
        Assert.Equal(0, runtime.TotalOperationCount);
    }

    [Theory]
    [InlineData(ServiceLifecycleTrigger.DisplayOff)]
    [InlineData(ServiceLifecycleTrigger.DisplayOn)]
    public async Task DisplayTransition_SkipsSuppressionWhileLowPowerGateIsActive(
        ServiceLifecycleTrigger trigger)
    {
        var source = new FakeLifecycleEventSource();
        var runtime = new RecordingRuntime(failFirstRefresh: false);
        var gate = new LowPowerGate();
        gate.Enter();
        var loop = new ServiceLifecycleRefreshLoop(
            runtime,
            source,
            gate,
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task running = loop.RunAsync(cancellation.Token);
        source.Publish(trigger);
        await source.FirstTriggerProcessed.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await running;

        Assert.Equal(0, runtime.LightingSuppressionCount);
        Assert.Equal(0, runtime.TotalOperationCount);
    }

    [Fact]
    public async Task DeviceRemoved_InvalidatesButSkipsObservationWhileLowPowerGateIsActive()
    {
        var source = new FakeLifecycleEventSource();
        var runtime = new RecordingRuntime(failFirstRefresh: false);
        var gate = new LowPowerGate();
        gate.Enter();
        var loop = new ServiceLifecycleRefreshLoop(
            runtime,
            source,
            gate,
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task running = loop.RunAsync(cancellation.Token);
        source.Publish(ServiceLifecycleTrigger.DeviceRemoved);
        await source.FirstTriggerProcessed.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await running;

        Assert.Equal(1, runtime.LightingInvalidationCount);
        Assert.Equal(0, runtime.ObserveCount);
    }

    [Fact]
    public async Task Resumed_DoesNotReacquireRendererUntilLowPowerGateClears()
    {
        var source = new FakeLifecycleEventSource();
        var runtime = new RecordingRuntime(failFirstRefresh: false);
        var gate = new LowPowerGate();
        gate.Enter();
        var loop = new ServiceLifecycleRefreshLoop(
            runtime,
            source,
            gate,
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task running = loop.RunAsync(cancellation.Token);
        source.Publish(ServiceLifecycleTrigger.Resumed);
        await source.FirstTriggerProcessed.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await running;

        Assert.Equal(1, runtime.LightingInvalidationCount);
        Assert.Equal(1, runtime.ResumeValidationBeginCount);
        Assert.Equal(1, runtime.ResumeValidationCancelCount);
        Assert.Equal(0, runtime.PowerSourceChangeCount);
        Assert.Equal(0, runtime.LightingRestoreCount);
    }

    [Fact]
    public async Task Suspending_ReleasesDriverModeBeforeRendererInvalidation()
    {
        var source = new FakeLifecycleEventSource();
        var runtime = new RecordingRuntime(failFirstRefresh: false);
        var loop = new ServiceLifecycleRefreshLoop(
            runtime,
            source,
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task running = loop.RunAsync(cancellation.Token);
        source.Publish(ServiceLifecycleTrigger.Suspending);
        await runtime.TwoRefreshes.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await running;

        Assert.Equal(
            [
                RecordingRuntime.Operation.PrepareLowPower,
                RecordingRuntime.Operation.InvalidateLighting,
            ],
            runtime.Operations);
    }

    [Fact]
    public async Task DisplayOff_RestoresOwnedHyperBoostBeforeLightingSuppression()
    {
        var source = new FakeLifecycleEventSource();
        var runtime = new RecordingRuntime(failFirstRefresh: false);
        var loop = new ServiceLifecycleRefreshLoop(
            runtime,
            source,
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task running = loop.RunAsync(cancellation.Token);
        source.Publish(ServiceLifecycleTrigger.DisplayOff);
        await runtime.TwoRefreshes.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await running;

        Assert.Equal(
            [
                RecordingRuntime.Operation.PrepareDisplayOffHyperBoost,
                RecordingRuntime.Operation.LightingSuppression,
            ],
            runtime.Operations);
    }

    [Fact]
    public async Task Notifications_ProcessSequentiallyAndContinueAfterFailure()
    {
        var source = new FakeLifecycleEventSource();
        var runtime = new RecordingRuntime(failFirstRefresh: true);
        var loop = new ServiceLifecycleRefreshLoop(
            runtime,
            source,
            new LowPowerGate(),
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

        public TaskCompletionSource FirstTriggerProcessed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Publish(ServiceLifecycleTrigger trigger) =>
            notifications.Writer.TryWrite(trigger);

        public async IAsyncEnumerable<ServiceLifecycleTrigger> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await foreach (ServiceLifecycleTrigger trigger in
                notifications.Reader.ReadAllAsync(cancellationToken))
            {
                yield return trigger;
                FirstTriggerProcessed.TrySetResult();
            }
        }
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
        private int lowPowerPreparationCount;
        private int displayOffHyperBoostPreparationCount;
        private int resumeValidationBeginCount;
        private int resumeValidationCancelCount;
        private int totalOperationCount;

        public int ControllerPresenceRefreshCount =>
            Volatile.Read(ref controllerPresenceRefreshCount);

        public int ObserveCount => Volatile.Read(ref observeCount);

        public int PowerSourceChangeCount => Volatile.Read(ref powerSourceChangeCount);

        public int LightingInvalidationCount => Volatile.Read(ref lightingInvalidationCount);

        public int LightingRestoreCount => Volatile.Read(ref lightingRestoreCount);

        public int LightingSuppressionCount => Volatile.Read(ref lightingSuppressionCount);

        public int LowPowerPreparationCount => Volatile.Read(ref lowPowerPreparationCount);

        public int DisplayOffHyperBoostPreparationCount =>
            Volatile.Read(ref displayOffHyperBoostPreparationCount);

        public int ResumeValidationBeginCount =>
            Volatile.Read(ref resumeValidationBeginCount);

        public int ResumeValidationCancelCount =>
            Volatile.Read(ref resumeValidationCancelCount);

        public List<Operation> Operations { get; } = [];

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

        public Task BeginResumeValidationAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref resumeValidationBeginCount);
            return Task.CompletedTask;
        }

        public Task CancelResumeValidationAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref resumeValidationCancelCount);
            return Task.CompletedTask;
        }

        public async Task<ControlOperationResult> PrepareForLowPowerAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref lowPowerPreparationCount);
            await RecordRefreshAsync(Operation.PrepareLowPower);
            return new ControlOperationResult(ControlOperationStatus.Applied, "Released");
        }

        public async Task<ControlOperationResult> PrepareOwnedHyperBoostForDisplayOffAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref displayOffHyperBoostPreparationCount);
            await RecordRefreshAsync(Operation.PrepareDisplayOffHyperBoost);
            return new ControlOperationResult(ControlOperationStatus.Applied, "Restored");
        }

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
            lock (Operations)
            {
                Operations.Add(operation);
            }
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

        public enum Operation
        {
            Observe,
            PowerSourceChange,
            ControllerPresence,
            InvalidateLighting,
            RestoreLighting,
            LightingSuppression,
            PrepareLowPower,
            PrepareDisplayOffHyperBoost,
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
