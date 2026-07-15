using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsServiceLifecycleEventSourceTests
{
    [Fact]
    public async Task WatchAsync_CleanupFailurePropagatesAndRetainsSubscription()
    {
        var backend = new FakeWindowsLifecycleNotificationBackend
        {
            DisposeFailure = new InvalidOperationException("unregister failed"),
        };
        var source = new WindowsServiceLifecycleEventSource(backend);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                await using IAsyncEnumerator<ServiceLifecycleTrigger> notifications =
                    source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
                Task<bool> pending = notifications.MoveNextAsync().AsTask();
                backend.Publish(ServiceLifecycleTrigger.Resumed);
                Assert.True(await pending);
            });

        Assert.Equal("unregister failed", exception.Message);
        ForceGarbageCollection();
        Assert.True(backend.SubscriptionReference!.IsAlive);
    }

    [Fact]
    public void RegistrationCleanup_ChecksEveryResultAndRepeatsFailureForEveryCaller()
    {
        var api = new FakeWindowsLifecycleUnregistrationApi
        {
            DeviceResult = 0x13,
            PowerSourceResult = 5,
            SuspendResumeResult = 6,
        };
        var cleanup = new WindowsLifecycleRegistrationCleanup(
            api,
            deviceRegistration: (IntPtr)1,
            powerSourceRegistration: (IntPtr)2,
            suspendResumeRegistration: (IntPtr)3,
            callbackLifetime: new object());

        AggregateException first = Assert.Throws<AggregateException>(cleanup.Dispose);
        AggregateException second = Assert.Throws<AggregateException>(cleanup.Dispose);

        Assert.Same(first, second);
        Assert.Equal(3, first.InnerExceptions.Count);
        Assert.Equal(1, api.DeviceUnregisterCount);
        Assert.Equal(1, api.PowerSourceUnregisterCount);
        Assert.Equal(1, api.SuspendResumeUnregisterCount);
    }

    [Fact]
    public void RegistrationCleanup_FailureRetainsCallbackLifetime()
    {
        WeakReference callbackLifetime = CreateFailedCleanupWithWeakCallbackLifetime();

        ForceGarbageCollection();

        Assert.True(callbackLifetime.IsAlive);
    }

    [Fact]
    public async Task RegistrationCleanup_ConcurrentSuccessfulDisposeUnregistersOnce()
    {
        var api = new FakeWindowsLifecycleUnregistrationApi();
        var cleanup = new WindowsLifecycleRegistrationCleanup(
            api,
            deviceRegistration: (IntPtr)1,
            powerSourceRegistration: (IntPtr)2,
            suspendResumeRegistration: (IntPtr)3,
            callbackLifetime: new object());

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(cleanup.Dispose)));

        Assert.Equal(1, api.DeviceUnregisterCount);
        Assert.Equal(1, api.PowerSourceUnregisterCount);
        Assert.Equal(1, api.SuspendResumeUnregisterCount);
    }

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

        public Exception? DisposeFailure { get; init; }

        public WeakReference? SubscriptionReference { get; private set; }

        public IDisposable Subscribe(Action<ServiceLifecycleTrigger> publish)
        {
            this.publish = publish;
            var subscription = new Subscription(() =>
            {
                SubscriptionDisposed = true;
                if (DisposeFailure is not null)
                {
                    throw DisposeFailure;
                }
            });
            SubscriptionReference = new WeakReference(subscription);
            return subscription;
        }

        public void Publish(ServiceLifecycleTrigger trigger) => publish!(trigger);

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private static void ForceGarbageCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static WeakReference CreateFailedCleanupWithWeakCallbackLifetime()
    {
        var callbackLifetime = new object();
        var reference = new WeakReference(callbackLifetime);
        var cleanup = new WindowsLifecycleRegistrationCleanup(
            new FakeWindowsLifecycleUnregistrationApi { DeviceResult = 0x13 },
            deviceRegistration: (IntPtr)1,
            powerSourceRegistration: IntPtr.Zero,
            suspendResumeRegistration: IntPtr.Zero,
            callbackLifetime);
        _ = Assert.Throws<AggregateException>(cleanup.Dispose);
        return reference;
    }

    private sealed class FakeWindowsLifecycleUnregistrationApi
        : IWindowsLifecycleUnregistrationApi
    {
        public uint DeviceResult { get; init; }
        public uint PowerSourceResult { get; init; }
        public uint SuspendResumeResult { get; init; }
        public int DeviceUnregisterCount { get; private set; }
        public int PowerSourceUnregisterCount { get; private set; }
        public int SuspendResumeUnregisterCount { get; private set; }

        public uint UnregisterDevice(IntPtr registration)
        {
            DeviceUnregisterCount++;
            return DeviceResult;
        }

        public uint UnregisterPowerSource(IntPtr registration)
        {
            PowerSourceUnregisterCount++;
            return PowerSourceResult;
        }

        public uint UnregisterSuspendResume(IntPtr registration)
        {
            SuspendResumeUnregisterCount++;
            return SuspendResumeResult;
        }
    }
}
