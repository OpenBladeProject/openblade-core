using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsServiceLifecycleEventSourceTests
{
    [Fact]
    public void Dispose_CleanupFailurePropagatesAndRetainsSubscription()
    {
        var backend = new FakeWindowsLifecycleNotificationBackend
        {
            DisposeFailure = new InvalidOperationException("unregister failed"),
        };
        var source = new WindowsServiceLifecycleEventSource(backend);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            source.Dispose);

        Assert.Equal("unregister failed", exception.Message);
        ForceGarbageCollection();
        Assert.True(backend.SubscriptionReference!.IsAlive);
    }

    [Fact]
    public async Task Dispose_ConcurrentCleanupFailureIsReplayedToEveryCaller()
    {
        var expected = new InvalidOperationException("unregister failed");
        using var disposeEntered = new ManualResetEventSlim();
        using var releaseDispose = new ManualResetEventSlim();
        var backend = new FakeWindowsLifecycleNotificationBackend
        {
            DisposeFailure = expected,
            DisposeEntered = disposeEntered,
            ReleaseDispose = releaseDispose,
        };
        var source = new WindowsServiceLifecycleEventSource(backend);
        Task<Exception?> first = Record.ExceptionAsync(() => Task.Run(source.Dispose));
        Assert.True(disposeEntered.Wait(TimeSpan.FromSeconds(5)));
        Task<Exception?> second = Record.ExceptionAsync(() => Task.Run(source.Dispose));

        try
        {
            Task timeout = Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.Same(timeout, await Task.WhenAny(second, timeout));
        }
        finally
        {
            releaseDispose.Set();
        }

        Assert.Same(expected, await first);
        Assert.Same(expected, await second);
    }

    [Fact]
    public void Constructor_SubscribesBeforeWatchBegins()
    {
        var backend = new FakeWindowsLifecycleNotificationBackend();
        using var source = new WindowsServiceLifecycleEventSource(backend);

        Assert.Equal(1, backend.SubscribeCount);
    }

    [Fact]
    public async Task WatchAsync_PrioritizesNonDeviceTriggersAndPreservesDeviceOrder()
    {
        var backend = new FakeWindowsLifecycleNotificationBackend();
        using var source = new WindowsServiceLifecycleEventSource(backend);
        backend.Publish(ServiceLifecycleTrigger.DeviceRemoved);
        backend.Publish(ServiceLifecycleTrigger.DeviceArrived);
        backend.Publish(ServiceLifecycleTrigger.Resumed);
        backend.Publish(ServiceLifecycleTrigger.Suspending);
        backend.Publish(ServiceLifecycleTrigger.PowerChanged);
        await using IAsyncEnumerator<ServiceLifecycleTrigger> notifications =
            source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();

        var actual = new List<ServiceLifecycleTrigger>();
        for (int index = 0; index < 5; index++)
        {
            Assert.True(await notifications.MoveNextAsync());
            actual.Add(notifications.Current);
        }

        Assert.Equal(
            [
                ServiceLifecycleTrigger.PowerChanged,
                ServiceLifecycleTrigger.Suspending,
                ServiceLifecycleTrigger.Resumed,
                ServiceLifecycleTrigger.DeviceRemoved,
                ServiceLifecycleTrigger.DeviceArrived,
            ],
            actual);
    }

    [Fact]
    public async Task WatchAsync_PreservesRapidArrivalThenRemovalOrder()
    {
        var backend = new FakeWindowsLifecycleNotificationBackend();
        using var source = new WindowsServiceLifecycleEventSource(backend);
        backend.Publish(ServiceLifecycleTrigger.DeviceArrived);
        backend.Publish(ServiceLifecycleTrigger.DeviceRemoved);
        await using IAsyncEnumerator<ServiceLifecycleTrigger> notifications =
            source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal(ServiceLifecycleTrigger.DeviceArrived, notifications.Current);
        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal(ServiceLifecycleTrigger.DeviceRemoved, notifications.Current);
    }

    [Fact]
    public async Task WatchAsync_CoalescesOnlyConsecutiveDuplicateDeviceNotifications()
    {
        var backend = new FakeWindowsLifecycleNotificationBackend();
        using var source = new WindowsServiceLifecycleEventSource(backend);
        backend.Publish(ServiceLifecycleTrigger.DeviceRemoved);
        backend.Publish(ServiceLifecycleTrigger.DeviceRemoved);
        backend.Publish(ServiceLifecycleTrigger.DeviceArrived);
        backend.Publish(ServiceLifecycleTrigger.DeviceArrived);
        backend.Publish(ServiceLifecycleTrigger.DeviceRemoved);
        await using IAsyncEnumerator<ServiceLifecycleTrigger> notifications =
            source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();

        var actual = new List<ServiceLifecycleTrigger>();
        for (int index = 0; index < 3; index++)
        {
            Assert.True(await notifications.MoveNextAsync());
            actual.Add(notifications.Current);
        }

        Assert.Equal(
            [
                ServiceLifecycleTrigger.DeviceRemoved,
                ServiceLifecycleTrigger.DeviceArrived,
                ServiceLifecycleTrigger.DeviceRemoved,
            ],
            actual);
    }

    [Theory]
    [InlineData(0x0004u, ServiceLifecycleTrigger.Suspending)]
    [InlineData(0x0007u, ServiceLifecycleTrigger.Resumed)]
    [InlineData(0x0012u, ServiceLifecycleTrigger.Resumed)]
    public void SuspendResumeNotification_MapsOfficialPowerBroadcastValues(
        uint value,
        ServiceLifecycleTrigger expected)
    {
        Assert.Equal(
            expected,
            NativeWindowsLifecycleNotificationBackend
                .ClassifySuspendResumeNotification(value));
    }

    [Fact]
    public void SuspendResumeNotification_IgnoresUnrelatedValues()
    {
        Assert.Null(
            NativeWindowsLifecycleNotificationBackend
                .ClassifySuspendResumeNotification(0x1234));
    }

    [Theory]
    [InlineData(1u, 0u, ServiceLifecycleTrigger.DisplayOff)]
    [InlineData(2u, 0u, ServiceLifecycleTrigger.DisplayOff)]
    [InlineData(0u, 1u, ServiceLifecycleTrigger.DisplayOn)]
    [InlineData(0u, 2u, ServiceLifecycleTrigger.DisplayOn)]
    public void ConsoleDisplayTransition_MapsOffAndOnLifecycleBoundaries(
        uint previous,
        uint current,
        ServiceLifecycleTrigger expected)
    {
        Assert.Equal(
            expected,
            NativeWindowsLifecycleNotificationBackend
                .ClassifyConsoleDisplayTransition(previous, current));
    }

    [Theory]
    [InlineData(null, 1u)]
    [InlineData(null, 0u)]
    [InlineData(1u, 1u)]
    [InlineData(0u, 0u)]
    [InlineData(2u, 1u)]
    [InlineData(1u, 2u)]
    public void ConsoleDisplayTransition_IgnoresInitialDuplicateAndActiveStateChanges(
        uint? previous,
        uint current)
    {
        Assert.Null(
            NativeWindowsLifecycleNotificationBackend
                .ClassifyConsoleDisplayTransition(previous, current));
    }

    [Fact]
    public async Task WatchAsync_CoalescesDuplicatePendingTriggersWithoutDisplacingPower()
    {
        var backend = new FakeWindowsLifecycleNotificationBackend();
        using var source = new WindowsServiceLifecycleEventSource(backend);
        backend.Publish(ServiceLifecycleTrigger.PowerChanged);
        backend.Publish(ServiceLifecycleTrigger.Resumed);
        backend.Publish(ServiceLifecycleTrigger.DeviceArrived);
        backend.Publish(ServiceLifecycleTrigger.DeviceRemoved);
        backend.Publish(ServiceLifecycleTrigger.PowerChanged);
        backend.Publish(ServiceLifecycleTrigger.Resumed);
        await using IAsyncEnumerator<ServiceLifecycleTrigger> notifications =
            source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal(ServiceLifecycleTrigger.PowerChanged, notifications.Current);
        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal(ServiceLifecycleTrigger.Resumed, notifications.Current);
        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal(ServiceLifecycleTrigger.DeviceArrived, notifications.Current);
        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal(ServiceLifecycleTrigger.DeviceRemoved, notifications.Current);
    }

    [Fact]
    public async Task WatchAsync_SuppressesSequentialDuplicateResumeUntilWindowExpires()
    {
        var backend = new FakeWindowsLifecycleNotificationBackend();
        long tick = 1_000;
        using var source = new WindowsServiceLifecycleEventSource(backend, () => tick);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ServiceLifecycleTrigger> notifications =
            source.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

        backend.Publish(ServiceLifecycleTrigger.Resumed);
        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal(ServiceLifecycleTrigger.Resumed, notifications.Current);

        backend.Publish(ServiceLifecycleTrigger.Resumed);
        backend.Publish(ServiceLifecycleTrigger.DeviceArrived);
        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal(ServiceLifecycleTrigger.DeviceArrived, notifications.Current);

        tick += WindowsServiceLifecycleEventSource.DuplicateResumeWindowMilliseconds + 1;
        backend.Publish(ServiceLifecycleTrigger.Resumed);
        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal(ServiceLifecycleTrigger.Resumed, notifications.Current);
    }

    [Fact]
    public async Task WatchAsync_SuspendImmediatelyRearmsResume()
    {
        var backend = new FakeWindowsLifecycleNotificationBackend();
        using var source = new WindowsServiceLifecycleEventSource(backend, () => 1_000);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ServiceLifecycleTrigger> notifications =
            source.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

        backend.Publish(ServiceLifecycleTrigger.Resumed);
        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal(ServiceLifecycleTrigger.Resumed, notifications.Current);

        backend.Publish(ServiceLifecycleTrigger.Suspending);
        backend.Publish(ServiceLifecycleTrigger.Resumed);
        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal(ServiceLifecycleTrigger.Suspending, notifications.Current);
        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal(ServiceLifecycleTrigger.Resumed, notifications.Current);
    }

    [Fact]
    public async Task WatchAsync_PublishAfterSignalDrainBeforeBitExchangeIsNotLost()
    {
        var backend = new FakeWindowsLifecycleNotificationBackend();
        var boundaryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBoundary = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int boundaryCalls = 0;
        using var source = new WindowsServiceLifecycleEventSource(
            backend,
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref boundaryCalls) == 1)
                {
                    boundaryEntered.TrySetResult();
                    await releaseBoundary.Task.WaitAsync(cancellationToken);
                }
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        backend.Publish(ServiceLifecycleTrigger.Resumed);
        await using IAsyncEnumerator<ServiceLifecycleTrigger> notifications =
            source.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Task<bool> first = notifications.MoveNextAsync().AsTask();
        await boundaryEntered.Task.WaitAsync(timeout.Token);

        backend.Publish(ServiceLifecycleTrigger.PowerChanged);
        releaseBoundary.TrySetResult();

        Assert.True(await first.WaitAsync(timeout.Token));
        Assert.Equal(ServiceLifecycleTrigger.PowerChanged, notifications.Current);
        Assert.True(await notifications.MoveNextAsync().AsTask().WaitAsync(timeout.Token));
        Assert.Equal(ServiceLifecycleTrigger.Resumed, notifications.Current);
    }

    [Fact]
    public async Task WatchAsync_SecondWatcherFailsExplicitly()
    {
        var backend = new FakeWindowsLifecycleNotificationBackend();
        using var source = new WindowsServiceLifecycleEventSource(backend);
        backend.Publish(ServiceLifecycleTrigger.Resumed);
        await using IAsyncEnumerator<ServiceLifecycleTrigger> first =
            source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        await using IAsyncEnumerator<ServiceLifecycleTrigger> second =
            source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => second.MoveNextAsync().AsTask());

        Assert.Contains("watcher", exception.Message, StringComparison.OrdinalIgnoreCase);
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
    public void RegistrationCleanup_UnregistersBothPowerSettingHandles()
    {
        var api = new FakeWindowsLifecycleUnregistrationApi();
        var cleanup = new WindowsLifecycleRegistrationCleanup(
            api,
            deviceRegistration: IntPtr.Zero,
            powerSourceRegistration: (IntPtr)2,
            suspendResumeRegistration: IntPtr.Zero,
            callbackLifetime: new object(),
            consoleDisplayRegistration: (IntPtr)4);

        cleanup.Dispose();

        Assert.Equal(2, api.PowerSourceUnregisterCount);
    }

    [Fact]
    public async Task Dispose_StopsPublishingCompletesWatcherAndUnregisters()
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

        Task<bool> completion = notifications.MoveNextAsync().AsTask();
        source.Dispose();
        Assert.False(await completion.WaitAsync(cancellation.Token));
        Assert.True(backend.SubscriptionDisposed);
        backend.Publish(ServiceLifecycleTrigger.PowerChanged);
    }

    private sealed class FakeWindowsLifecycleNotificationBackend
        : IWindowsLifecycleNotificationBackend
    {
        private Action<ServiceLifecycleTrigger>? publish;

        public bool SubscriptionDisposed { get; private set; }

        public Exception? DisposeFailure { get; init; }

        public ManualResetEventSlim? DisposeEntered { get; init; }

        public ManualResetEventSlim? ReleaseDispose { get; init; }

        public WeakReference? SubscriptionReference { get; private set; }

        public int SubscribeCount { get; private set; }

        public IDisposable Subscribe(Action<ServiceLifecycleTrigger> publish)
        {
            SubscribeCount++;
            this.publish = publish;
            var subscription = new Subscription(() =>
            {
                DisposeEntered?.Set();
                ReleaseDispose?.Wait();
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
