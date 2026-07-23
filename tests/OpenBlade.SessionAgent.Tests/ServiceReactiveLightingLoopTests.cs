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
    public async Task StatusStreamFailure_PreservesLastKnownInputOwnershipDuringRetry()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        var failures = new ConcurrentQueue<Exception>();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            failures.Enqueue);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Reactive));
        await WaitUntilAsync(() => factory.CreateCount == 1);
        RecordingSource source = factory.Last!;
        client.FailWatch(new IOException("subscription interrupted"));
        await WaitUntilAsync(() => failures.Count == 1);

        Assert.False(source.Disposed);
        source.Publish(new ReactiveKeyPulse(53));
        await WaitUntilAsync(() => client.Pulses.Count == 1);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task TransientUnavailableStatus_PreservesReactiveInputOwnership()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        ServiceStatusSnapshot reactive = Status(LightingRendererOwnershipState.Reactive);
        client.Publish(reactive);
        await WaitUntilAsync(() => factory.CreateCount == 1);

        client.Publish(reactive with
        {
            Revision = 2,
            DeviceVerified = false,
            WritesBlocked = true,
            PotentialControllers = ["RazerAppEngine"],
            Message = "Checking the laptop connection.",
            LightingRendererOwnership = LightingRendererOwnershipState.None,
        });
        await Task.Delay(25);

        factory.Last!.Publish(new ReactiveKeyPulse(53));
        await WaitUntilAsync(() => client.Pulses.Count == 1);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task TemporaryAccessRefresh_PreservesPreviouslyActiveInputWhenDesiredStateIsStale()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Reactive));
        await WaitUntilAsync(() => factory.CreateCount == 1);

        client.Publish(Status(LightingRendererOwnershipState.None) with
        {
            Revision = 2,
            DeviceVerified = true,
            WritesBlocked = true,
            Message = "Refreshing laptop controls after a temporary device-access failure. Last read failed.",
        });
        await Task.Delay(25);

        factory.Last!.Publish(new ReactiveKeyPulse(53));
        await WaitUntilAsync(() => client.Pulses.Count == 1);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task ExplicitEffectChange_ClearsReactiveInputOwnership()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Reactive));
        await WaitUntilAsync(() => factory.CreateCount == 1);
        client.Publish(Status(LightingRendererOwnershipState.Static) with { Revision = 2 });
        await Task.Delay(25);

        factory.Last!.Publish(new ReactiveKeyPulse(53));
        await Task.Delay(50);
        Assert.Empty(client.Pulses);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task FailedInputSource_IsRecreatedAndResumesDelivery()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        var failures = new ConcurrentQueue<Exception>();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            failures.Enqueue);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Reactive));
        await WaitUntilAsync(() => factory.CreateCount == 1);
        RecordingSource first = factory.Last!;
        first.Fail(new IOException("message loop stopped"));

        await WaitUntilAsync(() => factory.CreateCount == 2);
        Assert.True(first.Disposed);
        Assert.NotSame(first, factory.Last);
        factory.Last!.Publish(new ReactiveKeyPulse(53));
        await WaitUntilAsync(() => client.Pulses.Count == 1);
        Assert.True(failures.TryDequeue(out Exception? failure));
        Assert.IsType<IOException>(failure);

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

    [Fact]
    public async Task ConfiguredSpecialKey_UsesTheSharedInputSource()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        var delivered = new ConcurrentQueue<SpecialKeySlot>();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            shouldOverrideSpecialKey: slot => slot == SpecialKeySlot.M3,
            executeSpecialKeyAsync: (slot, _) =>
            {
                delivered.Enqueue(slot);
                return Task.CompletedTask;
            });
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Static));
        await WaitUntilAsync(() => factory.CreateCount == 1);
        Assert.True(factory.Last!.ShouldOverrideSpecialKey(SpecialKeySlot.M3));
        Assert.False(factory.Last.ShouldOverrideSpecialKey(SpecialKeySlot.M2));

        factory.Last.PublishSpecialKey(SpecialKeySlot.M3);
        await WaitUntilAsync(() => delivered.Count == 1);
        Assert.Equal(SpecialKeySlot.M3, Assert.Single(delivered));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    private static ServiceStatusSnapshot Status(LightingRendererOwnershipState ownership)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        LightingSettings desiredLighting = ownership is LightingRendererOwnershipState.Reactive
            ? LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0),
                brightness: 50,
                ReactiveLightingDuration.Medium)
            : profiles.PluggedIn.Lighting;
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
                    desiredLighting,
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

        public IReactiveKeyboardSource Create(
            Action<ReactiveKeyPulse> publish,
            Action<PerformanceShortcutSource> publishPerformanceShortcut,
            Action<SpecialKeySlot> publishSpecialKey,
            Func<SpecialKeySlot, bool> shouldOverrideSpecialKey)
        {
            CreateCount++;
            return Last = new RecordingSource(
                publish,
                publishPerformanceShortcut,
                publishSpecialKey,
                shouldOverrideSpecialKey);
        }
    }

    private sealed class RecordingSource : IReactiveKeyboardSource
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action<ReactiveKeyPulse> publish;
        private readonly Action<PerformanceShortcutSource> publishPerformanceShortcut;
        private readonly Action<SpecialKeySlot> publishSpecialKey;
        private readonly Func<SpecialKeySlot, bool> shouldOverrideSpecialKey;

        public RecordingSource(
            Action<ReactiveKeyPulse> publish,
            Action<PerformanceShortcutSource> publishPerformanceShortcut,
            Action<SpecialKeySlot>? publishSpecialKey = null,
            Func<SpecialKeySlot, bool>? shouldOverrideSpecialKey = null)
        {
            this.publish = publish;
            this.publishPerformanceShortcut = publishPerformanceShortcut;
            this.publishSpecialKey = publishSpecialKey ?? (_ => { });
            this.shouldOverrideSpecialKey = shouldOverrideSpecialKey ?? (_ => false);
        }

        public bool Disposed { get; private set; }

        public Task Completion => completion.Task;

        public void Publish(ReactiveKeyPulse pulse) => publish(pulse);

        public void PublishPerformanceShortcut(PerformanceShortcutSource source) =>
            publishPerformanceShortcut(source);

        public void PublishSpecialKey(SpecialKeySlot slot) => publishSpecialKey(slot);

        public bool ShouldOverrideSpecialKey(SpecialKeySlot slot) =>
            shouldOverrideSpecialKey(slot);

        public void Fail(Exception exception) => completion.TrySetException(exception);

        public void Dispose()
        {
            Disposed = true;
            completion.TrySetResult();
        }
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

        public void FailWatch(Exception exception) =>
            Assert.True(updates.Writer.TryComplete(exception));

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
