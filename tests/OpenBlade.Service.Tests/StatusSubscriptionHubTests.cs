using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class StatusSubscriptionHubTests
{
    [Fact]
    public async Task Subscribe_WithoutBootstrapSnapshot_WaitsForFirstPublication()
    {
        var hub = new StatusSubscriptionHub(new FakeTimeProvider());
        await using StatusSubscriptionLease lease = hub.Subscribe(CancellationToken.None);
        await using IAsyncEnumerator<ServiceStatusSnapshot> reader =
            lease.ReadAllAsync().GetAsyncEnumerator();

        hub.Publish(CreateStatus(0));

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(0, reader.Current.Revision);
    }

    [Fact]
    public async Task Subscribe_DeliversCurrentSnapshotImmediately()
    {
        var hub = new StatusSubscriptionHub(CreateStatus(4), new FakeTimeProvider());
        await using StatusSubscriptionLease lease = hub.Subscribe(CancellationToken.None);
        await using IAsyncEnumerator<ServiceStatusSnapshot> reader =
            lease.ReadAllAsync().GetAsyncEnumerator();

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(4, reader.Current.Revision);
    }

    [Fact]
    public async Task Publish_DeliversOnlyMonotonicallyNewerRevisions()
    {
        var hub = new StatusSubscriptionHub(CreateStatus(1), new FakeTimeProvider());
        await using StatusSubscriptionLease lease = hub.Subscribe(CancellationToken.None);
        await using IAsyncEnumerator<ServiceStatusSnapshot> reader =
            lease.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());

        hub.Publish(CreateStatus(3));
        hub.Publish(CreateStatus(2));

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(3, reader.Current.Revision);
    }

    [Fact]
    public async Task Publish_ReplacesUnreadSnapshotWithLatestValue()
    {
        var hub = new StatusSubscriptionHub(CreateStatus(0), new FakeTimeProvider());
        await using StatusSubscriptionLease lease = hub.Subscribe(CancellationToken.None);
        await using IAsyncEnumerator<ServiceStatusSnapshot> reader =
            lease.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());

        hub.Publish(CreateStatus(1));
        hub.Publish(CreateStatus(2));
        hub.Publish(CreateStatus(3));

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(3, reader.Current.Revision);
    }

    [Fact]
    public async Task Subscribe_RejectsLeaseBeyondBoundedCapacity()
    {
        var hub = new StatusSubscriptionHub(CreateStatus(0), new FakeTimeProvider());
        StatusSubscriptionLease[] leases = Enumerable
            .Range(0, StatusSubscriptionHub.MaximumSubscribers)
            .Select(_ => hub.Subscribe(CancellationToken.None))
            .ToArray();

        Assert.Throws<InvalidOperationException>(() => hub.Subscribe(CancellationToken.None));

        foreach (StatusSubscriptionLease lease in leases)
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task Subscribe_AllowsThreeSessionAgentObserversAndTray()
    {
        var hub = new StatusSubscriptionHub(CreateStatus(0), new FakeTimeProvider());
        StatusSubscriptionLease[] leases = Enumerable
            .Range(0, 4)
            .Select(_ => hub.Subscribe(CancellationToken.None))
            .ToArray();

        Assert.Equal(4, leases.Length);

        foreach (StatusSubscriptionLease lease in leases)
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task Dispose_ReleasesSlotExactlyOnce()
    {
        var hub = new StatusSubscriptionHub(CreateStatus(0), new FakeTimeProvider());
        StatusSubscriptionLease[] leases = Enumerable
            .Range(0, StatusSubscriptionHub.MaximumSubscribers)
            .Select(_ => hub.Subscribe(CancellationToken.None))
            .ToArray();

        await leases[0].DisposeAsync();
        await leases[0].DisposeAsync();
        await using StatusSubscriptionLease replacement = hub.Subscribe(CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => hub.Subscribe(CancellationToken.None));

        foreach (StatusSubscriptionLease lease in leases.Skip(1))
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public void Capacity_ReservesFourConnectionsForShortRequests()
    {
        Assert.True(
            NamedPipeControlServer.MaximumServerInstances
            - StatusSubscriptionHub.MaximumSubscribers >= 4);
    }

    [Fact]
    public async Task CallerCancellation_CompletesReaderWithCancellation()
    {
        var hub = new StatusSubscriptionHub(CreateStatus(0), new FakeTimeProvider());
        using CancellationTokenSource caller = new();
        await using StatusSubscriptionLease lease = hub.Subscribe(caller.Token);
        await using IAsyncEnumerator<ServiceStatusSnapshot> reader =
            lease.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());

        caller.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await reader.MoveNextAsync().AsTask());
        Assert.Equal(caller.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task LifetimeTimer_GracefullyCompletesAfterFifteenMinutes()
    {
        var time = new FakeTimeProvider();
        var hub = new StatusSubscriptionHub(CreateStatus(0), time);
        await using StatusSubscriptionLease lease = hub.Subscribe(CancellationToken.None);
        await using IAsyncEnumerator<ServiceStatusSnapshot> reader =
            lease.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());

        time.Advance(TimeSpan.FromMinutes(15));

        Assert.False(await reader.MoveNextAsync());
    }

    private static ServiceStatusSnapshot CreateStatus(long revision)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            revision,
            false,
            true,
            [],
            null,
            [],
            "Unavailable",
            DeviceCapabilities.Unavailable(),
            null,
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            PowerSource.PluggedIn,
            null,
            null,
            null,
            null,
            false);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<FakeTimer> timers = [];
        private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state, utcNow + dueTime, period);
            lock (sync)
            {
                timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            FakeTimer[] due;
            lock (sync)
            {
                utcNow += amount;
                due = timers.Where(timer => timer.IsDue(utcNow)).ToArray();
            }

            foreach (FakeTimer timer in due)
            {
                timer.Fire(utcNow);
            }
        }

        private void Remove(FakeTimer timer)
        {
            lock (sync)
            {
                timers.Remove(timer);
            }
        }

        private sealed class FakeTimer(
            FakeTimeProvider owner,
            TimerCallback callback,
            object? state,
            DateTimeOffset dueAt,
            TimeSpan period) : ITimer
        {
            private int disposed;
            private DateTimeOffset next = dueAt;

            public bool IsDue(DateTimeOffset now) =>
                Volatile.Read(ref disposed) == 0 && now >= next;

            public void Fire(DateTimeOffset now)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                callback(state);
                if (period == Timeout.InfiniteTimeSpan)
                {
                    Dispose();
                }
                else
                {
                    next = now + period;
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return false;
                }

                next = owner.GetUtcNow() + dueTime;
                period = newPeriod;
                return true;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    owner.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
