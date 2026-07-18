using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Session.Tests;

public sealed class DisplayAutomationCoordinatorTests
{
    [Fact]
    public async Task FirstConfirmedStatus_IsObservationOnly()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier();
        await using var coordinator = new DisplayAutomationCoordinator(context, applier);

        DisplayAutomationObservation result = coordinator.Observe(Status(
            PowerSource.PluggedIn,
            Profiles("plugged")));
        await coordinator.WhenIdleAsync();

        Assert.Equal(DisplayAutomationObservation.BaselineEstablished, result);
        Assert.Empty(applier.Transitions);
        Assert.Equal(PowerSource.PluggedIn, coordinator.LastConfirmedPowerSource);
    }

    [Fact]
    public async Task ExternalUnknown_IsIgnoredUntilFirstConfirmedBaseline()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier();
        await using var coordinator = new DisplayAutomationCoordinator(context, applier);

        Assert.Equal(
            DisplayAutomationObservation.IgnoredUnconfirmed,
            coordinator.Observe(Status(PowerSource.ExternalUnknown, Profiles("unknown"))));
        Assert.Equal(
            DisplayAutomationObservation.BaselineEstablished,
            coordinator.Observe(Status(PowerSource.Battery, Profiles("battery"))));
        await coordinator.WhenIdleAsync();

        Assert.Empty(applier.Transitions);
        Assert.Equal(PowerSource.Battery, coordinator.LastConfirmedPowerSource);
    }

    [Fact]
    public async Task ConfirmedTransition_UsesDestinationProfileFromSameSnapshot()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier();
        await using var coordinator = new DisplayAutomationCoordinator(context, applier);
        coordinator.Observe(Status(PowerSource.PluggedIn, Profiles("initial"), revision: 1));
        ProfileSet destination = Profiles("destination");

        DisplayAutomationObservation result = coordinator.Observe(
            Status(PowerSource.Battery, destination, revision: 2));
        await coordinator.WhenIdleAsync();

        Assert.Equal(DisplayAutomationObservation.TransitionScheduled, result);
        FakeApplier.Transition transition = Assert.Single(applier.Transitions);
        Assert.Equal(PowerSource.Battery, transition.Source);
        Assert.Equal(destination.Battery.Display, transition.Settings);
        Assert.Equal("destination-battery", transition.Settings.RefreshRateKey);
        Assert.Equal(PowerSource.Battery, context.Capture().PowerSource);
        Assert.Equal(SessionDisplayOperationStatus.Succeeded,
            coordinator.LastTransitionResult?.Result.Status);
    }

    [Fact]
    public async Task SameSourceProfileSave_DoesNotApply()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier();
        await using var coordinator = new DisplayAutomationCoordinator(context, applier);
        coordinator.Observe(Status(PowerSource.UsbC, Profiles("initial"), revision: 1));

        DisplayAutomationObservation result = coordinator.Observe(
            Status(PowerSource.UsbC, Profiles("saved"), revision: 2));
        await coordinator.WhenIdleAsync();

        Assert.Equal(DisplayAutomationObservation.SameSource, result);
        Assert.Empty(applier.Transitions);
    }

    [Fact]
    public async Task TransientUnknownRecoveryToSameSource_DoesNotApply()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier();
        await using var coordinator = new DisplayAutomationCoordinator(context, applier);
        coordinator.Observe(Status(PowerSource.PluggedIn, Profiles("initial"), revision: 1));
        coordinator.Observe(Status(PowerSource.ExternalUnknown, Profiles("unknown"), revision: 2));

        DisplayAutomationObservation result = coordinator.Observe(
            Status(PowerSource.PluggedIn, Profiles("recovered"), revision: 3));
        await coordinator.WhenIdleAsync();

        Assert.Equal(DisplayAutomationObservation.SameSource, result);
        Assert.Empty(applier.Transitions);
        Assert.Equal(PowerSource.PluggedIn, context.Capture().PowerSource);
    }

    [Fact]
    public async Task UnknownInterruptingNewDestination_ReschedulesDestinationOnRecovery()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier { BlockFirstTransition = true };
        await using var coordinator = new DisplayAutomationCoordinator(context, applier);
        coordinator.Observe(Status(PowerSource.PluggedIn, Profiles("initial"), revision: 1));
        coordinator.Observe(Status(PowerSource.Battery, Profiles("first-b"), revision: 2));
        await applier.FirstTransitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.Observe(Status(PowerSource.ExternalUnknown, Profiles("unknown"), revision: 3));
        DisplayAutomationObservation recovered = coordinator.Observe(
            Status(PowerSource.Battery, Profiles("recovered-b"), revision: 4));
        await coordinator.WhenIdleAsync();

        Assert.Equal(DisplayAutomationObservation.TransitionScheduled, recovered);
        Assert.Equal(2, applier.Transitions.Count);
        Assert.Equal("first-b-battery", applier.Transitions[0].Settings.RefreshRateKey);
        Assert.Equal("recovered-b-battery", applier.Transitions[1].Settings.RefreshRateKey);
        Assert.Equal(
            applier.Transitions[1].Generation,
            coordinator.LastTransitionResult?.Generation);
        Assert.Equal(PowerSource.Battery, context.Capture().PowerSource);
    }

    [Fact]
    public async Task SubscriptionUnavailable_SuspendsAndReschedulesIncompleteDestination()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier { BlockFirstTransition = true };
        await using var coordinator = new DisplayAutomationCoordinator(context, applier);
        coordinator.Observe(Status(PowerSource.PluggedIn, Profiles("initial"), revision: 1));
        coordinator.Observe(Status(PowerSource.Battery, Profiles("first-b"), revision: 2));
        await applier.FirstTransitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            DisplayAutomationObservation.IgnoredUnconfirmed,
            coordinator.MarkUnavailable());
        Assert.Equal(PowerSource.ExternalUnknown, context.Capture().PowerSource);
        DisplayAutomationObservation recovered = coordinator.Observe(
            Status(PowerSource.Battery, Profiles("reconnected-b"), revision: 1));
        await coordinator.WhenIdleAsync();

        Assert.Equal(DisplayAutomationObservation.TransitionScheduled, recovered);
        Assert.Equal(2, applier.Transitions.Count);
        Assert.True(applier.FirstTransitionCanceled);
        Assert.Equal(
            "reconnected-b-battery",
            applier.Transitions[1].Settings.RefreshRateKey);
    }

    [Fact]
    public async Task RapidTransitions_CancelOlderAndOnlyPublishLatestResult()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier { BlockFirstTransition = true };
        await using var coordinator = new DisplayAutomationCoordinator(context, applier);
        coordinator.Observe(Status(PowerSource.PluggedIn, Profiles("initial"), revision: 1));
        coordinator.Observe(Status(PowerSource.Battery, Profiles("battery"), revision: 2));
        await applier.FirstTransitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.Observe(Status(PowerSource.UsbC, Profiles("usb"), revision: 3));
        await coordinator.WhenIdleAsync();

        Assert.Equal(2, applier.Transitions.Count);
        Assert.True(applier.FirstTransitionCanceled);
        Assert.Equal(PowerSource.UsbC, coordinator.LastTransitionResult?.PowerSource);
        Assert.Equal("usb-usbc", applier.Transitions[1].Settings.RefreshRateKey);
    }

    [Fact]
    public async Task DifferentSourceOnReconnect_IsStillARealTransition()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier();
        await using var coordinator = new DisplayAutomationCoordinator(context, applier);
        coordinator.Observe(Status(PowerSource.PluggedIn, Profiles("before"), revision: 20));

        // A fresh subscription may restart revisions, but the coordinator keeps
        // its confirmed source baseline for its process lifetime.
        coordinator.Observe(Status(PowerSource.Battery, Profiles("after"), revision: 1));
        await coordinator.WhenIdleAsync();

        Assert.Single(applier.Transitions);
        Assert.Equal(PowerSource.Battery, applier.Transitions[0].Source);
    }

    private static ProfileSet Profiles(string prefix)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        return defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Display = new DisplayProfileSettings($"{prefix}-plugged", false, "native"),
            },
            Battery = defaults.Battery with
            {
                Display = new DisplayProfileSettings($"{prefix}-battery", false, "native"),
            },
            UsbC = defaults.UsbC with
            {
                Display = new DisplayProfileSettings($"{prefix}-usbc", false, "native"),
            },
        };
    }

    private static ServiceStatusSnapshot Status(
        PowerSource source,
        ProfileSet profiles,
        long revision = 1) => new(
        Revision: revision,
        DeviceVerified: true,
        WritesBlocked: false,
        PotentialControllers: [],
        ControllerDetectionWarning: null,
        ExternalChanges: [],
        Message: "test",
        Capabilities: null,
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
        ActivePowerSource: source,
        LiveReadings: null,
        PerformanceOverride: null,
        FanOverride: null,
        LightingOverride: null,
        FullChargeOverride: false,
        Firmware: null,
        ServiceVersion: "test");

    private sealed class FakeApplier : IDisplayProfileApplier
    {
        private int transitionCount;

        public sealed record Transition(
            PowerSource Source,
            long Generation,
            DisplayProfileSettings Settings);

        public List<Transition> Transitions { get; } = [];
        public bool BlockFirstTransition { get; init; }
        public bool FirstTransitionCanceled { get; private set; }
        public TaskCompletionSource FirstTransitionStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SessionDisplayExecutionResult> QueryAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        public Task<SessionDisplayExecutionResult> SetRefreshRateAsync(
            PowerSource expectedPowerSource,
            string refreshRateKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        public Task<SessionDisplayExecutionResult> SetHdrAsync(
            PowerSource expectedPowerSource,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        public Task<SessionDisplayExecutionResult> SetColorProfileAsync(
            PowerSource expectedPowerSource,
            string colorProfileKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        public Task<SessionDisplayExecutionResult> ApplyProfileAsync(
            PowerSource expectedPowerSource,
            DisplayProfileSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        public async Task<SessionDisplayExecutionResult> ApplyProfileForTransitionAsync(
            PowerSource expectedPowerSource,
            long expectedGeneration,
            DisplayProfileSettings settings,
            CancellationToken cancellationToken)
        {
            int ordinal = Interlocked.Increment(ref transitionCount);
            Transitions.Add(new Transition(expectedPowerSource, expectedGeneration, settings));
            if (BlockFirstTransition && ordinal == 1)
            {
                FirstTransitionStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstTransitionCanceled = true;
                    throw;
                }
            }
            return Success();
        }

        private static SessionDisplayExecutionResult Success() => new(
            SessionDisplayOperationStatus.Succeeded,
            null,
            SessionDisplayIpcCodecTests.Snapshot("fixed-60", false, "native"));
    }
}
