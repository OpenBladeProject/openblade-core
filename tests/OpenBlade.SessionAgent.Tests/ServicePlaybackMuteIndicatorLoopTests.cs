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

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Delay(10, cancellationToken);
        }
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

    private sealed class WatchClient : IServiceClient
    {
        private readonly Channel<ServiceWatchUpdate> updates =
            Channel.CreateUnbounded<ServiceWatchUpdate>();

        public ConcurrentQueue<bool> Requests { get; } = new();

        public void PublishConnected() => updates.Writer.TryWrite(
            new ServiceWatchUpdate.Connected(Status()));

        public void PublishReconnecting() => updates.Writer.TryWrite(
            new ServiceWatchUpdate.Reconnecting());

        public async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken token)
        {
            await foreach (ServiceWatchUpdate update in updates.Reader.ReadAllAsync(token))
            {
                yield return update;
            }
        }

        public Task<ControlOperationResult> SetPlaybackMuteIndicatorAsync(
            bool muted,
            CancellationToken token)
        {
            Requests.Enqueue(muted);
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Applied"));
        }

        public Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token) =>
            throw new NotSupportedException();

        public Task<ServiceStatusSnapshot> ObserveStateAsync(
            bool includeLiveReadings,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DiagnosticArchive> ExportDiagnosticsAsync(
            bool liveReadingsEnabled,
            CancellationToken token) => throw new NotSupportedException();

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

        public Task<ControlOperationResult> SetBatteryProtectionAsync(
            bool enabled,
            CancellationToken token) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetFullChargeOverrideAsync(
            bool enabled,
            CancellationToken token) => throw new NotSupportedException();
    }

    private static ServiceStatusSnapshot Status()
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
            DeviceCapabilities.Unavailable(),
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
