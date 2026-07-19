using System.Threading.Channels;
using System.Collections.Concurrent;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ServiceReactiveLightingLoopTests
{
    [Fact]
    public async Task ConnectedServiceKeepsOneSourceForReactiveAndPerformanceShortcuts()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        List<Exception> failures = [];
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            failures.Add);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Reactive));
        await WaitUntilAsync(() => factory.CreateCount == 1);
        RecordingSource first = factory.Last!;

        client.Publish(Status(LightingRendererOwnershipState.Reactive) with { Revision = 2 });
        await Task.Delay(25);
        Assert.Equal(1, factory.CreateCount);

        first.Publish(new ReactiveKeyPulse(77));
        await WaitUntilAsync(() => client.Pulses.Count == 1);
        Assert.True(client.Pulses.TryPeek(out ReactiveKeyPulse pulse));
        Assert.Equal(77, pulse.MatrixSlot);

        client.Publish(Status(LightingRendererOwnershipState.Static) with { Revision = 3 });
        await Task.Delay(25);
        Assert.False(first.Disposed);
        first.Publish(new ReactiveKeyPulse(78));
        await Task.Delay(25);
        Assert.Single(client.Pulses);

        first.PublishPerformanceShortcut(PerformanceShortcutSource.DedicatedButton);
        await WaitUntilAsync(() => client.PerformanceShortcuts.Count == 1);

        client.Publish(Status(LightingRendererOwnershipState.Reactive) with { Revision = 4 });
        await Task.Delay(25);
        Assert.Equal(1, factory.CreateCount);
        client.Reconnect();
        await WaitUntilAsync(() => first.Disposed);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Empty(failures);
    }

    private static ServiceStatusSnapshot Status(LightingRendererOwnershipState ownership)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            Revision: 1,
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
            FullChargeOverride: false,
            LightingRendererOwnership: ownership);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class RecordingSourceFactory : IReactiveKeyboardSourceFactory
    {
        public int CreateCount { get; private set; }

        public RecordingSource? Last { get; private set; }

        public IReactiveKeyboardSource Create(Action<ReactiveKeyPulse> publish)
        {
            CreateCount++;
            return Last = new RecordingSource(publish, _ => { });
        }

        public IReactiveKeyboardSource Create(
            Action<ReactiveKeyPulse> publish,
            Action<PerformanceShortcutSource> publishPerformanceShortcut)
        {
            CreateCount++;
            return Last = new RecordingSource(publish, publishPerformanceShortcut);
        }
    }

    private sealed class RecordingSource(
        Action<ReactiveKeyPulse> publish,
        Action<PerformanceShortcutSource> publishPerformanceShortcut)
        : IReactiveKeyboardSource
    {
        public bool Disposed { get; private set; }

        public void Publish(ReactiveKeyPulse pulse) => publish(pulse);

        public void PublishPerformanceShortcut(PerformanceShortcutSource source) =>
            publishPerformanceShortcut(source);

        public void Dispose() => Disposed = true;
    }

    private sealed class WatchClient : IServiceClient
    {
        private readonly Channel<ServiceWatchUpdate> updates = Channel.CreateUnbounded<ServiceWatchUpdate>();

        public ConcurrentQueue<ReactiveKeyPulse> Pulses { get; } = new();

        public ConcurrentQueue<PerformanceShortcutSource> PerformanceShortcuts { get; } = new();

        public void Publish(ServiceStatusSnapshot status) =>
            Assert.True(updates.Writer.TryWrite(new ServiceWatchUpdate.Connected(status)));

        public void Reconnect() =>
            Assert.True(updates.Writer.TryWrite(new ServiceWatchUpdate.Reconnecting()));

        public async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await foreach (ServiceWatchUpdate update in updates.Reader.ReadAllAsync(token))
            {
                yield return update;
            }
        }

        public Task<ControlOperationResult> PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken token)
        {
            Pulses.Enqueue(pulse);
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

        public Task<ControlOperationResult> CyclePerformanceShortcutAsync(
            PerformanceShortcutSource source,
            CancellationToken token)
        {
            PerformanceShortcuts.Enqueue(source);
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Applied"));
        }

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
}
