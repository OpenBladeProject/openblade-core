using OpenBlade.Contracts;
using OpenBlade.Display;
using OpenBlade.Session;

namespace OpenBlade.Session.Tests;

public sealed class DisplayAutomationCoordinatorTests
{
    [Fact]
    public async Task ApplicationEntryAndExitApplyOverrideThenRestoreCapturedBaseline()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier();
        await using var coordinator = new DisplayAutomationCoordinator(context, applier);
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        Guid id = Guid.NewGuid();
        ProfileSet profiles = defaults with
        {
            ApplicationProfiles =
            [
                new ApplicationProfileDefinition(
                    id,
                    "Game",
                    @"C:\Games\game.exe",
                    ApplicationActivationMode.Foreground,
                    new ApplicationProfileOverrideSet
                    {
                        PluggedIn = new ApplicationPowerProfileOverride
                        {
                            Display = new DisplayProfileSettings(null, true, null),
                        },
                    }),
            ],
        };
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles));

        Assert.Equal(DisplayAutomationObservation.TransitionScheduled,
            coordinator.Observe(Status(PowerSource.PluggedIn, profiles) with
            {
                ActiveApplicationProfileId = id,
            }));
        await coordinator.WhenIdleAsync();
        Assert.True(Assert.Single(applier.Transitions).Settings.HdrEnabled);

        Assert.Equal(DisplayAutomationObservation.TransitionScheduled,
            coordinator.Observe(Status(PowerSource.PluggedIn, profiles)));
        await coordinator.WhenIdleAsync();

        Assert.Equal(2, applier.Transitions.Count);
        Assert.False(applier.Transitions[1].Settings.HdrEnabled);
    }

    [Fact]
    public async Task IndeterminateTransitionBlocksTargetAndPowerChanges()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier();
        applier.Results.Enqueue(Indeterminate());
        DisplayAutomationRecoveryState? checkpoint = null;
        await using var coordinator = new DisplayAutomationCoordinator(
            context,
            applier,
            recordRecoveryState: state =>
            {
                checkpoint = state;
                return true;
            });
        (ProfileSet profiles, Guid id) = ApplicationProfiles(hdr: true);
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles));

        coordinator.Observe(Status(PowerSource.PluggedIn, profiles) with
        {
            ActiveApplicationProfileId = id,
        });
        await coordinator.WhenIdleAsync();

        Assert.Equal(SessionDisplayOperationStatus.Indeterminate,
            coordinator.LastTransitionResult?.Result.Status);
        Assert.True(checkpoint?.AutomaticWritesPaused);
        Assert.Equal(
            DisplayAutomationObservation.SameSource,
            coordinator.Observe(Status(PowerSource.PluggedIn, profiles)));
        Assert.Equal(
            DisplayAutomationObservation.SameSource,
            coordinator.Observe(Status(PowerSource.Battery, profiles)));
        await coordinator.WhenIdleAsync();
        Assert.Single(applier.Transitions);
        Assert.Equal(PowerSource.Battery, coordinator.LastConfirmedPowerSource);
    }

    [Fact]
    public async Task FailedWriteAheadCheckpointBlocksAutomaticMutation()
    {
        int saves = 0;
        var applier = new FakeApplier();
        (ProfileSet profiles, Guid id) = ApplicationProfiles(hdr: true);
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            applier,
            recordRecoveryState: _ => Interlocked.Increment(ref saves) == 1);
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles));

        coordinator.Observe(Status(PowerSource.PluggedIn, profiles) with
        {
            ActiveApplicationProfileId = id,
        });
        await coordinator.WhenIdleAsync();

        Assert.Empty(applier.Transitions);
        Assert.Equal(SessionDisplayOperationStatus.Blocked,
            coordinator.LastTransitionResult?.Result.Status);
    }

    [Fact]
    public async Task FailedCompletionCheckpointKeepsAutomationPaused()
    {
        int saves = 0;
        var applier = new FakeApplier();
        (ProfileSet profiles, Guid id) = ApplicationProfiles(hdr: true);
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            applier,
            recordRecoveryState: _ => Interlocked.Increment(ref saves) != 3);
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles));
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles) with
        {
            ActiveApplicationProfileId = id,
        });
        await coordinator.WhenIdleAsync();

        Assert.Single(applier.Transitions);
        Assert.Equal(SessionDisplayOperationStatus.Blocked,
            coordinator.LastTransitionResult?.Result.Status);
        Assert.Equal(
            DisplayAutomationObservation.SameSource,
            coordinator.Observe(Status(PowerSource.PluggedIn, profiles)));
        Assert.Single(applier.Transitions);
    }

    [Fact]
    public async Task WriteAheadCheckpointRemainsPausedAcrossCrashDuringMutation()
    {
        DisplayAutomationRecoveryState? checkpoint = null;
        var applier = new FakeApplier { BlockFirstTransition = true };
        (ProfileSet profiles, Guid id) = ApplicationProfiles(hdr: true);
        var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            applier,
            recordRecoveryState: state =>
            {
                checkpoint = state;
                return true;
            });
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles));
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles) with
        {
            ActiveApplicationProfileId = id,
        });
        await applier.FirstTransitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(checkpoint?.AutomaticWritesPaused);
        Assert.True(checkpoint?.UncertainHdr);
        await coordinator.DisposeAsync();

        var restartedApplier = new FakeApplier();
        await using var restarted = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            restartedApplier,
            recoveryState: checkpoint);
        restarted.Observe(Status(PowerSource.PluggedIn, profiles) with
        {
            ActiveApplicationProfileId = id,
        });
        await restarted.WhenIdleAsync();
        Assert.Empty(restartedApplier.Transitions);
    }

    [Fact]
    public async Task PartialReadbackDoesNotClearAffectedBrightnessAmbiguity()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        var recovery = new DisplayAutomationRecoveryState(
            PowerSource.PluggedIn,
            null,
            null,
            OwnedRefreshRate: false,
            OwnedHdr: false,
            OwnedColorProfile: false,
            OwnedBrightness: false,
            ManualHold: false,
            AutomaticWritesPaused: true,
            UncertainRefreshRate: false,
            UncertainHdr: false,
            UncertainColorProfile: false,
            UncertainBrightness: true);
        var applier = new FakeApplier();
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            applier,
            recoveryState: recovery);
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles));
        DisplaySettingsSnapshot partial = applier.QueryResult.State with
        {
            BrightnessPercent = null,
            SupportedBrightnessLevels = [],
        };

        Assert.False(coordinator.ResumeAfterSuccessfulExplicitRead(partial));
        Assert.Empty(applier.Transitions);

        DisplaySettingsSnapshot complete = partial with
        {
            BrightnessPercent = 40,
            SupportedBrightnessLevels = [20, 40, 60],
        };
        Assert.True(coordinator.ResumeAfterSuccessfulExplicitRead(complete));
        await coordinator.WhenIdleAsync();
        Assert.Single(applier.Transitions);
    }

    [Fact]
    public async Task BrightnessManualReadDoesNotClearPriorHdrAmbiguity()
    {
        DisplayAutomationRecoveryState? checkpoint = null;
        var recovery = new DisplayAutomationRecoveryState(
            PowerSource.PluggedIn,
            ApplicationProfileId: null,
            Baseline: null,
            OwnedRefreshRate: false,
            OwnedHdr: false,
            OwnedColorProfile: false,
            OwnedBrightness: false,
            ManualHold: true,
            AutomaticWritesPaused: true,
            UncertainRefreshRate: false,
            UncertainHdr: true,
            UncertainColorProfile: false,
            UncertainBrightness: false);
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            new FakeApplier(),
            recoveryState: recovery,
            recordRecoveryState: state =>
            {
                checkpoint = state;
                return true;
            });

        Assert.True(coordinator.BeginManualOperation(
            DisplayProfileSettings.Unconfigured() with { BrightnessPercent = 50 }));
        DisplaySettingsSnapshot brightnessKnownButHdrUnavailable =
            SessionDisplayIpcCodecTests.Snapshot("fixed-60", false, "native") with
            {
                HdrSupported = false,
                BrightnessPercent = 50,
                SupportedBrightnessLevels = [0, 50, 100],
            };
        coordinator.CompleteManualOperation(
            SessionDisplayOperationStatus.Succeeded,
            brightnessKnownButHdrUnavailable);

        Assert.NotNull(checkpoint);
        Assert.True(checkpoint.AutomaticWritesPaused);
        Assert.True(checkpoint.UncertainHdr);
        Assert.True(checkpoint.UncertainBrightness);
    }

    [Fact]
    public async Task FailedManualAdmissionCanRecoverThroughSuccessfulRead()
    {
        bool allowCheckpoint = true;
        DisplayAutomationRecoveryState? checkpoint = null;
        var applier = new FakeApplier();
        (ProfileSet profiles, Guid id) = ApplicationProfiles(hdr: true);
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            applier,
            recordRecoveryState: state =>
            {
                checkpoint = state;
                return allowCheckpoint;
            });
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles));

        allowCheckpoint = false;
        Assert.False(coordinator.BeginManualOperation(
            DisplayProfileSettings.Unconfigured() with { BrightnessPercent = 50 }));
        Assert.Equal(
            DisplayAutomationObservation.SameSource,
            coordinator.Observe(Status(PowerSource.PluggedIn, profiles) with
            {
                ActiveApplicationProfileId = id,
            }));

        allowCheckpoint = true;
        Assert.True(coordinator.ResumeAfterSuccessfulExplicitRead(applier.QueryResult.State));
        await coordinator.WhenIdleAsync();

        Assert.Single(applier.Transitions);
        Assert.NotNull(checkpoint);
        Assert.False(checkpoint.AutomaticWritesPaused);
        Assert.True(applier.Transitions[0].Settings.HdrEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualAdmissionPreservesAutomaticUncertaintyAfterLateTransition(
        bool returnsLateSuccess)
    {
        DisplayAutomationRecoveryState? checkpoint = null;
        var applier = new FakeApplier
        {
            BlockFirstTransition = !returnsLateSuccess,
            ReturnLateSuccess = returnsLateSuccess,
        };
        (ProfileSet profiles, Guid id) = ApplicationProfiles(hdr: true);
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            applier,
            recordRecoveryState: state =>
            {
                checkpoint = state;
                return true;
            });
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles));
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles) with
        {
            ActiveApplicationProfileId = id,
        });
        await applier.FirstTransitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(coordinator.BeginManualOperation(
            DisplayProfileSettings.Unconfigured() with { BrightnessPercent = 50 }));
        applier.ReleaseFirstTransition.TrySetResult();
        await coordinator.WhenIdleAsync();

        Assert.NotNull(checkpoint);
        Assert.True(checkpoint.AutomaticWritesPaused);
        Assert.True(checkpoint.UncertainHdr);
        Assert.True(checkpoint.UncertainBrightness);

        DisplaySettingsSnapshot brightnessKnownButHdrUnavailable =
            applier.QueryResult.State with
            {
                HdrSupported = false,
                BrightnessPercent = 50,
                SupportedBrightnessLevels = [0, 50, 100],
            };
        coordinator.CompleteManualOperation(
            SessionDisplayOperationStatus.Succeeded,
            brightnessKnownButHdrUnavailable);

        Assert.True(checkpoint.AutomaticWritesPaused);
        Assert.True(checkpoint.UncertainHdr);
        Assert.True(checkpoint.UncertainBrightness);
    }

    [Fact]
    public async Task InvalidRecoveryCheckpointStartsPausedAndRequiresCompleteReadback()
    {
        var applier = new FakeApplier();
        (ProfileSet profiles, Guid id) = ApplicationProfiles(hdr: true);
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            applier,
            recordRecoveryState: _ => true,
            recoveryStateInvalid: true);

        coordinator.Observe(Status(PowerSource.PluggedIn, profiles) with
        {
            ActiveApplicationProfileId = id,
        });
        await coordinator.WhenIdleAsync();

        Assert.Empty(applier.Transitions);
        Assert.False(coordinator.ResumeAfterSuccessfulExplicitRead(
            applier.QueryResult.State));
    }

    [Fact]
    public async Task RestartFromIndeterminateCheckpointBlocksAutomaticReplay()
    {
        (ProfileSet profiles, Guid id) = ApplicationProfiles(hdr: true);
        var recovery = new DisplayAutomationRecoveryState(
            PowerSource.PluggedIn,
            id,
            new DisplayProfileSettings("fixed-60", false, "native"),
            OwnedRefreshRate: false,
            OwnedHdr: true,
            OwnedColorProfile: false,
            OwnedBrightness: false,
            ManualHold: false,
            AutomaticWritesPaused: true,
            UncertainRefreshRate: false,
            UncertainHdr: true,
            UncertainColorProfile: false,
            UncertainBrightness: false);
        var applier = new FakeApplier();
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            applier,
            recoveryState: recovery);

        Assert.Equal(
            DisplayAutomationObservation.SameSource,
            coordinator.Observe(Status(PowerSource.PluggedIn, profiles) with
            {
                ActiveApplicationProfileId = id,
            }));
        await coordinator.WhenIdleAsync();

        Assert.Empty(applier.Transitions);
    }

    [Fact]
    public async Task TargetChangeDuringWriteWaitsForLateIndeterminateResult()
    {
        var applier = new FakeApplier { ReturnLateIndeterminate = true };
        (ProfileSet profiles, Guid id) = ApplicationProfiles(hdr: true);
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            applier);
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles));
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles) with
        {
            ActiveApplicationProfileId = id,
        });
        await applier.FirstTransitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.Observe(Status(PowerSource.PluggedIn, profiles));
        applier.ReleaseFirstTransition.TrySetResult();
        await coordinator.WhenIdleAsync();

        Assert.Equal(SessionDisplayOperationStatus.Indeterminate,
            coordinator.LastTransitionResult?.Result.Status);
        Assert.Equal(
            DisplayAutomationObservation.SameSource,
            coordinator.Observe(Status(PowerSource.Battery, profiles)));
        Assert.Single(applier.Transitions);
    }

    [Fact]
    public async Task SuccessfulExplicitReadReconcilesLatestTargetAfterAmbiguity()
    {
        var applier = new FakeApplier();
        applier.Results.Enqueue(Indeterminate());
        (ProfileSet profiles, Guid id) = ApplicationProfiles(hdr: true);
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            applier);
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles));
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles) with
        {
            ActiveApplicationProfileId = id,
        });
        await coordinator.WhenIdleAsync();

        coordinator.ResumeAfterSuccessfulExplicitRead(applier.QueryResult.State);
        await coordinator.WhenIdleAsync();

        Assert.Equal(2, applier.Transitions.Count);
        Assert.True(applier.Transitions[1].Settings.HdrEnabled);
        Assert.Equal(SessionDisplayOperationStatus.Succeeded,
            coordinator.LastTransitionResult?.Result.Status);
    }

    [Fact]
    public async Task ManualChangeDuringApplicationPreventsExitRestore()
    {
        var applier = new FakeApplier();
        (ProfileSet profiles, Guid id) = ApplicationProfiles(hdr: true);
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            applier);
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles));
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles) with
        {
            ActiveApplicationProfileId = id,
        });
        await coordinator.WhenIdleAsync();

        Assert.True(coordinator.BeginManualOperation(
            new DisplayProfileSettings(null, false, null)));
        coordinator.CompleteManualOperation(
            SessionDisplayOperationStatus.Succeeded,
            applier.QueryResult.State);
        Assert.Equal(
            DisplayAutomationObservation.SameSource,
            coordinator.Observe(Status(PowerSource.PluggedIn, profiles)));
        await coordinator.WhenIdleAsync();

        Assert.Single(applier.Transitions);
    }

    [Fact]
    public async Task ManualMutationAdmissionDurablyDropsApplicationRestoreOwnership()
    {
        DisplayAutomationRecoveryState? checkpoint = null;
        Guid id = Guid.NewGuid();
        var recovery = new DisplayAutomationRecoveryState(
            PowerSource.PluggedIn,
            id,
            new DisplayProfileSettings("fixed-60", false, "native"),
            OwnedRefreshRate: false,
            OwnedHdr: true,
            OwnedColorProfile: false,
            OwnedBrightness: false,
            ManualHold: false,
            AutomaticWritesPaused: false,
            UncertainRefreshRate: false,
            UncertainHdr: false,
            UncertainColorProfile: false,
            UncertainBrightness: false);
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            new FakeApplier(),
            recoveryState: recovery,
            recordRecoveryState: state =>
            {
                checkpoint = state;
                return true;
            });

        Assert.True(coordinator.BeginManualOperation(
            new DisplayProfileSettings(null, true, null)));

        Assert.NotNull(checkpoint);
        Assert.Null(checkpoint.Baseline);
        Assert.Null(checkpoint.ApplicationProfileId);
        Assert.False(checkpoint.OwnedHdr);
        Assert.True(checkpoint.ManualHold);
        Assert.True(checkpoint.AutomaticWritesPaused);
        Assert.True(checkpoint.UncertainHdr);
    }

    [Fact]
    public async Task RestartPreservesManualHoldUntilPowerSourceChanges()
    {
        (ProfileSet profiles, Guid id) = ApplicationProfiles(hdr: true);
        var recovery = new DisplayAutomationRecoveryState(
            PowerSource.PluggedIn,
            id,
            new DisplayProfileSettings("fixed-60", false, "native"),
            OwnedRefreshRate: false,
            OwnedHdr: true,
            OwnedColorProfile: false,
            OwnedBrightness: false,
            ManualHold: true,
            AutomaticWritesPaused: false,
            UncertainRefreshRate: false,
            UncertainHdr: false,
            UncertainColorProfile: false,
            UncertainBrightness: false);
        var applier = new FakeApplier();
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            applier,
            recoveryState: recovery);

        Assert.Equal(
            DisplayAutomationObservation.SameSource,
            coordinator.Observe(Status(PowerSource.PluggedIn, profiles) with
            {
                ActiveApplicationProfileId = id,
            }));
        Assert.Empty(applier.Transitions);

        Assert.Equal(
            DisplayAutomationObservation.TransitionScheduled,
            coordinator.Observe(Status(PowerSource.Battery, profiles) with
            {
                ActiveApplicationProfileId = id,
            }));
        await coordinator.WhenIdleAsync();
        Assert.Single(applier.Transitions);
    }

    [Fact]
    public async Task ApplicationExitRestoresCapturedFieldBeforeConfiguredBase()
    {
        var applier = new FakeApplier
        {
            QueryResult = new SessionDisplayExecutionResult(
                SessionDisplayOperationStatus.Succeeded,
                null,
                SessionDisplayIpcCodecTests.Snapshot("fixed-60", true, "native")),
        };
        (ProfileSet original, Guid id) = ApplicationProfiles(hdr: false);
        ProfileSet profiles = original with
        {
            PluggedIn = original.PluggedIn with
            {
                Display = new DisplayProfileSettings(null, false, null),
            },
        };
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            applier);
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles));
        coordinator.Observe(Status(PowerSource.PluggedIn, profiles) with
        {
            ActiveApplicationProfileId = id,
        });
        await coordinator.WhenIdleAsync();

        coordinator.Observe(Status(PowerSource.PluggedIn, profiles));
        await coordinator.WhenIdleAsync();

        Assert.Equal(2, applier.Transitions.Count);
        Assert.False(applier.Transitions[0].Settings.HdrEnabled);
        Assert.True(applier.Transitions[1].Settings.HdrEnabled);
    }

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
    public async Task RestartAfterMissedSourceChange_AppliesCurrentDestinationProfile()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier();
        PowerSource? reconciled = null;
        await using var coordinator = new DisplayAutomationCoordinator(
            context,
            applier,
            lastReconciledPowerSource: PowerSource.PluggedIn,
            recordReconciledPowerSource: source => reconciled = source);

        DisplayAutomationObservation result = coordinator.Observe(
            Status(PowerSource.Battery, Profiles("restart")));
        await coordinator.WhenIdleAsync();

        Assert.Equal(DisplayAutomationObservation.TransitionScheduled, result);
        Assert.Equal(PowerSource.Battery, Assert.Single(applier.Transitions).Source);
        Assert.Equal(PowerSource.Battery, reconciled);
    }

    [Fact]
    public async Task RestartOnSameSource_DoesNotOverwriteExternalDisplayChanges()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier();
        await using var coordinator = new DisplayAutomationCoordinator(
            context,
            applier,
            lastReconciledPowerSource: PowerSource.UsbC);

        DisplayAutomationObservation result = coordinator.Observe(
            Status(PowerSource.UsbC, Profiles("restart")));
        await coordinator.WhenIdleAsync();

        Assert.Equal(DisplayAutomationObservation.SameSource, result);
        Assert.Empty(applier.Transitions);
        Assert.Equal(PowerSource.UsbC, context.Capture().PowerSource);
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
    public async Task FailedTransition_RetriesOnceWithoutAnotherServiceStatus()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier();
        applier.Results.Enqueue(Failure("Windows temporarily rejected the change."));
        applier.Results.Enqueue(Success());
        await using var coordinator = new DisplayAutomationCoordinator(
            context,
            applier,
            retryDelay: static (_, _) => Task.CompletedTask);
        coordinator.Observe(Status(PowerSource.PluggedIn, Profiles("initial"), revision: 1));
        ProfileSet destination = Profiles("destination");

        coordinator.Observe(Status(PowerSource.Battery, destination, revision: 2));
        await coordinator.WhenIdleAsync();

        Assert.Equal(2, applier.Transitions.Count);
        Assert.All(applier.Transitions, transition =>
        {
            Assert.Equal(PowerSource.Battery, transition.Source);
            Assert.Equal("destination-battery", transition.Settings.RefreshRateKey);
        });
        Assert.Equal(SessionDisplayOperationStatus.Succeeded,
            coordinator.LastTransitionResult?.Result.Status);

        Assert.Equal(
            DisplayAutomationObservation.SameSource,
            coordinator.Observe(Status(PowerSource.Battery, destination, revision: 3)));
        await coordinator.WhenIdleAsync();
        Assert.Equal(2, applier.Transitions.Count);
    }

    [Fact]
    public async Task PersistentlyFailedTransition_StopsAfterOneAutomaticRetry()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier();
        applier.Results.Enqueue(Failure("first failure"));
        applier.Results.Enqueue(Failure("second failure"));
        await using var coordinator = new DisplayAutomationCoordinator(
            context,
            applier,
            retryDelay: static (_, _) => Task.CompletedTask);
        coordinator.Observe(Status(PowerSource.PluggedIn, Profiles("initial"), revision: 1));
        ProfileSet destination = Profiles("destination");

        coordinator.Observe(Status(PowerSource.Battery, destination, revision: 2));
        await coordinator.WhenIdleAsync();

        Assert.Equal(SessionDisplayOperationStatus.Failed,
            coordinator.LastTransitionResult?.Result.Status);
        Assert.Equal(
            DisplayAutomationObservation.SameSource,
            coordinator.Observe(Status(PowerSource.Battery, destination, revision: 3)));
        await coordinator.WhenIdleAsync();
        Assert.Equal(2, applier.Transitions.Count);
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
    public async Task UnknownInterruptingPotentialWriteRequiresReadBeforeReschedule()
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

        Assert.Equal(DisplayAutomationObservation.SameSource, recovered);
        Assert.True(applier.FirstTransitionCanceled);
        Assert.True(coordinator.ResumeAfterSuccessfulExplicitRead(applier.QueryResult.State));
        await coordinator.WhenIdleAsync();
        Assert.Equal(2, applier.Transitions.Count);
        Assert.Equal("first-b-battery", applier.Transitions[0].Settings.RefreshRateKey);
        Assert.Equal("recovered-b-battery", applier.Transitions[1].Settings.RefreshRateKey);
        Assert.Equal(
            applier.Transitions[1].Generation,
            coordinator.LastTransitionResult?.Generation);
        Assert.Equal(PowerSource.Battery, context.Capture().PowerSource);
    }

    [Fact]
    public async Task SubscriptionUnavailableRequiresReadBeforeReschedulingPotentialWrite()
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

        Assert.Equal(DisplayAutomationObservation.SameSource, recovered);
        Assert.True(coordinator.ResumeAfterSuccessfulExplicitRead(applier.QueryResult.State));
        await coordinator.WhenIdleAsync();
        Assert.Equal(2, applier.Transitions.Count);
        Assert.True(applier.FirstTransitionCanceled);
        Assert.Equal(
            "reconnected-b-battery",
            applier.Transitions[1].Settings.RefreshRateKey);
    }

    [Fact]
    public async Task RapidTransitionRequiresReadBeforeApplyingLatestTarget()
    {
        var context = new DisplayPowerContextState();
        var applier = new FakeApplier { BlockFirstTransition = true };
        await using var coordinator = new DisplayAutomationCoordinator(context, applier);
        coordinator.Observe(Status(PowerSource.PluggedIn, Profiles("initial"), revision: 1));
        coordinator.Observe(Status(PowerSource.Battery, Profiles("battery"), revision: 2));
        await applier.FirstTransitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.Observe(Status(PowerSource.UsbC, Profiles("usb"), revision: 3));
        await coordinator.WhenIdleAsync();

        Assert.True(applier.FirstTransitionCanceled);
        Assert.Single(applier.Transitions);
        Assert.True(coordinator.ResumeAfterSuccessfulExplicitRead(applier.QueryResult.State));
        await coordinator.WhenIdleAsync();
        Assert.Equal(2, applier.Transitions.Count);
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

    private static (ProfileSet Profiles, Guid Id) ApplicationProfiles(bool hdr)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        Guid id = Guid.NewGuid();
        return (defaults with
        {
            ApplicationProfiles =
            [
                new ApplicationProfileDefinition(
                    id,
                    "Game",
                    @"C:\Games\game.exe",
                    ApplicationActivationMode.Foreground,
                    new ApplicationProfileOverrideSet
                    {
                        PluggedIn = new ApplicationPowerProfileOverride
                        {
                            Display = new DisplayProfileSettings(null, hdr, null),
                        },
                    }),
            ],
        }, id);
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
        public Queue<SessionDisplayExecutionResult> Results { get; } = [];
        public SessionDisplayExecutionResult QueryResult { get; init; } = Success();
        public bool BlockFirstTransition { get; init; }
        public bool ReturnLateIndeterminate { get; init; }
        public bool ReturnLateSuccess { get; init; }
        public bool FirstTransitionCanceled { get; private set; }
        public TaskCompletionSource FirstTransitionStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstTransition { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SessionDisplayExecutionResult> QueryAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(QueryResult);

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

        public Task<SessionDisplayExecutionResult> SetBrightnessAsync(
            PowerSource expectedPowerSource,
            byte percent,
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
            if (ReturnLateIndeterminate && ordinal == 1)
            {
                FirstTransitionStarted.TrySetResult();
                await ReleaseFirstTransition.Task;
                return Indeterminate();
            }
            if (ReturnLateSuccess && ordinal == 1)
            {
                FirstTransitionStarted.TrySetResult();
                await ReleaseFirstTransition.Task;
                return Success();
            }
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
            return Results.TryDequeue(out SessionDisplayExecutionResult? result)
                ? result
                : Success();
        }

        private static SessionDisplayExecutionResult Success() =>
            DisplayAutomationCoordinatorTests.Success();
    }

    private static SessionDisplayExecutionResult Success() => new(
            SessionDisplayOperationStatus.Succeeded,
            null,
            SessionDisplayIpcCodecTests.Snapshot("fixed-60", false, "native"));

    private static SessionDisplayExecutionResult Failure(string error) => new(
        SessionDisplayOperationStatus.Failed,
        error,
        SessionDisplayIpcCodecTests.Snapshot("fixed-60", false, "native"));

    private static SessionDisplayExecutionResult Indeterminate() => new(
        SessionDisplayOperationStatus.Indeterminate,
        "The write may have been submitted.",
        SessionDisplayIpcCodecTests.Snapshot("fixed-60", false, "native"));
}
