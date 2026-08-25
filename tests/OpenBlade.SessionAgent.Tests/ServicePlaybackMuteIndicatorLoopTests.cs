using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ServicePlaybackMuteIndicatorLoopTests
{
    [Fact]
    public async Task MuteChangesAreSentOnceAndReconnectResendsCurrentState()
    {
        var client = new WatchClient();
        var source = new MutableMuteSource();
        var loop = new ServicePlaybackMuteIndicatorLoop(
            client,
            source);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        source.Publish(true);
        client.PublishConnected();
        await WaitUntilAsync(() => client.Requests.Count == 1, cancellation.Token);
        Assert.Equal([true], client.Requests);

        source.Publish(true);
        await Task.Delay(20, cancellation.Token);
        Assert.Equal([true], client.Requests);

        source.Publish(false);
        await WaitUntilAsync(() => client.Requests.Count == 2, cancellation.Token);
        Assert.Equal([true, false], client.Requests);

        client.PublishReconnecting();
        client.PublishConnected();
        await WaitUntilAsync(() => client.Requests.Count == 3, cancellation.Token);
        Assert.Equal([true, false, false], client.Requests);

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task ReopenedSubscriptionResendsStateWithoutOrdinaryStatusDuplicates()
    {
        var client = new WatchClient();
        var source = new MutableMuteSource();
        var loop = new ServicePlaybackMuteIndicatorLoop(client, source);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        source.Publish(false);
        client.PublishConnected(subscriptionOpened: true);
        await WaitUntilAsync(() => client.Requests.Count == 1, cancellation.Token);

        client.PublishConnected();
        await WaitUntilAsync(
            () => client.StatusUpdatesAcknowledged >= 2,
            cancellation.Token);
        Assert.Equal([false], client.Requests);

        client.PublishConnected(subscriptionOpened: true);
        await WaitUntilAsync(() => client.Requests.Count == 2, cancellation.Token);
        Assert.Equal([false, false], client.Requests);

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task UnavailableEndpointDoesNotSendOrReportFailure()
    {
        var client = new WatchClient();
        var source = new MutableMuteSource();
        List<Exception> failures = [];
        var loop = new ServicePlaybackMuteIndicatorLoop(
            client,
            source,
            failures.Add);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.PublishConnected();
        source.Publish(null);
        await Task.Delay(20, cancellation.Token);

        Assert.Empty(client.Requests);
        Assert.Empty(failures);

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task UnsupportedLaptopDoesNotSendOrRetry()
    {
        var client = new WatchClient();
        var source = new MutableMuteSource();
        var loop = new ServicePlaybackMuteIndicatorLoop(client, source);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.PublishConnected(supportsPlaybackMuteIndicator: false);
        source.Publish(true);
        await Task.Delay(20, cancellation.Token);

        Assert.Empty(client.Requests);

        await cancellation.CancelAsync();
        await running;
    }

    private sealed class MutableMuteSource : IPlaybackMuteStateSource
    {
        private readonly Channel<bool?> updates = Channel.CreateUnbounded<bool?>();

        public void Publish(bool? muted) => updates.Writer.TryWrite(muted);

        public async IAsyncEnumerable<bool?> WatchMutedAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await foreach (bool? muted in updates.Reader.ReadAllAsync(cancellationToken))
            {
                yield return muted;
            }
        }
    }

    private sealed class WatchClient : OpenBlade.Testing.UnavailableServiceClient
    {
        private readonly Channel<ServiceWatchUpdate> updates =
            Channel.CreateUnbounded<ServiceWatchUpdate>();
        private int statusUpdatesAcknowledged;

        public ConcurrentQueue<bool> Requests { get; } = new();

        public int StatusUpdatesAcknowledged =>
            Volatile.Read(ref statusUpdatesAcknowledged);

        public void PublishConnected(
            bool supportsPlaybackMuteIndicator = true,
            bool subscriptionOpened = false) =>
            updates.Writer.TryWrite(new ServiceWatchUpdate.Connected(
                Status(supportsPlaybackMuteIndicator))
            {
                SubscriptionOpened = subscriptionOpened,
            });

        public void PublishReconnecting() => updates.Writer.TryWrite(
            new ServiceWatchUpdate.Reconnecting());

        public override async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken token)
        {
            await foreach (ServiceWatchUpdate update in updates.Reader.ReadAllAsync(token))
            {
                yield return update;
                Interlocked.Increment(ref statusUpdatesAcknowledged);
            }
        }

        public override Task<ControlOperationResult> SetPlaybackMuteIndicatorAsync(
            bool muted,
            CancellationToken token)
        {
            Requests.Enqueue(muted);
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Applied"));
        }

    }

    private static ServiceStatusSnapshot Status(bool supportsPlaybackMuteIndicator)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            1,
            DeviceVerified: true,
            WritesBlocked: false,
            [],
            null,
            [],
            "Ready",
            DeviceCapabilities.Unavailable() with
            {
                SupportsPlaybackMuteIndicator = supportsPlaybackMuteIndicator,
            },
            null,
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Balanced,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(
                    FanMode.Automatic,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(
                    profiles.PluggedIn.Lighting,
                    SettingValueSource.PowerProfile),
                new DesiredBatteryState(
                    profiles.BatteryPolicy,
                    protectionEnabled: true,
                    temporaryFullCharge: false)),
            PowerSource.PluggedIn,
            null,
            null,
            null,
            null,
            FullChargeOverride: false);
    }
}
