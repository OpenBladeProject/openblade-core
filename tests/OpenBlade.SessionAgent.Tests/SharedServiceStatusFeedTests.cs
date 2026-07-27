using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SharedServiceStatusFeedTests
{
    [Fact]
    public async Task MultipleConsumersShareOneUpstreamSubscription()
    {
        var upstream = new FakeStatusWatcher();
        await using var feed = new SharedServiceStatusFeed(upstream);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<ServiceWatchUpdate> first = ReadOneAsync(feed, cancellation.Token);
        Task<ServiceWatchUpdate> second = ReadOneAsync(feed, cancellation.Token);
        Task running = feed.RunAsync(cancellation.Token);

        await WaitUntilAsync(() => feed.SubscriberCount == 2, cancellation.Token);
        ServiceWatchUpdate update = Connected(1);
        upstream.Publish(update);

        Assert.Same(update, await first);
        Assert.Same(update, await second);
        Assert.Equal(1, upstream.SubscriptionCount);

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task SlowConsumerReceivesLatestPendingUpdate()
    {
        var upstream = new FakeStatusWatcher();
        await using var feed = new SharedServiceStatusFeed(upstream);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = feed.RunAsync(cancellation.Token);
        await using IAsyncEnumerator<ServiceWatchUpdate> reader = feed
            .WatchStatusAsync(cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        ValueTask<bool> firstMove = reader.MoveNextAsync();
        await WaitUntilAsync(() => feed.SubscriberCount == 1, cancellation.Token);

        upstream.Publish(Connected(1));
        Assert.True(await firstMove);
        Assert.Equal(1, Revision(reader.Current));

        // With no second MoveNext pending, revisions 2 and 3 must compete for
        // the subscriber's single buffered slot. Wait until the feed has
        // actually processed both updates before reading that slot.
        upstream.Publish(Connected(2));
        upstream.Publish(Connected(3));
        await WaitUntilAsync(
            () => feed.LatestUpdate is ServiceWatchUpdate.Connected connected
                && connected.Status.Revision == 3,
            cancellation.Token);

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(3, Revision(reader.Current));

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task NewConsumerReceivesLatestValueImmediately()
    {
        var upstream = new FakeStatusWatcher();
        await using var feed = new SharedServiceStatusFeed(upstream);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = feed.RunAsync(cancellation.Token);
        upstream.Publish(Connected(7));
        upstream.Publish(Connected(8));
        await WaitUntilAsync(
            () => feed.LatestUpdate is ServiceWatchUpdate.Connected connected
                && connected.Status.Revision == 8,
            cancellation.Token);

        ServiceWatchUpdate replayed = await ReadOneAsync(feed, cancellation.Token);

        Assert.Equal(8, Revision(replayed));
        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task CanceledConsumerIsRemoved()
    {
        var upstream = new FakeStatusWatcher();
        await using var feed = new SharedServiceStatusFeed(upstream);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var consumerCancellation = new CancellationTokenSource();
        Task running = feed.RunAsync(lifetime.Token);
        await using IAsyncEnumerator<ServiceWatchUpdate> reader = feed
            .WatchStatusAsync(consumerCancellation.Token)
            .GetAsyncEnumerator(consumerCancellation.Token);
        ValueTask<bool> pending = reader.MoveNextAsync();
        await WaitUntilAsync(() => feed.SubscriberCount == 1, lifetime.Token);

        await consumerCancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await pending.AsTask());
        await WaitUntilAsync(() => feed.SubscriberCount == 0, lifetime.Token);
        await lifetime.CancelAsync();
        await running;
    }

    [Fact]
    public async Task UpstreamCompletionCompletesConsumers()
    {
        var upstream = new FakeStatusWatcher();
        await using var feed = new SharedServiceStatusFeed(upstream);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = feed.RunAsync(cancellation.Token);
        await using IAsyncEnumerator<ServiceWatchUpdate> reader = feed
            .WatchStatusAsync(cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        ValueTask<bool> pending = reader.MoveNextAsync();
        await WaitUntilAsync(() => feed.SubscriberCount == 1, cancellation.Token);

        upstream.Complete();

        Assert.False(await pending);
        await running;
        Assert.Equal(0, feed.SubscriberCount);
    }

    private static async Task<ServiceWatchUpdate> ReadOneAsync(
        IServiceStatusWatcher watcher,
        CancellationToken cancellationToken)
    {
        await foreach (ServiceWatchUpdate update in watcher
            .WatchStatusAsync(cancellationToken)
            .WithCancellation(cancellationToken))
        {
            return update;
        }
        throw new InvalidOperationException("The status feed completed without an update.");
    }

    private static ServiceWatchUpdate Connected(long revision) =>
        new ServiceWatchUpdate.Connected(Status(revision));

    private static long Revision(ServiceWatchUpdate update) =>
        Assert.IsType<ServiceWatchUpdate.Connected>(update).Status.Revision;

    private static ServiceStatusSnapshot Status(long revision)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            revision,
            DeviceVerified: true,
            WritesBlocked: false,
            PotentialControllers: [],
            ControllerDetectionWarning: null,
            ExternalChanges: [],
            Message: "Ready",
            Capabilities: DeviceCapabilities.Unavailable(),
            ConfirmedState: null,
            Profiles: profiles,
            DesiredState: new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(
                    profiles.PluggedIn.Performance,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(
                    profiles.PluggedIn.Fans,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(
                    profiles.PluggedIn.Lighting,
                    SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            ActivePowerSource: PowerSource.PluggedIn,
            LiveReadings: null,
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false);
    }

    private sealed class FakeStatusWatcher : IServiceStatusWatcher
    {
        private readonly Channel<ServiceWatchUpdate> updates =
            Channel.CreateUnbounded<ServiceWatchUpdate>();
        private int subscriptionCount;

        public int SubscriptionCount => Volatile.Read(ref subscriptionCount);

        public void Publish(ServiceWatchUpdate update) => updates.Writer.TryWrite(update);

        public void Complete() => updates.Writer.TryComplete();

        public async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken token)
        {
            Interlocked.Increment(ref subscriptionCount);
            await foreach (ServiceWatchUpdate update in updates.Reader.ReadAllAsync(token))
            {
                yield return update;
            }
        }
    }
}
