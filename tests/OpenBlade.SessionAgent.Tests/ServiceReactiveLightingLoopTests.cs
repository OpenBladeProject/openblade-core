using System.Threading.Channels;
using System.Collections.Concurrent;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ServiceReactiveLightingLoopTests
{
    [Theory]
    [InlineData(ControlOperationStatus.Applied, true)]
    [InlineData(ControlOperationStatus.Stored, false)]
    [InlineData(ControlOperationStatus.Blocked, false)]
    [InlineData(ControlOperationStatus.Failed, false)]
    public async Task PhysicalShortcut_NotifiesOnlyAfterAppliedResult(
        ControlOperationStatus status,
        bool shouldNotify)
    {
        const string message = "Fn+P selected Performance.";
        var client = new WatchClient
        {
            PerformanceShortcutResult = new(status, message),
        };
        var factory = new RecordingSourceFactory();
        var notifications = new RecordingNotificationSink();
        List<Exception> failures = [];
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            failures.Add,
            notifications);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Static));
        await WaitUntilAsync(() => factory.CreateCount == 1);
        factory.Last!.PublishPerformanceShortcut(PerformanceShortcutSource.FunctionP);
        await WaitUntilAsync(() => client.PerformanceShortcuts.Count == 1);
        if (shouldNotify)
        {
            await WaitUntilAsync(() => notifications.Messages.Count == 1);
            Assert.Equal(message, Assert.Single(notifications.Messages));
        }
        else
        {
            await Task.Delay(50);
            Assert.Empty(notifications.Messages);
        }

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task StatusHydrationAndReconnect_PreserveInputWithoutNotifications()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        var notifications = new RecordingNotificationSink();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            shortcutNotifications: notifications);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Static));
        await WaitUntilAsync(() => factory.CreateCount == 1);
        client.Publish(Status(LightingRendererOwnershipState.Reactive) with { Revision = 2 });
        client.Reconnect();
        await Task.Delay(50);

        Assert.False(factory.Last!.Disposed);
        factory.Last.Publish(new ReactiveKeyPulse(53));
        await WaitUntilAsync(() => client.Pulses.Count == 1);
        Assert.Empty(notifications.Messages);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task NotificationFailure_DoesNotFailTheAppliedShortcutLoop()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        var failures = new ConcurrentQueue<Exception>();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            failures.Enqueue,
            new ThrowingNotificationSink());
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Static));
        await WaitUntilAsync(() => factory.CreateCount == 1);
        factory.Last!.PublishPerformanceShortcut(PerformanceShortcutSource.DedicatedButton);
        await WaitUntilAsync(() => client.PerformanceShortcuts.Count == 1);
        await WaitUntilAsync(() => failures.Count == 1);

        Assert.True(failures.TryDequeue(out Exception? failure));
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Empty(failures);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

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

        client.Publish(Status(LightingRendererOwnershipState.Ripple) with { Revision = 5 });
        await Task.Delay(25);
        first.Publish(new ReactiveKeyPulse(79));
        await WaitUntilAsync(() => client.Pulses.Count == 2);
        Assert.Equal(1, factory.CreateCount);

        client.Reconnect();
        await Task.Delay(25);
        Assert.False(first.Disposed);
        first.Publish(new ReactiveKeyPulse(80));
        await WaitUntilAsync(() => client.Pulses.Count == 3);

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

        public ControlOperationResult PerformanceShortcutResult { get; init; } = new(
            ControlOperationStatus.Applied,
            "Applied");

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
            return Task.FromResult(PerformanceShortcutResult);
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

    private sealed class RecordingNotificationSink : IPerformanceShortcutNotificationSink
    {
        internal ConcurrentQueue<string> Messages { get; } = new();

        public void ShowApplied(string message) => Messages.Enqueue(message);

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingNotificationSink : IPerformanceShortcutNotificationSink
    {
        public void ShowApplied(string message) =>
            throw new InvalidOperationException("Notification unavailable");

        public void Dispose()
        {
        }
    }
}
