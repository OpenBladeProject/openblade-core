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
        private int refreshCount;

        public int RefreshCount => Volatile.Read(ref refreshCount);

        public int MaximumConcurrentRefreshes { get; private set; }

        public TaskCompletionSource TwoRefreshes { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RefreshAsync(CancellationToken token)
        {
            int concurrent = Interlocked.Increment(ref concurrentRefreshes);
            MaximumConcurrentRefreshes = Math.Max(MaximumConcurrentRefreshes, concurrent);
            int current = Interlocked.Increment(ref refreshCount);
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
}
