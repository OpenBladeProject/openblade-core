using System.Collections.Concurrent;
using System.Threading.Channels;
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
            new ServiceReactiveLightingLoopOptions(
                failures.Add,
                notifications,
                PerformanceShortcutQuietPeriod: TimeSpan.FromMilliseconds(25)));
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
        await WaitUntilAsync(() => client.PerformancePreviews.Count == 2);
        Assert.Equal(
            PerformancePreset.Silent,
            client.PerformancePreviews.First().Preset);
        Assert.Null(client.PerformancePreviews.Last().Preset);
        Assert.Equal(shouldShowFailure ? 1 : 0, client.ObservationCount);

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
            new ServiceReactiveLightingLoopOptions(
                ShortcutNotifications: notifications,
                PerformanceShortcutQuietPeriod: TimeSpan.FromMilliseconds(25)));
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

    [Theory]
    [InlineData(ControlOperationStatus.Stored)]
    [InlineData(ControlOperationStatus.Blocked)]
    [InlineData(ControlOperationStatus.Failed)]
    public async Task PhysicalShortcut_DoesNotWarnWhenFreshReadbackConfirmsSelection(
        ControlOperationStatus status)
    {
        var client = new WatchClient
        {
            PerformanceShortcutResult = new(status, "Performance was not confirmed."),
            ObservationStatus = Status(LightingRendererOwnershipState.Static) with
            {
                ConfirmedState = new ConfirmedDeviceState(
                    PerformancePreset.Silent,
                    Fans: null,
                    Battery: null,
                    Lighting: null),
            },
        };
        var factory = new RecordingSourceFactory();
        var notifications = new RecordingNotificationSink();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            new ServiceReactiveLightingLoopOptions(
                ShortcutNotifications: notifications,
                PerformanceShortcutQuietPeriod: TimeSpan.FromMilliseconds(25)));
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            KeyboardDriverModeActive = true,
        });
        await WaitUntilAsync(() => factory.CreateCount == 1);
        await WaitUntilAsync(() => factory.Last!.IsDriverModeActive());
        factory.Last!.PublishPerformanceShortcut(PerformanceShortcutSource.DedicatedButton);

        await WaitUntilAsync(() => client.ObservationCount == 1);
        await WaitUntilAsync(() => client.PerformancePreviews.Count == 2);
        await Task.Delay(50);

        Assert.Equal(PerformancePreset.Silent, Assert.Single(notifications.Selected));
        Assert.Empty(notifications.FailureMessages);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task PhysicalShortcut_DoesNotWarnWhenRequestFaultsButReadbackConfirmsSelection()
    {
        var completion = new TaskCompletionSource<ControlOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new WatchClient
        {
            PerformanceShortcutCompletion = completion,
            ObservationStatus = Status(LightingRendererOwnershipState.Static) with
            {
                ConfirmedState = new ConfirmedDeviceState(
                    PerformancePreset.Silent,
                    Fans: null,
                    Battery: null,
                    Lighting: null),
            },
        };
        var factory = new RecordingSourceFactory();
        var notifications = new RecordingNotificationSink();
        var failures = new ConcurrentQueue<Exception>();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            new ServiceReactiveLightingLoopOptions(
                failures.Enqueue,
                notifications,
                PerformanceShortcutQuietPeriod: TimeSpan.FromMilliseconds(25)));
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
        completion.SetException(new IOException("The response pipe closed."));

        await WaitUntilAsync(() => client.ObservationCount == 1);
        await WaitUntilAsync(() => failures.Count == 1);
        await Task.Delay(50);

        Assert.IsType<IOException>(Assert.Single(failures));
        Assert.Empty(notifications.FailureMessages);

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
            new ServiceReactiveLightingLoopOptions(
                ShortcutNotifications: notifications,
                PerformanceShortcutQuietPeriod: TimeSpan.FromMilliseconds(40)));
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
    public async Task Dispose_DiscardsQueuedPreviewBeforeItCanReachFirmware()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        var notifications = new RecordingNotificationSink();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            new ServiceReactiveLightingLoopOptions(
                ShortcutNotifications: notifications,
                PerformanceShortcutQuietPeriod: TimeSpan.FromSeconds(5)));
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        ServiceStatusSnapshot status = Status(LightingRendererOwnershipState.Static) with
        {
            KeyboardDriverModeActive = true,
        };
        client.Publish(status);
        await WaitUntilAsync(() => factory.CreateCount == 1);
        await WaitUntilAsync(() => factory.Last!.IsDriverModeActive());
        factory.Last!.PublishPerformanceShortcut(PerformanceShortcutSource.FunctionP);
        await WaitUntilAsync(() => notifications.Selected.Count == 1);
        await WaitUntilAsync(() => client.PerformancePreviews.Count == 1);

        await loop.DisposeAsync();
        await WaitUntilAsync(() => client.PerformancePreviews.Count == 2);
        Assert.Null(client.PerformancePreviews.Last().Preset);
        await Task.Delay(200);
        Assert.Empty(client.PerformanceSelections);

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
            new ServiceReactiveLightingLoopOptions(ShortcutNotifications: notifications));
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Static));
        await WaitUntilAsync(() => factory.CreateCount == 1);
        client.Publish(Status(LightingRendererOwnershipState.Reactive) with { Revision = 2 });
        client.Reconnect();
        await WaitUntilAsync(() => client.DeliveryCount == 3);

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
            new ServiceReactiveLightingLoopOptions(ErrorHandler: failures.Enqueue));
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

        ServiceStatusSnapshot reactive = Status(LightingRendererOwnershipState.Reactive) with
        {
            KeyboardDriverModeActive = true,
        };
        client.Publish(reactive);
        await WaitUntilAsync(() => client.DeliveryCount == 1);
        Assert.True(factory.Last!.ReadDriverModeObservation().AllowsFunctionRowInterception);

        client.Publish(reactive with
        {
            Revision = 2,
            DeviceVerified = false,
            WritesBlocked = true,
            PotentialControllers = ["RazerAppEngine"],
            ObservationReason = DeviceObservationReason.CheckingConnection,
            Message = "Device observation is pending.",
            LightingRendererOwnership = LightingRendererOwnershipState.None,
        });
        await WaitUntilAsync(() => client.DeliveryCount == 2);

        Assert.False(factory.Last!.ReadDriverModeObservation().AllowsFunctionRowInterception);
        Assert.Equal(RazerBladeInputDeviceKind.Unsupported, factory.Last.ReadVerifiedInputDeviceKind());
        Assert.False(factory.Last.IsSpecialKeyInputAdmitted(SpecialKeySlot.M3));
        factory.Last.Publish(new ReactiveKeyPulse(53));
        await WaitUntilAsync(() => client.Pulses.Count == 1);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task StatusReconnectMarksDriverModeUnknownWithoutForgettingConfirmation()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            KeyboardDriverModeActive = true,
        });
        await WaitUntilAsync(
            () => factory.Last?.ReadDriverModeObservation().Knowledge
                == KeyboardDriverModeKnowledge.Active);
        long confirmedAt =
            factory.Last!.ReadDriverModeObservation().LastConfirmedTimestamp;

        client.Reconnect();

        await WaitUntilAsync(
            () => factory.Last.ReadDriverModeObservation().Knowledge
                == KeyboardDriverModeKnowledge.Unknown);
        KeyboardDriverModeObservation unknown =
            factory.Last.ReadDriverModeObservation();
        Assert.False(unknown.AllowsFunctionRowInterception);
        Assert.Equal(confirmedAt, unknown.LastConfirmedTimestamp);
        Assert.True(factory.Last.FunctionPResetCount > 0);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Theory]
    [InlineData(SessionControlAccess.InactiveSession)]
    [InlineData(SessionControlAccess.NotInteractive)]
    public async Task ControlAccessRoundTripResetsFunctionPWithoutInput(
        SessionControlAccess inactiveAccess)
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);
        ServiceStatusSnapshot active = Status(LightingRendererOwnershipState.Static) with
        {
            KeyboardDriverModeActive = true,
            ControlAccess = SessionControlAccess.Active,
        };
        client.Publish(active);
        await WaitUntilAsync(() => factory.Last?.IsDriverModeActive() == true);
        RecordingSource source = factory.Last!;
        int before = Volatile.Read(ref source.FunctionPResetCount);

        client.Publish(active with { ControlAccess = inactiveAccess });
        await WaitUntilAsync(() => Volatile.Read(ref source.FunctionPResetCount) > before);
        int inactive = Volatile.Read(ref source.FunctionPResetCount);
        Assert.True(source.IsDriverModeActive());
        client.Publish(active);
        await WaitUntilAsync(() => Volatile.Read(ref source.FunctionPResetCount) > inactive);
        Assert.Same(source, factory.Last);
        Assert.False(source.Disposed);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Theory]
    [InlineData(KeyboardDriverModeKnowledge.Unknown, false)]
    [InlineData(KeyboardDriverModeKnowledge.Inactive, false)]
    [InlineData(KeyboardDriverModeKnowledge.Active, true)]
    public void DriverModeObservation_InterceptsOnlyWhenKnownActive(
        KeyboardDriverModeKnowledge knowledge,
        bool expected)
    {
        var observation = new KeyboardDriverModeObservation(knowledge, 123);

        Assert.Equal(expected, observation.AllowsFunctionRowInterception);
    }

    [Theory]
    [InlineData(DeviceObservationReason.CheckingConnection)]
    [InlineData(DeviceObservationReason.RefreshingControls)]
    public async Task TemporaryAccessRefresh_PreservesPreviouslyActiveInputWhenDesiredStateIsStale(
        DeviceObservationReason reason)
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
            ObservationReason = reason,
            Message = "Device observation is pending.",
        });
        await WaitUntilAsync(() => client.DeliveryCount == 2);

        factory.Last!.Publish(new ReactiveKeyPulse(53));
        await WaitUntilAsync(() => client.Pulses.Count == 1);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Theory]
    [InlineData(DeviceObservationReason.None, LightingRendererOwnershipState.None,
        "Checking the laptop connection.")]
    [InlineData(DeviceObservationReason.None, LightingRendererOwnershipState.None,
        "Refreshing laptop controls after a temporary device-access failure.")]
    [InlineData(DeviceObservationReason.None, LightingRendererOwnershipState.None,
        "No supported laptop connected.")]
    [InlineData(DeviceObservationReason.CheckingConnection, LightingRendererOwnershipState.Faulted,
        "Device observation is pending.")]
    public async Task UnavailableStatusWithoutTemporaryReasonOrWithFault_ClearsReactiveInputOwnership(
        DeviceObservationReason reason,
        LightingRendererOwnershipState ownership,
        string message)
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Reactive) with
        {
            KeyboardDriverModeActive = true,
        });
        await WaitUntilAsync(() => factory.CreateCount == 1);
        client.Publish(Status(ownership) with
        {
            Revision = 2,
            DeviceVerified = false,
            WritesBlocked = true,
            ObservationReason = reason,
            Message = message,
        });
        await WaitUntilAsync(() => client.DeliveryCount == 2);

        Assert.False(factory.Last!.ReadDriverModeObservation().AllowsFunctionRowInterception);
        factory.Last.Publish(new ReactiveKeyPulse(53));
        await Task.Delay(50);
        Assert.Empty(client.Pulses);

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
        await WaitUntilAsync(() => client.DeliveryCount == 2);

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
            new ServiceReactiveLightingLoopOptions(ErrorHandler: failures.Enqueue));
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
            new ServiceReactiveLightingLoopOptions(
                failures.Enqueue,
                new ThrowingNotificationSink(),
                PerformanceShortcutQuietPeriod: TimeSpan.FromMilliseconds(25)));
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
            new ServiceReactiveLightingLoopOptions(
                ErrorHandler: failures.Add,
                PerformanceShortcutQuietPeriod: TimeSpan.FromMilliseconds(25)));
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingRendererOwnershipState.Reactive));
        await WaitUntilAsync(() => factory.CreateCount == 1);
        RecordingSource first = factory.Last!;

        client.Publish(Status(LightingRendererOwnershipState.Reactive) with { Revision = 2 });
        await WaitUntilAsync(() => client.DeliveryCount == 2);
        Assert.Equal(1, factory.CreateCount);

        first.Publish(new ReactiveKeyPulse(77));
        await WaitUntilAsync(() => client.Pulses.Count == 1);
        Assert.True(client.Pulses.TryPeek(out ReactiveKeyPulse pulse));
        Assert.Equal(77, pulse.MatrixSlot);

        client.Publish(Status(LightingRendererOwnershipState.Static) with { Revision = 3 });
        await WaitUntilAsync(() => client.DeliveryCount == 3);
        Assert.False(first.Disposed);
        first.Publish(new ReactiveKeyPulse(78));
        await Task.Delay(25);
        Assert.Single(client.Pulses);

        first.PublishPerformanceShortcut(PerformanceShortcutSource.DedicatedButton);
        await WaitUntilAsync(() => client.PerformanceSelections.Count == 1);

        client.Publish(Status(LightingRendererOwnershipState.Reactive) with { Revision = 4 });
        await WaitUntilAsync(() => client.DeliveryCount == 4);
        Assert.Equal(1, factory.CreateCount);

        client.Publish(Status(LightingRendererOwnershipState.Ripple) with { Revision = 5 });
        await WaitUntilAsync(() => client.DeliveryCount == 5);
        first.Publish(new ReactiveKeyPulse(79));
        await WaitUntilAsync(() => client.Pulses.Count == 2);
        Assert.Equal(1, factory.CreateCount);

        client.Reconnect();
        await WaitUntilAsync(() => client.DeliveryCount == 6);
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
            new ServiceReactiveLightingLoopOptions(
                ShouldOverrideSpecialKey: slot => slot == SpecialKeySlot.M3,
                ExecuteSpecialKeyAsync: (slot, _) =>
                {
                    delivered.Enqueue(slot);
                    return Task.CompletedTask;
                }));
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
            new ServiceReactiveLightingLoopOptions(
                ShouldOverrideSpecialKey: _ => true,
                ExecuteSpecialKeyAsync: (slot, _) =>
                {
                    configured.Enqueue(slot);
                    return Task.CompletedTask;
                },
                ExecuteDefaultLaptopKeyAsync: (slot, _) =>
                {
                    defaults.Enqueue(slot);
                    return Task.CompletedTask;
                }));
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
        var delivered = new ConcurrentQueue<FunctionRowInputEvent>();
        await using var loop = new ServiceReactiveLightingLoop(
            client,
            factory,
            new ServiceReactiveLightingLoopOptions(
                ExecuteFunctionRowInputAsync: (inputEvent, _) =>
                {
                    delivered.Enqueue(inputEvent);
                    return Task.CompletedTask;
                }));
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

        var expected = new FunctionRowInputEvent(
            0x3B,
            FunctionRowAction.VolumeMute,
            FunctionRowInputPhase.Down);
        factory.Last.PublishFunctionRowInput(expected);
        await WaitUntilAsync(() => delivered.Count == 1);
        Assert.Equal(expected, Assert.Single(delivered));

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
    public async Task VerifiedFirmwareModelControlsModelSpecificInputAdmission()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        await WaitUntilAsync(() => factory.CreateCount == 1);
        Assert.Equal(
            RazerBladeInputDeviceKind.Unsupported,
            factory.Last!.ReadVerifiedInputDeviceKind());
        Assert.False(factory.Last.IsSpecialKeyInputAdmitted(SpecialKeySlot.M3));

        DeviceCapabilities observed = DeviceCapabilities.Unavailable() with
        {
            SpecialKeyBindingsAccess = DeviceFeatureAccess.ObservedOnly,
        };
        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            Capabilities = observed,
            Firmware = new FirmwareVersions(
                "2.02",
                "1.09",
                "1.9.0.0",
                "Unavailable",
                RazerBladeModelCatalog.Rz090528ModelNumber),
        });
        await WaitUntilAsync(
            () => factory.Last.ReadVerifiedInputDeviceKind()
                    == RazerBladeInputDeviceKind.Rz09_0528);
        Assert.False(factory.Last.IsSpecialKeyInputAdmitted(SpecialKeySlot.M3));

        DeviceCapabilities admitted = DeviceCapabilities.Unavailable() with
        {
            SpecialKeyBindingsAccess = DeviceFeatureAccess.ReadWrite,
            WritableSpecialKeySlots =
                SpecialKeySlotSet.M3
                | SpecialKeySlotSet.M4
                | SpecialKeySlotSet.M5,
        };
        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            Revision = 2,
            Capabilities = admitted,
            Firmware = new FirmwareVersions(
                "2.02",
                "1.09",
                "1.9.0.0",
                "Unavailable",
                RazerBladeModelCatalog.Rz090528ModelNumber),
        });
        await WaitUntilAsync(
            () => factory.Last.ReadVerifiedInputDeviceKind()
                    == RazerBladeInputDeviceKind.Rz09_0528
                && factory.Last.IsSpecialKeyInputAdmitted(SpecialKeySlot.M3)
                && !factory.Last.IsSpecialKeyInputAdmitted(SpecialKeySlot.M1));

        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            Revision = 3,
            DeviceVerified = false,
            Firmware = new FirmwareVersions(
                "3.01",
                "1.04",
                "Unavailable",
                "Unavailable",
                RazerBladeModelCatalog.OpenBladeTargetModelNumber),
        });
        await WaitUntilAsync(
            () => factory.Last.ReadVerifiedInputDeviceKind()
                    == RazerBladeInputDeviceKind.Unsupported
                && !factory.Last.IsSpecialKeyInputAdmitted(SpecialKeySlot.M3));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task UnknownVerifiedReadbackPreservesPreferenceButDisablesInterception()
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
        long confirmedAt =
            factory.Last!.ReadDriverModeObservation().LastConfirmedTimestamp;
        Assert.True(confirmedAt > 0);

        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            Revision = 2,
            FunctionKeyPrimary = null,
            KeyboardDriverModeActive = null,
        });
        await WaitUntilAsync(
            () => factory.Last!.ReadDriverModeObservation().Knowledge
                == KeyboardDriverModeKnowledge.Unknown);

        Assert.Equal(
            FunctionKeyPrimaryMode.MultimediaKeys,
            factory.Last!.ReadFunctionKeyPrimaryMode());
        Assert.False(factory.Last.IsDriverModeActive());
        Assert.Equal(
            confirmedAt,
            factory.Last.ReadDriverModeObservation().LastConfirmedTimestamp);

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

    [Fact]
    public async Task GamingModeKeyPolicy_IsActiveOnlyWhileGamingModeIsOn()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        ServiceStatusSnapshot enabled = Status(
            LightingRendererOwnershipState.Static);
        enabled = enabled with
        {
            GamingModeEnabled = true,
            Profiles = enabled.Profiles with
            {
                PluggedIn = enabled.Profiles.PluggedIn with
                {
                    LaptopBehavior = new LaptopBehaviorProfileSettings(
                        StartupAnimationEnabled: null,
                        FunctionKeyPrimary: null,
                        GamingModeEnabled: true)
                    {
                        GamingModeKeyPolicy = new GamingModeKeyPolicy(
                            BlockWindowsKey: true,
                            BlockAltTab: true),
                    },
                },
            },
        };
        client.Publish(enabled);
        await WaitUntilAsync(
            () => factory.Last?.ReadGamingModeKeyPolicy()
                == new GamingModeKeyPolicy(true, true));

        client.Publish(enabled with
        {
            Revision = 2,
            GamingModeEnabled = false,
        });
        await WaitUntilAsync(
            () => factory.Last?.ReadGamingModeKeyPolicy()
                == GamingModeKeyPolicy.Disabled);

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

    private sealed class RecordingSourceFactory : IReactiveKeyboardSourceFactory
    {
        public int CreateCount { get; private set; }

        public RecordingSource? Last { get; private set; }

        public IReactiveKeyboardSource Create(ReactiveKeyboardSourceOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            CreateCount++;
            return Last = new RecordingSource(options);
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
        private readonly Action<FunctionRowInputEvent> publishFunctionRowInput;
        private readonly Func<FunctionKeyPrimaryMode?> readFunctionKeyPrimaryMode;
        private readonly Func<KeyboardDriverModeObservation> readDriverModeObservation;
        private readonly Func<GamingModeKeyPolicy> readGamingModeKeyPolicy;
        private readonly Func<RazerBladeInputDeviceKind> readVerifiedInputDeviceKind;
        private readonly Func<SpecialKeySlot, bool> isSpecialKeyInputAdmitted;

        public RecordingSource(ReactiveKeyboardSourceOptions options)
        {
            publish = options.PublishPulse;
            publishPerformanceShortcut = options.PublishPerformanceShortcut;
            publishSpecialKey = options.PublishSpecialKey;
            shouldOverrideSpecialKey = options.ShouldOverrideSpecialKey;
            publishDefaultLaptopKey = options.PublishDefaultLaptopKey;
            publishFunctionRowInput = options.PublishFunctionRowInput;
            readFunctionKeyPrimaryMode = options.ReadFunctionKeyPrimaryMode;
            readDriverModeObservation = options.ReadDriverModeObservation;
            readGamingModeKeyPolicy = options.ReadGamingModeKeyPolicy;
            readVerifiedInputDeviceKind = options.ReadVerifiedInputDeviceKind;
            isSpecialKeyInputAdmitted = options.IsSpecialKeyInputAdmitted;
        }

        public bool Disposed { get; private set; }

        public int FunctionPResetCount;

        public void ResetFunctionPInput() => Interlocked.Increment(ref FunctionPResetCount);

        public Task Completion => completion.Task;

        public void Publish(ReactiveKeyPulse pulse) => publish(pulse);

        public void PublishPerformanceShortcut(PerformanceShortcutSource source) =>
            publishPerformanceShortcut(source);

        public void PublishSpecialKey(SpecialKeySlot slot) => publishSpecialKey(slot);

        public void PublishDefaultLaptopKey(SpecialKeySlot slot) =>
            publishDefaultLaptopKey(slot);

        public bool ShouldOverrideSpecialKey(SpecialKeySlot slot) =>
            shouldOverrideSpecialKey(slot);

        public void PublishFunctionRowInput(FunctionRowInputEvent inputEvent) =>
            publishFunctionRowInput(inputEvent);

        public FunctionKeyPrimaryMode? ReadFunctionKeyPrimaryMode() =>
            readFunctionKeyPrimaryMode();

        public KeyboardDriverModeObservation ReadDriverModeObservation() =>
            readDriverModeObservation();

        public bool IsDriverModeActive() =>
            ReadDriverModeObservation().AllowsFunctionRowInterception;

        public GamingModeKeyPolicy ReadGamingModeKeyPolicy() =>
            readGamingModeKeyPolicy();

        public RazerBladeInputDeviceKind ReadVerifiedInputDeviceKind() =>
            readVerifiedInputDeviceKind();

        public bool IsSpecialKeyInputAdmitted(SpecialKeySlot slot) =>
            isSpecialKeyInputAdmitted(slot);

        public void Fail(Exception exception) => completion.TrySetException(exception);

        public void Dispose()
        {
            Disposed = true;
            completion.TrySetResult();
        }
    }

    private sealed class WatchClient : OpenBlade.Testing.UnavailableServiceClient
    {
        private readonly Channel<ServiceWatchUpdate> updates = Channel.CreateUnbounded<ServiceWatchUpdate>();
        private int deliveryCount;

        public ConcurrentQueue<ReactiveKeyPulse> Pulses { get; } = new();

        public ConcurrentQueue<PerformanceShortcutSource> PerformanceShortcuts { get; } = new();

        public ConcurrentQueue<PerformanceOverride> PerformanceSelections { get; } = new();

        public ConcurrentQueue<(Guid SessionId, long Generation, PerformancePreset? Preset)>
            PerformancePreviews
        { get; } = new();

        public int DeliveryCount => Volatile.Read(ref deliveryCount);

        public int ObservationCount { get; private set; }

        public ServiceStatusSnapshot ObservationStatus { get; init; } =
            Status(LightingRendererOwnershipState.Static);

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

        public override async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await foreach (ServiceWatchUpdate update in updates.Reader.ReadAllAsync(token))
            {
                yield return update;
                Interlocked.Increment(ref deliveryCount);
            }
        }

        public override Task<ControlOperationResult> PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken token)
        {
            Pulses.Enqueue(pulse);
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Applied"));
        }

        public override Task<ServiceStatusSnapshot> ObserveStateAsync(
            bool includeLiveReadings,
            CancellationToken cancellationToken)
        {
            ObservationCount++;
            return Task.FromResult(ObservationStatus);
        }

        public override Task<ControlOperationResult> SetPerformanceOverrideAsync(
            ManualOverride<PerformancePreset>? value,
            CancellationToken token) => SetPerformanceConfigurationOverrideAsync(
                value is null
                    ? null
                    : new PerformanceOverride(value.Value, value.Lifetime),
                token);

        public override Task<ControlOperationResult> SetPerformanceConfigurationOverrideAsync(
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

        public override Task<ControlOperationResult> CyclePerformanceShortcutAsync(
            PerformanceShortcutSource source,
            CancellationToken token)
        {
            PerformanceShortcuts.Enqueue(source);
            return PerformanceShortcutCompletion?.Task
                ?? Task.FromResult(PerformanceShortcutResult);
        }

        public override Task<ControlOperationResult> SetPerformanceShortcutPreviewAsync(
            Guid sessionId,
            long generation,
            PerformancePreset? preset,
            CancellationToken token)
        {
            PerformancePreviews.Enqueue((sessionId, generation, preset));
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Stored,
                "Stored"));
        }

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
