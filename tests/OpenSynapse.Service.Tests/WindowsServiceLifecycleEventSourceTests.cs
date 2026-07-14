using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class WindowsServiceLifecycleEventSourceTests
{
    [Fact]
    public async Task WatchAsync_ForwardsNotificationsAndDisposesSubscription()
    {
        var backend = new FakeWindowsLifecycleNotificationBackend();
        var source = new WindowsServiceLifecycleEventSource(backend);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using IAsyncEnumerator<ServiceLifecycleTrigger> notifications =
            source.WatchAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Task<bool> pending = notifications.MoveNextAsync().AsTask();
        backend.Publish(ServiceLifecycleTrigger.Resumed);

        Assert.True(await pending.WaitAsync(cancellation.Token));
        Assert.Equal(ServiceLifecycleTrigger.Resumed, notifications.Current);

        await notifications.DisposeAsync();
        Assert.True(backend.SubscriptionDisposed);
    }

    private sealed class FakeWindowsLifecycleNotificationBackend
        : IWindowsLifecycleNotificationBackend
    {
        private Action<ServiceLifecycleTrigger>? publish;

        public bool SubscriptionDisposed { get; private set; }

        public IDisposable Subscribe(Action<ServiceLifecycleTrigger> publish)
        {
            this.publish = publish;
            return new Subscription(() => SubscriptionDisposed = true);
        }

        public void Publish(ServiceLifecycleTrigger trigger) => publish!(trigger);

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
