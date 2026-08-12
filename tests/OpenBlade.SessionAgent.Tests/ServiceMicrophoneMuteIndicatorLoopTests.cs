using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ServiceMicrophoneMuteIndicatorLoopTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MutedStartupStateIsSentAndReconnectResendsCurrentState(
        bool stateArrivesFirst)
    {
        var client = new WatchClient();
        var source = new MutableMuteSource();
        var loop = new ServiceMicrophoneMuteIndicatorLoop(client, source);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        if (stateArrivesFirst)
        {
            source.Publish(true);
            client.PublishConnected();
        }
        else
        {
            client.PublishConnected();
            source.Publish(true);
        }
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
    public async Task UnavailableStateDoesNotReplaceLastDeliveredState()
    {
        var client = new WatchClient();
        var source = new MutableMuteSource();
        List<Exception> failures = [];
        var loop = new ServiceMicrophoneMuteIndicatorLoop(
            client,
            source,
            failures.Add);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.PublishConnected();
        source.Publish(true);
        await WaitUntilAsync(() => client.Requests.Count == 1, cancellation.Token);
        source.Publish(null);
        await Task.Delay(20, cancellation.Token);

        Assert.Equal([true], client.Requests);
        Assert.Empty(failures);

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task UnsupportedLaptopDoesNotSendIndicator()
    {
        var client = new WatchClient();
        var source = new MutableMuteSource();
        var loop = new ServiceMicrophoneMuteIndicatorLoop(client, source);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.PublishConnected(supportsMicrophoneMuteIndicator: false);
        source.Publish(true);
        await Task.Delay(20, cancellation.Token);

        Assert.Empty(client.Requests);

        await cancellation.CancelAsync();
        await running;
    }

    [Theory]
    [InlineData(new bool[] { true }, true)]
    [InlineData(new bool[] { true, true, true }, true)]
    [InlineData(new bool[] { false }, false)]
    [InlineData(new bool[] { true, false }, false)]
    [InlineData(new bool[] { }, false)]
    public void CaptureEndpointStatesRequireEveryEndpointMuted(
        bool[] states,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsMicrophoneMuteStateSource.AggregateIndicatorState(states));
    }

    private sealed class MutableMuteSource : IMicrophoneMuteStateSource
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

        public ConcurrentQueue<bool> Requests { get; } = new();

        public void PublishConnected(bool supportsMicrophoneMuteIndicator = true) =>
            updates.Writer.TryWrite(new ServiceWatchUpdate.Connected(
                Status(supportsMicrophoneMuteIndicator)));

        public void PublishReconnecting() => updates.Writer.TryWrite(
            new ServiceWatchUpdate.Reconnecting());

        public override async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken token)
        {
            await foreach (ServiceWatchUpdate update in updates.Reader.ReadAllAsync(token))
            {
                yield return update;
            }
        }

        public override Task<ControlOperationResult> ApplyOneShotLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken token)
        {
            Assert.Equal(
                OneShotLaptopControl.MicrophoneMuteIndicator,
                value.Control);
            Requests.Enqueue(value.Enabled!.Value);
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Applied"));
        }
    }

    private static ServiceStatusSnapshot Status(
        bool supportsMicrophoneMuteIndicator)
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
                OneShotLaptopControls = supportsMicrophoneMuteIndicator
                    ? new HashSet<OneShotLaptopControl>
                    {
                        OneShotLaptopControl.MicrophoneMuteIndicator,
                    }
                    : new HashSet<OneShotLaptopControl>(),
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

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        CancellationToken token)
    {
        while (!predicate())
        {
            await Task.Delay(10, token);
        }
    }
}
