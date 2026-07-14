using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSynapse.Contracts;
using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class ServiceLifecycleRefreshLoopTests
{
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
