using System.Threading.Channels;
using System.Collections.Concurrent;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ServiceReactiveLightingLoopTests
{
    [Theory]
    [InlineData(ControlOperationStatus.Applied, false)]
    [InlineData(ControlOperationStatus.Stored, true)]
    [InlineData(ControlOperationStatus.Blocked, true)]
    [InlineData(ControlOperationStatus.Failed, true)]
    public async Task PhysicalShortcut_ShowsImmediateSelectionAndCorrectsRejectedChange(
        ControlOperationStatus status,
        bool shouldShowFailure)
    {
        const string message = "Fn+P selected Silent.";
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
            notifications,
            performanceShortcutQuietPeriod: TimeSpan.FromMilliseconds(25));
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            KeyboardDriverModeActive = true,
        });
        await WaitUntilAsync(() => factory.CreateCount == 1);
        await WaitUntilAsync(() => factory.Last!.IsDriverModeActive());
        factory.Last!.PublishPerformanceShortcut(PerformanceShortcutSource.FunctionP);
        await WaitUntilAsync(() => client.PerformanceSelections.Count == 1);
        await WaitUntilAsync(() => notifications.Selected.Count == 1);
        Assert.Equal(PerformancePreset.Silent, Assert.Single(notifications.Selected));
        Assert.Empty(notifications.AppliedMessages);
        if (shouldShowFailure)
        {
            await WaitUntilAsync(() => notifications.FailureMessages.Count == 1);
            Assert.Equal(message, Assert.Single(notifications.FailureMessages));
        }
        else
        {
            await Task.Delay(50);
            Assert.Empty(notifications.FailureMessages);
        }

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task PhysicalShortcut_NotifiesBeforeFirmwareOperationCompletes()
    {
        var completion = new TaskCompletionSource<ControlOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new WatchClient
        {
            PerformanceShortcutCompletion = completion,
        };
        var factory = new RecordingSourceFactory();
        var notifications = new RecordingNotificationSink();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            shortcutNotifications: notifications,
            performanceShortcutQuietPeriod: TimeSpan.FromMilliseconds(25));
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            KeyboardDriverModeActive = true,
        });
        await WaitUntilAsync(() => factory.CreateCount == 1);
        await WaitUntilAsync(() => factory.Last!.IsDriverModeActive());
        factory.Last!.PublishPerformanceShortcut(PerformanceShortcutSource.DedicatedButton);

        await WaitUntilAsync(() => notifications.Selected.Count == 1);
        Assert.False(completion.Task.IsCompleted);
        Assert.Equal(PerformancePreset.Silent, Assert.Single(notifications.Selected));

        completion.SetResult(new ControlOperationResult(
            ControlOperationStatus.Applied,
            "The performance button selected Silent."));
        await WaitUntilAsync(() => client.PerformanceSelections.Count == 1);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task RapidPerformancePressesNotifyEachSelectionButWriteOnlyFinalMode()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        var notifications = new RecordingNotificationSink();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            shortcutNotifications: notifications,
            performanceShortcutQuietPeriod: TimeSpan.FromMilliseconds(40));
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            KeyboardDriverModeActive = true,
        });
        await WaitUntilAsync(() => factory.CreateCount == 1);
        await WaitUntilAsync(() => factory.Last!.IsDriverModeActive());

        factory.Last!.PublishPerformanceShortcut(PerformanceShortcutSource.DedicatedButton);
        factory.Last.PublishPerformanceShortcut(PerformanceShortcutSource.DedicatedButton);
        factory.Last.PublishPerformanceShortcut(PerformanceShortcutSource.DedicatedButton);

        await WaitUntilAsync(() => notifications.Selected.Count == 3);
        await WaitUntilAsync(() => client.PerformanceSelections.Count == 1);
        Assert.Equal(
            [
                PerformancePreset.Silent,
                PerformancePreset.Performance,
                PerformancePreset.Custom,
            ],
            notifications.Selected.ToArray());
        Assert.Equal(
            PerformancePreset.Custom,
            Assert.Single(client.PerformanceSelections).Value);
        Assert.Empty(client.PerformanceShortcuts);

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
        Assert.Empty(notifications.Selected);
        Assert.Empty(notifications.AppliedMessages);
        Assert.Empty(notifications.FailureMessages);
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
            new ThrowingNotificationSink(),
            performanceShortcutQuietPeriod: TimeSpan.FromMilliseconds(25));
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Static));
        await WaitUntilAsync(() => factory.CreateCount == 1);
        factory.Last!.PublishPerformanceShortcut(PerformanceShortcutSource.DedicatedButton);
        await WaitUntilAsync(
            () => client.PerformanceSelections.Count + client.PerformanceShortcuts.Count == 1);
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
            failures.Add,
            performanceShortcutQuietPeriod: TimeSpan.FromMilliseconds(25));
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
        await WaitUntilAsync(() => client.PerformanceSelections.Count == 1);

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
    public async Task ConfiguredSpecialKey_WorksBeforeTheServiceConnects()
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

        await WaitUntilAsync(() => factory.CreateCount == 1);
        Assert.True(factory.Last!.ShouldOverrideSpecialKey(SpecialKeySlot.M3));
        Assert.False(factory.Last.ShouldOverrideSpecialKey(SpecialKeySlot.M2));

        factory.Last.PublishSpecialKey(SpecialKeySlot.M3);
        await WaitUntilAsync(() => delivered.Count == 1);
        Assert.Equal(SpecialKeySlot.M3, Assert.Single(delivered));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task DirectDefaultLaptopKey_BypassesFnLayerBinding()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        var configured = new ConcurrentQueue<SpecialKeySlot>();
        var defaults = new ConcurrentQueue<SpecialKeySlot>();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            shouldOverrideSpecialKey: _ => true,
            executeSpecialKeyAsync: (slot, _) =>
            {
                configured.Enqueue(slot);
                return Task.CompletedTask;
            },
            executeDefaultLaptopKeyAsync: (slot, _) =>
            {
                defaults.Enqueue(slot);
                return Task.CompletedTask;
            });
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        await WaitUntilAsync(() => factory.CreateCount == 1);
        factory.Last!.PublishDefaultLaptopKey(SpecialKeySlot.M3);
        await WaitUntilAsync(() => defaults.Count == 1);

        Assert.Equal(SpecialKeySlot.M3, Assert.Single(defaults));
        Assert.Empty(configured);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task AuthoritativeStatusControlsFunctionRowTranslation()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        var delivered = new ConcurrentQueue<FunctionRowAction>();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            executeFunctionRowActionAsync: (action, _) =>
            {
                delivered.Enqueue(action);
                return Task.CompletedTask;
            });
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        await WaitUntilAsync(() => factory.CreateCount == 1);
        Assert.Null(factory.Last!.ReadFunctionKeyPrimaryMode());
        Assert.False(factory.Last.IsDriverModeActive());

        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys,
            KeyboardDriverModeActive = true,
        });
        await WaitUntilAsync(
            () => factory.Last.ReadFunctionKeyPrimaryMode()
                == FunctionKeyPrimaryMode.MultimediaKeys
                && factory.Last.IsDriverModeActive());

        factory.Last.PublishFunctionRowAction(FunctionRowAction.VolumeMute);
        await WaitUntilAsync(() => delivered.Count == 1);
        Assert.Equal(FunctionRowAction.VolumeMute, Assert.Single(delivered));

        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            Revision = 2,
            FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys,
            KeyboardDriverModeActive = false,
        });
        await WaitUntilAsync(
            () => factory.Last.ReadFunctionKeyPrimaryMode()
                == FunctionKeyPrimaryMode.FunctionKeys
                && !factory.Last.IsDriverModeActive());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task UnknownVerifiedReadbackPreservesLastConfirmedFunctionRowTranslation()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        await WaitUntilAsync(() => factory.CreateCount == 1);
        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys,
            KeyboardDriverModeActive = true,
        });
        await WaitUntilAsync(
            () => factory.Last!.ReadFunctionKeyPrimaryMode()
                == FunctionKeyPrimaryMode.MultimediaKeys
                && factory.Last.IsDriverModeActive());

        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            Revision = 2,
            FunctionKeyPrimary = null,
            KeyboardDriverModeActive = null,
        });
        await Task.Delay(50);

        Assert.Equal(
            FunctionKeyPrimaryMode.MultimediaKeys,
            factory.Last!.ReadFunctionKeyPrimaryMode());
        Assert.True(factory.Last.IsDriverModeActive());

        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            Revision = 3,
            DeviceVerified = false,
            FunctionKeyPrimary = null,
            KeyboardDriverModeActive = null,
        });
        await WaitUntilAsync(
            () => factory.Last!.ReadFunctionKeyPrimaryMode() is null
                && !factory.Last.IsDriverModeActive());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task UnknownReadbackUsesActiveProfileAfterDeviceVerificationRecovers()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        await WaitUntilAsync(() => factory.CreateCount == 1);
        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            DeviceVerified = false,
            FunctionKeyPrimary = null,
            KeyboardDriverModeActive = null,
        });
        await WaitUntilAsync(
            () => factory.Last!.ReadFunctionKeyPrimaryMode() is null
                && !factory.Last.IsDriverModeActive());

        ServiceStatusSnapshot recovered = Status(
            LightingRendererOwnershipState.Static);
        recovered = recovered with
        {
            Revision = 2,
            FunctionKeyPrimary = null,
            KeyboardDriverModeActive = true,
            Profiles = recovered.Profiles with
            {
                PluggedIn = recovered.Profiles.PluggedIn with
                {
                    LaptopBehavior = new LaptopBehaviorProfileSettings(
                        StartupAnimationEnabled: null,
                        FunctionKeyPrimary: FunctionKeyPrimaryMode.MultimediaKeys,
                        GamingModeEnabled: null),
                },
            },
        };
        client.Publish(recovered);

        await WaitUntilAsync(
            () => factory.Last!.ReadFunctionKeyPrimaryMode()
                    == FunctionKeyPrimaryMode.MultimediaKeys
                && factory.Last.IsDriverModeActive());

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

        public IReactiveKeyboardSource Create(
            Action<ReactiveKeyPulse> publish,
            Action<PerformanceShortcutSource> publishPerformanceShortcut,
            Action<SpecialKeySlot> publishSpecialKey,
            Func<SpecialKeySlot, bool> shouldOverrideSpecialKey,
            Action<FunctionRowAction> publishFunctionRowAction,
            Func<FunctionKeyPrimaryMode?> readFunctionKeyPrimaryMode,
            Func<bool> isDriverModeActive)
        {
            CreateCount++;
            return Last = new RecordingSource(
                publish,
                publishPerformanceShortcut,
                publishSpecialKey,
                shouldOverrideSpecialKey,
                _ => { },
                publishFunctionRowAction,
                readFunctionKeyPrimaryMode,
                isDriverModeActive);
        }

        public IReactiveKeyboardSource Create(
            Action<ReactiveKeyPulse> publish,
            Action<PerformanceShortcutSource> publishPerformanceShortcut,
            Action<SpecialKeySlot> publishSpecialKey,
            Func<SpecialKeySlot, bool> shouldOverrideSpecialKey,
            Action<SpecialKeySlot> publishDefaultLaptopKey,
            Action<FunctionRowAction> publishFunctionRowAction,
            Func<FunctionKeyPrimaryMode?> readFunctionKeyPrimaryMode,
            Func<bool> isDriverModeActive)
        {
            CreateCount++;
            return Last = new RecordingSource(
                publish,
                publishPerformanceShortcut,
                publishSpecialKey,
                shouldOverrideSpecialKey,
                publishDefaultLaptopKey,
                publishFunctionRowAction,
                readFunctionKeyPrimaryMode,
                isDriverModeActive);
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
        private readonly Action<SpecialKeySlot> publishDefaultLaptopKey;
        private readonly Action<FunctionRowAction> publishFunctionRowAction;
        private readonly Func<FunctionKeyPrimaryMode?> readFunctionKeyPrimaryMode;
        private readonly Func<bool> isDriverModeActive;

        public RecordingSource(
            Action<ReactiveKeyPulse> publish,
            Action<PerformanceShortcutSource> publishPerformanceShortcut,
            Action<SpecialKeySlot>? publishSpecialKey = null,
            Func<SpecialKeySlot, bool>? shouldOverrideSpecialKey = null,
            Action<SpecialKeySlot>? publishDefaultLaptopKey = null,
            Action<FunctionRowAction>? publishFunctionRowAction = null,
            Func<FunctionKeyPrimaryMode?>? readFunctionKeyPrimaryMode = null,
            Func<bool>? isDriverModeActive = null)
        {
            this.publish = publish;
            this.publishPerformanceShortcut = publishPerformanceShortcut;
            this.publishSpecialKey = publishSpecialKey ?? (_ => { });
            this.shouldOverrideSpecialKey = shouldOverrideSpecialKey ?? (_ => false);
            this.publishDefaultLaptopKey = publishDefaultLaptopKey ?? (_ => { });
            this.publishFunctionRowAction = publishFunctionRowAction ?? (_ => { });
            this.readFunctionKeyPrimaryMode = readFunctionKeyPrimaryMode ?? (() => null);
            this.isDriverModeActive = isDriverModeActive ?? (() => false);
        }

        public bool Disposed { get; private set; }

        public Task Completion => completion.Task;

        public void Publish(ReactiveKeyPulse pulse) => publish(pulse);

        public void PublishPerformanceShortcut(PerformanceShortcutSource source) =>
            publishPerformanceShortcut(source);

        public void PublishSpecialKey(SpecialKeySlot slot) => publishSpecialKey(slot);

        public void PublishDefaultLaptopKey(SpecialKeySlot slot) =>
            publishDefaultLaptopKey(slot);

        public bool ShouldOverrideSpecialKey(SpecialKeySlot slot) =>
            shouldOverrideSpecialKey(slot);

        public void PublishFunctionRowAction(FunctionRowAction action) =>
            publishFunctionRowAction(action);

        public FunctionKeyPrimaryMode? ReadFunctionKeyPrimaryMode() =>
            readFunctionKeyPrimaryMode();

        public bool IsDriverModeActive() => isDriverModeActive();

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

        public ConcurrentQueue<PerformanceOverride> PerformanceSelections { get; } = new();

        public ControlOperationResult PerformanceShortcutResult { get; init; } = new(
            ControlOperationStatus.Applied,
            "Applied");

        public TaskCompletionSource<ControlOperationResult>? PerformanceShortcutCompletion
        {
            get;
            init;
        }

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
            CancellationToken token) => SetPerformanceConfigurationOverrideAsync(
                value is null
                    ? null
                    : new PerformanceOverride(value.Value, value.Lifetime),
                token);

        public Task<ControlOperationResult> SetPerformanceConfigurationOverrideAsync(
            PerformanceOverride? value,
            CancellationToken token)
        {
            if (value is not null)
            {
                PerformanceSelections.Enqueue(value);
            }

            return PerformanceShortcutCompletion?.Task
                ?? Task.FromResult(PerformanceShortcutResult);
        }

        public Task<ControlOperationResult> CyclePerformanceShortcutAsync(
            PerformanceShortcutSource source,
            CancellationToken token)
        {
            PerformanceShortcuts.Enqueue(source);
            return PerformanceShortcutCompletion?.Task
                ?? Task.FromResult(PerformanceShortcutResult);
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
        internal ConcurrentQueue<PerformancePreset> Selected { get; } = new();

        internal ConcurrentQueue<string> AppliedMessages { get; } = new();

        internal ConcurrentQueue<string> FailureMessages { get; } = new();

        public void ShowSelected(PerformancePreset preset) => Selected.Enqueue(preset);

        public void ShowApplied(string message) => AppliedMessages.Enqueue(message);

        public void ShowFailed(string message) => FailureMessages.Enqueue(message);

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingNotificationSink : IPerformanceShortcutNotificationSink
    {
        public void ShowSelected(PerformancePreset preset) =>
            throw new InvalidOperationException("Notification unavailable");

        public void ShowApplied(string message) =>
            throw new InvalidOperationException("Notification unavailable");

        public void ShowFailed(string message) =>
            throw new InvalidOperationException("Notification unavailable");

        public void Dispose()
        {
        }
    }
}
