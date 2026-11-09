using OpenBlade.Capture;
using OpenBlade.Contracts;

namespace OpenBlade.Capture.Tests;

public sealed class So6120DirectDutyCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThreeChannelEntryRequiresActualThirdDutyAndRestoresExactStaging(bool thirdResponds)
    {
        var fake = new Fake();
        fake.Controls = fake.Controls with { Staged3 = 0xF0 };
        fake.State = fake.State with { Actual3 = 70, Tach3 = 20 };
        if (!thirdResponds)
        {
            fake.OnWrite = _ => fake.State = fake.State with { Actual3 = 70 };
        }
        So6120DutyControls original = fake.Controls;
        var operation = fake.Operation(threeChannels: true);
        var entry = new So6120DirectDutyCoordinator(operation).EnterThreeChannel(PerformancePreset.Silent, 0, 0, 0);
        Assert.Equal(thirdResponds ? So6120DutyFailure.None : So6120DutyFailure.PwmNotObserved, entry.Failure);
        Assert.Equal(new[] { So6120MailboxRegister.StagedDuty1, So6120MailboxRegister.StagedDuty2,
            So6120MailboxRegister.StagedDuty3, So6120MailboxRegister.Override, So6120MailboxRegister.Selector },
            fake.Writes.Select(x => x.Register));
        Assert.Equal(new byte[] { 0, 0, 0, 0x51, 0xFE }, fake.Writes.Select(x => x.Value));
        var checkpoint = Assert.IsType<So6120DutyCheckpoint>(entry.CleanupCheckpoint);
        Assert.True(checkpoint.Owned.HasFlag(So6120DutyOwnedFields.Staged3));
        var exit = new So6120DirectDutyAutoExit(operation).Restore(checkpoint);
        Assert.True(exit.Success);
        Assert.True(exit.CleanupConfirmed);
        Assert.Equal(original, fake.Controls);
        Assert.Equal(new[] { So6120MailboxRegister.Selector, So6120MailboxRegister.Override,
            So6120MailboxRegister.StagedDuty1, So6120MailboxRegister.StagedDuty2, So6120MailboxRegister.StagedDuty3 },
            fake.Writes.Skip(5).Select(x => x.Register));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ThreeChannelObservationMissingAnyThirdFieldFencesBeforeFirstWrite(int missing)
    {
        var fake = new Fake();
        fake.Controls = fake.Controls with { Staged3 = missing == 0 ? null : (byte)30 };
        fake.State = fake.State with { Actual3 = missing == 1 ? null : (byte)70, Tach3 = missing == 2 ? null : (byte)20 };
        var operation = fake.Operation(threeChannels: true);
        var entry = new So6120DirectDutyCoordinator(operation).EnterThreeChannel(PerformancePreset.Silent, 0, 0, 0);
        Assert.Equal(So6120DutyFailure.ObservationRejected, entry.Failure);
        Assert.True(entry.CommandsFenced);
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public void ThirdTachMustIndependentlyReachZeroAfterZeroPwm()
    {
        var controls = new So6120DutyControls(0xFE, 0, 0x40, 0, 0, 183, 0) { Staged3 = 0 };
        var snapshot = new So6120DutySnapshot(controls, 0, 0, 0, 0, 0, 1) { Actual3 = 0, Tach3 = 10 };
        Assert.True(snapshot.MatchesActualDuty(0, 0, 0));
        Assert.False(snapshot.AllTachStopped(threeChannels: true));
        Assert.False((snapshot with { Tach3 = null }).AllTachStopped(threeChannels: true));
        Assert.True((snapshot with { Tach3 = 0 }).AllTachStopped(threeChannels: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(69)]
    public void ThreeChannelReadbackRejectsInvalidCycleOrOutOfRangeActualDuty(int cycle)
    {
        var controls = new So6120DutyControls(0xFE, 0, 0x40, 0, 0, (byte)cycle, 0) { Staged3 = 0 };
        var snapshot = new So6120DutySnapshot(controls, 0, 0, 0, 0, 0, 1) { Actual3 = 70, Tach3 = 20 };
        Assert.False(snapshot.HasValidReadback(threeChannels: true));
    }

    [Fact]
    public void MissingThirdReadbackAfterSubmissionFencesWithoutReplayOrHandback()
    {
        var fake = new Fake();
        fake.Controls = fake.Controls with { Staged3 = 30 };
        fake.State = fake.State with { Actual3 = 70, Tach3 = 20 };
        fake.OnWrite = mutation =>
        {
            if (mutation.Register == So6120MailboxRegister.StagedDuty3)
            {
                fake.State = fake.State with { Actual3 = null };
            }
        };
        var operation = fake.Operation(threeChannels: true);
        var entry = new So6120DirectDutyCoordinator(operation).EnterThreeChannel(PerformancePreset.Silent, 0, 0, 0);
        Assert.Equal(So6120DutyFailure.CommandUnresolved, entry.Failure);
        Assert.True(entry.CommandsFenced);
        Assert.Null(entry.CleanupCheckpoint);
        Assert.Equal(3, fake.Writes.Count);
        Assert.Equal(So6120MailboxRegister.StagedDuty3, fake.Writes[^1].Register);
    }

    [Fact]
    public void EntryStagesBeforePermissionAndFeThenExitRestoresExactOwnedBytes()
    {
        var fake = new Fake();
        var original = fake.State.Controls;
        var operation = fake.Operation();
        var entry = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 30, 40);
        Assert.True(entry.Success);
        Assert.True(entry.DirectEntryConfirmed);
        Assert.True(entry.PermissionEnabledConfirmed);
        Assert.True(entry.PermissionExposureMayRemain);
        Assert.Equal(new[] { So6120MailboxRegister.StagedDuty1, So6120MailboxRegister.StagedDuty2,
            So6120MailboxRegister.Override, So6120MailboxRegister.Selector }, fake.Writes.Select(x => x.Register));
        Assert.Equal(new byte[] { 30, 40, 0x51, 0xFE }, fake.Writes.Select(x => x.Value));
        var exit = new So6120DirectDutyAutoExit(operation).Restore(entry.CleanupCheckpoint!);
        Assert.True(exit.Success);
        Assert.True(exit.NormalModeObserved);
        Assert.True(exit.CleanupConfirmed);
        Assert.True(exit.PermissionRestoredConfirmed);
        Assert.False(exit.PermissionExposureMayRemain);
        Assert.Equal(original, fake.State.Controls);
        Assert.Equal(new[] { So6120MailboxRegister.Selector, So6120MailboxRegister.Override,
            So6120MailboxRegister.StagedDuty1, So6120MailboxRegister.StagedDuty2 }, fake.Writes.Skip(4).Select(x => x.Register));
        Assert.DoesNotContain(fake.Writes, x => x.Register is So6120MailboxRegister.Manual or So6120MailboxRegister.PolicyCommand);
        Assert.All(fake.Writes, x => Assert.Equal((byte)0, x.Expected.PolicyCommand));
        Assert.All(entry.Observations, x => Assert.True(x.CompletedTimestamp >= x.StartedTimestamp));
    }

    [Theory]
    [InlineData(PerformancePreset.Balanced, 0)]
    [InlineData(PerformancePreset.Performance, 2)]
    [InlineData(PerformancePreset.BatterySaver, 3)]
    [InlineData(PerformancePreset.Silent, 5)]
    [InlineData(PerformancePreset.BatteryBalanced, 6)]
    public void AutoExitRestoresCapturedNormalProfileWithoutSilentOrBalancedIntermediate(PerformancePreset profile, int selector)
    {
        var fake = new Fake();
        fake.Controls = fake.Controls with { Selector = (byte)selector };
        var operation = fake.Operation();
        var entry = new So6120DirectDutyCoordinator(operation).Enter(profile, 0, 0);
        Assert.True(entry.Success);
        Assert.True(new So6120DirectDutyAutoExit(operation).Restore(entry.CleanupCheckpoint!).Success);
        Assert.Equal(new byte[] { 0xFE, (byte)selector }, fake.Writes.Where(x => x.Register == So6120MailboxRegister.Selector).Select(x => x.Value));
    }

    [Fact]
    public void UnchangedStagingIsNeitherWrittenNorOwned()
    {
        var fake = new Fake();
        var operation = fake.Operation();
        var entry = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 10, 20);
        Assert.True(entry.Success);
        Assert.Equal(So6120DutyOwnedFields.Override | So6120DutyOwnedFields.Selector, entry.CleanupCheckpoint!.Owned);
        Assert.True(new So6120DirectDutyAutoExit(operation).Restore(entry.CleanupCheckpoint).Success);
        Assert.DoesNotContain(fake.Writes, x => x.Register is So6120MailboxRegister.StagedDuty1 or So6120MailboxRegister.StagedDuty2);
    }

    [Fact]
    public void InactiveSavedStagingAboveCycleIsRestoredExactlyAfterNormalHandback()
    {
        var fake = new Fake();
        fake.Controls = fake.Controls with { Staged1 = 255, Staged2 = 254 };
        var operation = fake.Operation();
        var entry = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 0, 0);
        Assert.True(entry.Success);
        var exit = new So6120DirectDutyAutoExit(operation).Restore(entry.CleanupCheckpoint!);
        Assert.True(exit.Success);
        Assert.Equal((byte)255, fake.Controls.Staged1);
        Assert.Equal((byte)254, fake.Controls.Staged2);
        Assert.All(fake.Writes.Where(x => x.Value >= 254 && x.Register != So6120MailboxRegister.Selector),
            x => Assert.Equal((byte)5, x.Expected.Selector));
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("policy")]
    [InlineData("permission-owner")]
    [InlineData("sensor-suppression")]
    [InlineData("inactive")]
    [InlineData("zero-cycle")]
    [InlineData("selector")]
    [InlineData("actual-above-cycle")]
    [InlineData("request-above-cycle")]
    public void UnsafeBaselineNeverCreatesIntentOrWrites(string defect)
    {
        var fake = new Fake();
        fake.Controls = defect switch
        {
            "manual" => fake.Controls with { Manual = 1 },
            "policy" => fake.Controls with { PolicyCommand = 5 },
            "permission-owner" => fake.Controls with { Override = 0x40 },
            "sensor-suppression" => fake.Controls with { Override = 0x20 },
            "zero-cycle" => fake.Controls with { Cycle = 0 },
            "selector" => fake.Controls with { Selector = 0xFE },
            _ => fake.Controls,
        };
        if (defect == "inactive") { fake.State = fake.State with { ActiveState = 0 }; }
        if (defect == "actual-above-cycle") { fake.State = fake.State with { Actual1 = 255 }; }
        var result = new So6120DirectDutyCoordinator(fake.Operation()).Enter(PerformancePreset.Silent,
            defect == "request-above-cycle" ? (byte)255 : (byte)30, 40);
        Assert.Equal(So6120DutyFailure.BaselineRejected, result.Failure);
        Assert.Empty(fake.Writes);
        Assert.Equal(0, fake.Intents);
    }

    [Theory]
    [InlineData(PerformancePreset.Custom)]
    [InlineData((PerformancePreset)999)]
    public void UnsupportedProfileIsRejectedBeforeClaim(PerformancePreset profile)
    {
        var fake = new Fake();
        Assert.Equal(So6120DutyFailure.InvalidRequest,
            new So6120DirectDutyCoordinator(fake.Operation()).Enter(profile, 1, 1).Failure);
        Assert.Empty(fake.Events);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ConfirmedPartialEntryHasExplicitPrefixCleanupOnly(int refusedWrite)
    {
        var fake = new Fake { RefuseAdmissionAt = refusedWrite };
        var baseline = fake.Controls;
        var operation = fake.Operation();
        var entry = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 30, 40);
        Assert.Equal(So6120DutyFailure.AdmissionRefused, entry.Failure);
        Assert.False(entry.CommandsFenced);
        Assert.False(entry.DirectEntryConfirmed);
        Assert.NotNull(entry.CleanupCheckpoint);
        Assert.Equal(refusedWrite - 1, fake.Writes.Count);
        Assert.Equal(1, fake.Resolutions);
        fake.RefuseAdmissionAt = 0;
        var exit = new So6120DirectDutyAutoExit(operation).Restore(entry.CleanupCheckpoint!);
        Assert.True(exit.Success);
        Assert.Equal(baseline, fake.Controls);
        Assert.DoesNotContain(fake.Writes, x => x.Register == So6120MailboxRegister.Selector);
        if (refusedWrite == 4)
        {
            Assert.Equal(So6120MailboxRegister.Override, fake.Writes[3].Register);
            Assert.True(exit.PermissionRestoredConfirmed);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AmbiguousWriteStopsEvenFurtherA0ReadsAndRejectsOldCheckpoint(int failedWrite)
    {
        var fake = new Fake { UnresolvedWriteAt = failedWrite };
        var operation = fake.Operation();
        var entry = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 30, 40);
        Assert.Equal(So6120DutyFailure.CommandUnresolved, entry.Failure);
        Assert.True(entry.CommandsFenced);
        Assert.True(entry.DurableFenceConfirmed);
        Assert.Null(entry.CleanupCheckpoint);
        Assert.Equal(failedWrite, fake.Writes.Count);
        Assert.Equal(new[] { "write", "fence" }, fake.Events.TakeLast(2));
        int events = fake.Events.Count;
        Assert.Equal(So6120DutyFailure.AlreadyUsed,
            new So6120DirectDutyAutoExit(operation).Restore(fake.Checkpoints[0]).Failure);
        Assert.Equal(So6120DutyFailure.AlreadyUsed,
            new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 1, 1).Failure);
        Assert.Equal(events, fake.Events.Count);
        Assert.Equal(failedWrite >= 3, entry.PermissionPublicationPossible);
        Assert.False(entry.PermissionRestoredConfirmed);
    }

    [Fact]
    public void EveryUnresolvedSnapshotStopsAllFurtherCommands()
    {
        var successful = new Fake();
        Assert.True(new So6120DirectDutyCoordinator(successful.Operation()).Enter(PerformancePreset.Silent, 30, 40).Success);
        for (int read = 1; read <= successful.Reads; read++)
        {
            var fake = new Fake { UnresolvedReadAt = read };
            var result = new So6120DirectDutyCoordinator(fake.Operation()).Enter(PerformancePreset.Silent, 30, 40);
            Assert.Equal(So6120DutyFailure.CommandUnresolved, result.Failure);
            Assert.Equal(read, fake.Reads);
            Assert.True(result.CommandsFenced);
            Assert.Equal(new[] { "read", "fence" }, fake.Events.TakeLast(2));
            Assert.Equal(1, fake.Fences);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void JournalUncertaintyFencesLocallyEvenWhenDurableFenceFails(int checkpoint)
    {
        var fake = new Fake { FailCheckpointAt = checkpoint, FenceSucceeds = false };
        var operation = fake.Operation();
        var result = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 30, 40);
        Assert.Equal(So6120DutyFailure.JournalUnresolved, result.Failure);
        Assert.True(result.CommandsFenced);
        Assert.False(result.DurableFenceConfirmed);
        Assert.Null(result.CleanupCheckpoint);
        Assert.Equal(checkpoint - 1, fake.Writes.Count);
        int events = fake.Events.Count;
        Assert.Equal(So6120DutyFailure.AlreadyUsed,
            new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 30, 40).Failure);
        Assert.Equal(events, fake.Events.Count);
    }

    [Theory]
    [InlineData("selector")]
    [InlineData("manual")]
    [InlineData("override")]
    [InlineData("staged1")]
    [InlineData("staged2")]
    [InlineData("cycle")]
    [InlineData("policy")]
    [InlineData("active")]
    public void FullExpectedStateIsRecheckedAfterAdmissionBeforeStartingA1(string changed)
    {
        var fake = new Fake();
        fake.OnAdmission = () =>
        {
            fake.Controls = changed switch
            {
                "selector" => fake.Controls with { Selector = 2 },
                "manual" => fake.Controls with { Manual = 1 },
                "override" => fake.Controls with { Override = (byte)(fake.Controls.Override ^ 1) },
                "staged1" => fake.Controls with { Staged1 = 11 },
                "staged2" => fake.Controls with { Staged2 = 21 },
                "cycle" => fake.Controls with { Cycle = 182 },
                "policy" => fake.Controls with { PolicyCommand = 5 },
                _ => fake.Controls,
            };
            if (changed == "active") { fake.State = fake.State with { ActiveState = 0 }; }
        };
        var result = new So6120DirectDutyCoordinator(fake.Operation()).Enter(PerformancePreset.Silent, 30, 40);
        Assert.Equal(So6120DutyFailure.StateChanged, result.Failure);
        Assert.True(result.CommandsFenced);
        Assert.Empty(fake.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterIntentNeedsConfirmedNoSubmissionResolution(bool resolutionFails)
    {
        using var cancel = new CancellationTokenSource();
        var fake = new Fake { OnIntent = cancel.Cancel, FailResolution = resolutionFails };
        var result = new So6120DirectDutyCoordinator(fake.Operation()).Enter(PerformancePreset.Silent, 30, 40,
            cancellationToken: cancel.Token);
        Assert.Empty(fake.Writes);
        Assert.Equal(1, fake.Resolutions);
        Assert.Equal(resolutionFails ? So6120DutyFailure.JournalUnresolved : So6120DutyFailure.Cancelled, result.Failure);
        Assert.Equal(resolutionFails, result.CommandsFenced);
    }

    [Fact]
    public void CancellationAfterA1ConfirmationCannotStartAnotherReadOrCleanup()
    {
        using var cancel = new CancellationTokenSource();
        var fake = new Fake { OnWrite = _ => cancel.Cancel() };
        var result = new So6120DirectDutyCoordinator(fake.Operation()).Enter(PerformancePreset.Silent, 30, 40,
            cancellationToken: cancel.Token);
        Assert.Equal(So6120DutyFailure.CommandUnresolved, result.Failure);
        Assert.True(result.CommandsFenced);
        Assert.Equal(new[] { "write", "fence" }, fake.Events.TakeLast(2));
    }

    [Fact]
    public void KnownUnsubmittedPermissionDoesNotClaimExposureAndHasConfirmedPartialCleanup()
    {
        var fake = new Fake { UnsubmittedWriteAt = 3 };
        var operation = fake.Operation();
        var result = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 30, 40);
        Assert.Equal(So6120DutyFailure.NotSubmitted, result.Failure);
        Assert.False(result.PermissionPublicationPossible);
        Assert.False(result.CommandsFenced);
        Assert.NotNull(result.CleanupCheckpoint);
        Assert.True(new So6120DirectDutyAutoExit(operation).Restore(result.CleanupCheckpoint!).Success);
    }

    [Fact]
    public void BoundedPwmObservationFailureRetainsConfirmedEntryForExplicitAutoExit()
    {
        var fake = new Fake { ApplyPwm = false };
        var operation = fake.Operation();
        var result = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 30, 40, maxPwmObservations: 3);
        Assert.Equal(So6120DutyFailure.PwmNotObserved, result.Failure);
        Assert.True(result.DirectEntryConfirmed);
        Assert.False(result.CommandsFenced);
        Assert.True(new So6120DirectDutyAutoExit(operation).Restore(result.CleanupCheckpoint!).Success);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("policy")]
    [InlineData("active")]
    public void HandbackDriftIsNeverRepairedByAnotherWrite(string changed)
    {
        var fake = new Fake();
        var operation = fake.Operation();
        var entry = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 30, 40);
        fake.OnWrite = mutation =>
        {
            if (mutation.Register != So6120MailboxRegister.Selector) { return; }
            if (changed == "manual") { fake.Controls = fake.Controls with { Manual = 1 }; }
            if (changed == "policy") { fake.Controls = fake.Controls with { PolicyCommand = 5 }; }
            if (changed == "active") { fake.State = fake.State with { ActiveState = 0 }; }
        };
        var exit = new So6120DirectDutyAutoExit(operation).Restore(entry.CleanupCheckpoint!);
        Assert.Equal(So6120DutyFailure.StateChanged, exit.Failure);
        Assert.True(exit.CommandsFenced);
        Assert.Equal(5, fake.Writes.Count);
        Assert.False(exit.CleanupConfirmed);
        Assert.True(exit.PermissionExposureMayRemain);
    }

    [Fact]
    public void ObservedHandbackAndPermissionRestoreSurviveLaterJournalFailure()
    {
        var fake = new Fake();
        var operation = fake.Operation();
        var entry = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 30, 40);
        fake.FailCheckpointAt = 7; // Entry has five checkpoints; normal and F0 are next.
        var exit = new So6120DirectDutyAutoExit(operation).Restore(entry.CleanupCheckpoint!);
        Assert.Equal(So6120DutyFailure.JournalUnresolved, exit.Failure);
        Assert.True(exit.NormalModeObserved);
        Assert.True(exit.PermissionRestoredConfirmed);
        Assert.False(exit.PermissionExposureMayRemain);
        Assert.False(exit.CleanupConfirmed);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("sequence")]
    [InlineData("start")]
    [InlineData("end")]
    public void ObservationProvenanceFailureFencesBeforeMutation(string defect)
    {
        var fake = new Fake();
        fake.TransformObservation = observation => defect switch
        {
            "owner" => observation with { Owner = observation.Owner with { OperationId = Guid.NewGuid() } },
            "sequence" => observation with { Sequence = 0 },
            "start" => observation with { StartedTimestamp = -1 },
            _ => observation with { CompletedTimestamp = observation.StartedTimestamp - 1 },
        };
        var result = new So6120DirectDutyCoordinator(fake.Operation()).Enter(PerformancePreset.Silent, 30, 40);
        Assert.Equal(So6120DutyFailure.ObservationRejected, result.Failure);
        Assert.True(result.CommandsFenced);
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public void FinalPolicyCommandDriftPreventsCleanupSuccessWithoutFurtherCommands()
    {
        var fake = new Fake();
        var operation = fake.Operation();
        var entry = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 30, 40);
        int readsAfterLastWrite = 0;
        fake.OnRead = () =>
        {
            if (fake.Writes.Count == 8 && ++readsAfterLastWrite == 2)
            { fake.Controls = fake.Controls with { PolicyCommand = 5 }; }
        };
        var exit = new So6120DirectDutyAutoExit(operation).Restore(entry.CleanupCheckpoint!);
        Assert.Equal(So6120DutyFailure.StateChanged, exit.Failure);
        Assert.True(exit.NormalModeObserved);
        Assert.True(exit.PermissionRestoredConfirmed);
        Assert.False(exit.CleanupConfirmed);
        Assert.Equal(8, fake.Writes.Count);
        Assert.Equal(new[] { "read", "fence" }, fake.Events.TakeLast(2));
    }

    [Fact]
    public void NonOwnedOverrideDriftAfterHandbackCannotBeClobberedByRestoration()
    {
        var fake = new Fake();
        var operation = fake.Operation();
        var entry = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 30, 40);
        int readsAfterSelector = 0;
        fake.OnRead = () =>
        {
            if (fake.Writes.Count == 5 && ++readsAfterSelector == 2)
            { fake.Controls = fake.Controls with { Override = (byte)(fake.Controls.Override ^ 1) }; }
        };
        var exit = new So6120DirectDutyAutoExit(operation).Restore(entry.CleanupCheckpoint!);
        Assert.Equal(So6120DutyFailure.StateChanged, exit.Failure);
        Assert.True(exit.NormalModeObserved);
        Assert.False(exit.PermissionRestoredConfirmed);
        Assert.Equal(5, fake.Writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExitRejectsReplayOfEntryObservationNewerThanItsDurableCheckpoint(bool pwmApplied)
    {
        var fake = new Fake { ApplyPwm = pwmApplied };
        var operation = fake.Operation();
        var entry = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 30, 40, maxPwmObservations: 3);
        Assert.Equal(pwmApplied ? So6120DutyFailure.None : So6120DutyFailure.PwmNotObserved, entry.Failure);
        So6120DutyObservation last = entry.Observations[^1];
        Assert.True(last.Sequence > entry.CleanupCheckpoint!.Observation.Sequence);
        int writes = fake.Writes.Count;
        fake.TransformObservation = _ => last;
        var exit = new So6120DirectDutyAutoExit(operation).Restore(entry.CleanupCheckpoint);
        Assert.Equal(So6120DutyFailure.ObservationRejected, exit.Failure);
        Assert.True(exit.CommandsFenced);
        Assert.Equal(writes, fake.Writes.Count);
        Assert.Equal(new[] { "claim-exit", "read", "fence" }, fake.Events.TakeLast(3));
    }

    [Fact]
    public void StaleReceiptAndRepeatedExitCannotReplayCommands()
    {
        var fake = new Fake();
        var operation = fake.Operation();
        var entry = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Silent, 30, 40);
        int events = fake.Events.Count;
        Assert.Equal(So6120DutyFailure.AlreadyUsed,
            new So6120DirectDutyAutoExit(operation).Restore(fake.Checkpoints[0]).Failure);
        Assert.Equal(events, fake.Events.Count);
        Assert.True(new So6120DirectDutyAutoExit(operation).Restore(entry.CleanupCheckpoint!).Success);
        events = fake.Events.Count;
        Assert.Equal(So6120DutyFailure.AlreadyUsed,
            new So6120DirectDutyAutoExit(operation).Restore(entry.CleanupCheckpoint!).Failure);
        Assert.Equal(events, fake.Events.Count);
    }

    private sealed class Fake : ISo6120DirectDutyBoundary
    {
        public DirectDutyOwner Owner { get; } = new(Guid.NewGuid(), "RZ09-0581/1532:02E0", "SO6120 EC1.04", Guid.NewGuid());
        public So6120DutySnapshot State = new(new(5, 0, 0x11, 10, 20, 183, 0), 50, 60, 20, 20, 4, 1);
        public So6120DutyControls Controls { get => State.Controls; set => State = State with { Controls = value }; }
        public List<So6120DutyMutation> Writes { get; } = [];
        public List<So6120DutyCheckpoint> Checkpoints { get; } = [];
        public List<string> Events { get; } = [];
        public int Reads, Intents, Admissions, Fences, Resolutions, CheckpointCalls;
        public int RefuseAdmissionAt, UnresolvedWriteAt, UnresolvedReadAt, FailCheckpointAt, UnsubmittedWriteAt;
        public bool FenceSucceeds = true, ApplyPwm = true, FailResolution;
        public Action? OnAdmission, OnIntent, OnRead;
        public Func<So6120DutyObservation, So6120DutyObservation>? TransformObservation;
        public Action<So6120DutyMutation>? OnWrite;
        private long sequence;
        public So6120DirectDutyOperation Operation(bool threeChannels = false) => new(Owner, this, threeChannels);
        public bool ClaimEntry(DirectDutyOwner owner, PerformancePreset capturedProfile) { Events.Add("claim-entry"); return true; }
        public bool ClaimExit(So6120DutyCheckpoint checkpoint) { Events.Add("claim-exit"); return true; }
        public So6120DutyReadResult ReadSnapshot(CancellationToken cancellationToken)
        {
            Events.Add("read");
            Reads++;
            OnRead?.Invoke();
            if (Reads == UnresolvedReadAt) { return new(So6120DutyIoDisposition.Unresolved, null); }
            sequence++;
            var observation = new So6120DutyObservation(Owner, sequence, sequence * 10, sequence * 10 + 1, State);
            return new(So6120DutyIoDisposition.Confirmed, TransformObservation?.Invoke(observation) ?? observation);
        }
        public bool RecordIntent(DirectDutyOwner owner, So6120DutyMutation mutation, So6120DutyObservation observation)
        {
            Events.Add("intent");
            Intents++;
            Assert.Equal(Controls, observation.Snapshot.Controls);
            OnIntent?.Invoke();
            return true;
        }
        public bool AdmitWrite(DirectDutyOwner owner, So6120DutyMutation mutation)
        {
            Events.Add("admit");
            Admissions++;
            OnAdmission?.Invoke();
            return Admissions != RefuseAdmissionAt;
        }
        public bool RecordNotSubmitted(DirectDutyOwner owner, So6120DutyMutation mutation, So6120DutyFailure reason)
        {
            Events.Add("not-submitted");
            Resolutions++;
            return !FailResolution;
        }
        public So6120DutyWriteResult WriteAndConfirm(So6120DutyMutation mutation, So6120DutyObservation before,
            CancellationToken cancellationToken)
        {
            Events.Add("write");
            Assert.Equal(Controls, mutation.Expected);
            Assert.Equal(Controls, before.Snapshot.Controls);
            Assert.Equal(sequence, before.Sequence);
            Writes.Add(mutation);
            if (Writes.Count == UnsubmittedWriteAt)
            {
                return new(So6120DutyIoDisposition.DefinitelyUnsubmitted,
                    new(So6120MailboxFailure.AdmissionRefused, null, 0, 0, false, []));
            }
            if (Writes.Count == UnresolvedWriteAt)
            {
                return new(So6120DutyIoDisposition.Unresolved,
                    new(So6120MailboxFailure.Deadline, null, 1, 0, false, []));
            }
            Controls = mutation.Register switch
            {
                So6120MailboxRegister.StagedDuty1 => Controls with { Staged1 = mutation.Value },
                So6120MailboxRegister.StagedDuty2 => Controls with { Staged2 = mutation.Value },
                So6120MailboxRegister.StagedDuty3 => Controls with { Staged3 = mutation.Value },
                So6120MailboxRegister.Override => Controls with { Override = mutation.Value },
                So6120MailboxRegister.Selector => Controls with { Selector = mutation.Value },
                _ => throw new InvalidOperationException("Unexpected coordinator field"),
            };
            if (Controls.Selector == 0xFE && ApplyPwm)
            {
                State = State with { Actual1 = Controls.Staged1, Actual2 = Controls.Staged2, Actual3 = Controls.Staged3 };
            }
            OnWrite?.Invoke(mutation);
            return new(So6120DutyIoDisposition.Confirmed, new(So6120MailboxFailure.None, mutation.Value, 2, 2, true, []));
        }
        public bool RecordCheckpoint(So6120DutyCheckpoint checkpoint)
        {
            Events.Add("checkpoint");
            CheckpointCalls++;
            if (CheckpointCalls == FailCheckpointAt) { return false; }
            Checkpoints.Add(checkpoint);
            return true;
        }
        public bool RecordFence(DirectDutyOwner owner, So6120DutyFailure reason)
        {
            Events.Add("fence");
            Fences++;
            return FenceSucceeds;
        }
    }
}
