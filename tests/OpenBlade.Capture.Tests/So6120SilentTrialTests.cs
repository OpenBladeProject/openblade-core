using OpenBlade.Capture;
using OpenBlade.Contracts;
using Fixture = OpenBlade.Capture.Tests.So6120DirectDutySessionTests.Fixture;

namespace OpenBlade.Capture.Tests;

public sealed class So6120SilentTrialTests
{
    private static So6120SilentTrialHooks Hooks(Action<So6120SilentTrialProgress>? progress = null) => new(() => true, () => true, progress);
    private static So6120SilentTrialApproval Approve(So6120SilentTrialPlan plan) => new(plan.PlanId, plan.PlanSha256, Guid.NewGuid(), new('C', 64));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(91, 20)]
    public void StopTrialConfirmsTwoZeroSamplesAndRestoresSavedStaging(byte saved1, byte saved2)
    {
        using var f = StopFixture(saved1, saved2);
        using (var session = f.Create())
        {
            var plan = session.PrepareBalancedStop(Hooks());
            Assert.Equal(So6120TrialTargetPolicy.HalfCycleThenStop, plan.TargetPolicy);
            Assert.Equal((byte)91, plan.RequestedDuty1);
            Assert.NotEqual(plan.PlanSha256, (plan with { TargetPolicy = So6120TrialTargetPolicy.HalfCycle }).PlanSha256);
            Assert.Empty(f.Port.Writes);

            var result = session.ExecuteApproved(plan, Approve(plan));

            Assert.True(result.Success);
            Assert.True(result.StopStage!.ZeroTachObserved);
            Assert.NotNull(result.StopStage.ZeroPwmObservation);
            Assert.Equal(2, result.StopStage.Work.Writes.Count);
            Assert.All(result.StopStage.Work.Writes, write =>
                Assert.True(write.Transaction is { WriteConfirmed: true, CommandsCompleted: 2, Value: 0 }));
            Assert.Equal(new (byte Register, byte Value)[] { (0xB8, 0), (0xB9, 0), (0x1A, 0), (0xF0, 0x11) },
                f.Port.Writes.SkipWhile(write => write != (0x1A, 0xFE)).Skip(1).Take(4));
            Assert.Equal(saved1, f.Port.Mapped[0xB8]);
            Assert.Equal(saved2, f.Port.Mapped[0xB9]);
            Assert.True(result.Restoration!.CleanupConfirmed);
        }
        AssertCompleteStopJournal(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopTrialRequiresHalfCycleAndFreshNonzeroTachBeforeZero(bool failHalfCycle)
    {
        using var f = StopFixture();
        f.Port.UpdatePwm = !failHalfCycle;
        f.Port.AfterWrite += (register, value) =>
        {
            if (register == 0x1A && value == 0xFE) { f.Port.Mapped[0x09] = 0; }
        };
        using var session = f.Create();
        var plan = session.PrepareBalancedStop(Hooks());

        var result = session.ExecuteApproved(plan, Approve(plan));

        Assert.False(result.Success);
        Assert.Equal(failHalfCycle ? So6120SilentTrialStop.PwmNotObserved : So6120SilentTrialStop.SpinNotObserved, result.StopReason);
        Assert.DoesNotContain(f.Port.Writes, write => write is (0xB8 or 0xB9, 0));
        Assert.True(result.Restoration!.CleanupConfirmed);
    }

    [Theory]
    [InlineData("cancel", nameof(So6120SilentTrialStop.Cancelled))]
    [InlineData("unsubmitted", nameof(So6120SilentTrialStop.StopStageFailed))]
    [InlineData("thermal", nameof(So6120SilentTrialStop.ThermalRefused))]
    [InlineData("pwm", nameof(So6120SilentTrialStop.SpinNotObserved))]
    public void StopTrialUsesConfirmedFirstZeroForPartialAutoHandback(string stop, string reason)
    {
        using var f = StopFixture();
        using var cancellation = new CancellationTokenSource();
        bool thermal = true;
        f.Port.AfterWrite += (register, value) =>
        {
            if (register == 0x1A && value == 0) { f.Port.UnperformedSecondWrite = false; }
            if (register != 0xB8 || value != 0) { return; }
            switch (stop)
            {
                case "cancel": cancellation.Cancel(); break;
                case "unsubmitted": f.Port.UnperformedSecondWrite = true; break;
                case "thermal": thermal = false; break;
                case "pwm": f.Port.Mapped[0xB0] = 92; break;
            }
        };
        using (var session = f.Create())
        {
            var plan = session.PrepareBalancedStop(new(() => thermal, () => thermal));

            var result = session.ExecuteApproved(plan, Approve(plan), cancellation.Token);

            Assert.Equal(reason, result.StopReason.ToString());
            Assert.False(result.Success);
            Assert.True(result.Restoration!.CleanupConfirmed);
            Assert.Contains(((byte)0xB8, (byte)0), f.Port.Writes);
            Assert.DoesNotContain(((byte)0xB9, (byte)0), f.Port.Writes);
            Assert.True(result.StopStage!.Work.Writes[0].Transaction!.WriteConfirmed);
            Assert.Equal((byte)0, result.StopStage.Work.CleanupCheckpoint!.Expected.Staged1);
            Assert.Equal((byte)10, f.Port.Mapped[0xB8]);
            Assert.Equal((byte)20, f.Port.Mapped[0xB9]);
        }
        AssertCompleteStopJournal(f);
    }

    [Fact]
    public void PermissionCutoffAfterHalfCyclePreventsZeroButStillHandsBackAuto()
    {
        using var f = StopFixture();
        using var session = f.Create();
        var plan = session.PrepareBalancedStop(Hooks(progress =>
        {
            if (progress.Phase == "EntryFinished") { f.Clock.Advance(TimeSpan.FromSeconds(30)); }
        }));

        var result = session.ExecuteApproved(plan, Approve(plan));

        Assert.Equal(So6120SilentTrialStop.EntryBudget, result.StopReason);
        Assert.Null(result.StopStage);
        Assert.DoesNotContain(f.Port.Writes, write => write is (0xB8 or 0xB9, 0));
        Assert.True(result.Restoration!.CleanupConfirmed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmbiguousSecondZeroFencesWithoutAutoOrAnotherCommand(bool confirmation)
    {
        using var f = StopFixture();
        f.Port.AfterWrite += (register, value) =>
        {
            if (register == 0xB9 && value == 0 && !confirmation) { f.Port.AmbiguousFirstWrite = true; }
        };
        f.Port.AfterPublication = () =>
        {
            if (confirmation && f.Port.Mapped[0x1A] == 0xFE && f.Port.Mapped[0xB9] == 0
                && f.Port.Publications[^1] is { Command: 0xA0, Register: 0xB9 })
            { f.Clock.Advance(TimeSpan.FromSeconds(3)); }
        };
        using (var session = f.Create())
        {
            var plan = session.PrepareBalancedStop(Hooks());

            var result = session.ExecuteApproved(plan, Approve(plan));

            Assert.True(result.CommandsFenced);
            Assert.Null(result.Restoration);
            Assert.Null(result.StopStage!.Work.CleanupCheckpoint);
            Assert.Equal((confirmation ? (byte)0xA0 : (byte)0xA1, (byte)0xB9),
                (f.Port.Publications[^1].Command, f.Port.Publications[^1].Register));
        }
        var journal = So6120DirectDutyJournal.Inspect(f.Root);
        Assert.True(journal.Fenced);
        Assert.False(journal.Complete);
        Assert.False(journal.Corrupt);
    }

    [Theory]
    [InlineData(false, true, 11)]
    [InlineData(true, true, 8)]
    [InlineData(false, false, 11)]
    public void StopTrialCoastCapsNewReadsAndSeparatelyReportsZeroPwm(bool hitTimeLimit, bool pwmZero, int observations)
    {
        using var f = StopFixture();
        int zeroSelectors = 0;
        f.Port.AfterWrite += (register, value) =>
        {
            if (register == 0xB9 && value == 0)
            {
                f.Port.Mapped[0x09] = 1;
                if (!pwmZero) { f.Port.Mapped[0xB1] = 91; }
            }
        };
        f.Port.AfterPublication = () =>
        {
            var last = f.Port.Publications[^1];
            if (!hitTimeLimit || f.Port.Mapped[0x1A] != 0xFE || f.Port.Mapped[0xB9] != 0
                || last is not { Command: 0xA0, Register: 0x1A } || ++zeroSelectors != 2) { return; }
            int calls = 0;
            f.Source.OnCapture = () =>
            {
                if (++calls <= 12) { f.Clock.Advance(TimeSpan.FromMilliseconds(450)); }
            };
        };
        using (var session = f.Create())
        {
            var plan = session.PrepareBalancedStop(Hooks());

            var result = session.ExecuteApproved(plan, Approve(plan));

            Assert.Equal(So6120SilentTrialStop.ZeroTachNotObserved, result.StopReason);
            Assert.False(result.StopStage!.ZeroTachObserved);
            Assert.Equal(pwmZero, result.StopStage.ZeroPwmObservation is not null);
            Assert.Equal(observations, result.StopStage.Work.Observations.Count);
            Assert.True(result.Restoration!.CleanupConfirmed);
        }
        AssertCompleteStopJournal(f);
    }

    internal static Fixture StopFixture(byte saved1 = 10, byte saved2 = 20)
    {
        var f = new Fixture(PerformancePreset.Balanced, 0, preparing: true);
        f.Port.Mapped[0xB8] = saved1;
        f.Port.Mapped[0xB9] = saved2;
        f.Port.AfterWrite = (register, value) =>
        {
            if (f.Port.Mapped[0x1A] != 0xFE) { return; }
            switch (register)
            {
                case 0x1A when value == 0xFE:
                    f.Port.Mapped[0x08] = f.Port.Mapped[0x09] = 38;
                    break;
                case 0xB8 when value == 0:
                    f.Port.Mapped[0xB0] = f.Port.Mapped[0x08] = 0;
                    break;
                case 0xB9 when value == 0:
                    f.Port.Mapped[0xB1] = f.Port.Mapped[0x09] = 0;
                    break;
            }
        };
        return f;
    }

    private static void AssertCompleteStopJournal(Fixture fixture)
    {
        var journal = So6120DirectDutyJournal.Inspect(fixture.Root);
        Assert.True(journal.Complete);
        Assert.False(journal.Corrupt);
        Assert.InRange(journal.Records, 1, So6120DirectDutyJournal.MaximumRecords);
        Assert.InRange(new FileInfo(Path.Combine(fixture.Root, So6120DirectDutyJournal.FileName)).Length, 1,
            So6120DirectDutyJournal.MaximumFileBytes);
    }

    [Fact]
    public void PreparationIsReadOnlyUnplannedVersionTwoAndRejectsFixedEntry()
    {
        using var f = new Fixture(preparing: true);
        using (var session = f.Create())
        {
            var plan = session.PrepareSilentIncrease(Hooks());
            Assert.True(plan.AllowBoundedPwmDrift);
            Assert.Equal((byte)54, plan.RequestedDuty1);
            Assert.Equal((byte)64, plan.RequestedDuty2);
            Assert.Equal(13, f.Port.Publications.Count);
            Assert.All(f.Port.Publications, p => Assert.Equal(0xA0, p.Command));
            Assert.Empty(f.Port.Writes);
            Assert.Throws<InvalidOperationException>(() => session.Enter());
        }
        var inspected = So6120DirectDutyJournal.Inspect(f.Root);
        Assert.Equal(2, inspected.FormatVersion);
        Assert.False(inspected.Corrupt);
        Assert.False(inspected.Complete);
        Assert.Null(inspected.Identity!.RequestedDuty1);
        Assert.False(inspected.CanResume);
    }

    [Theory]
    [InlineData(PerformancePreset.Silent, 5, 0, 0)]
    [InlineData(PerformancePreset.Silent, 5, 50, 60)]
    [InlineData(PerformancePreset.Balanced, 0, 50, 60)]
    public void ExactApprovedTrialIncreasesAndRestoresWithoutInferringRpm(
        PerformancePreset profile, byte selector, int actual1, int actual2)
    {
        using var f = new Fixture(profile, selector, preparing: true);
        f.Port.Mapped[0xB0] = (byte)actual1;
        f.Port.Mapped[0xB1] = (byte)actual2;
        using (var session = f.Create())
        {
            var plan = profile == PerformancePreset.Balanced
                ? session.PrepareBalancedIncrease(Hooks())
                : session.PrepareSilentIncrease(Hooks());
            var result = session.ExecuteApproved(plan, Approve(plan));
            Assert.True(result.Success);
            Assert.False(result.NoSettingWritePublished);
            Assert.True(result.Restoration!.CleanupConfirmed);
            Assert.Equal(actual1 + 4, result.ObservationAfterDwell!.Snapshot.Actual1);
            Assert.Equal(actual2 + 4, result.ObservationAfterDwell.Snapshot.Actual2);
            Assert.Equal((byte)0, result.ObservationAfterDwell.Snapshot.Tach1);
            Assert.Equal((byte)0, result.ObservationAfterDwell.Snapshot.Tach2);
            Assert.Equal(selector, f.Port.Mapped[0x1A]);
            Assert.Equal((byte)0, f.Port.Mapped[0x36]);
            Assert.Equal(new byte[] { 0xFE, selector }, f.Port.Writes.Where(w => w.Register == 0x1A).Select(w => w.Value));
            Assert.DoesNotContain(f.Port.Writes, w => w.Register is 0x36 or 0x0A);
            Assert.Equal((byte)0x11, f.Port.Mapped[0xF0]);
            Assert.Equal((byte)10, f.Port.Mapped[0xB8]);
            Assert.Equal((byte)20, f.Port.Mapped[0xB9]);
            Assert.NotNull(result.PermissionStartedTimestamp);
            Assert.NotNull(result.PermissionRestoredTimestamp);
        }
        var inspected = So6120DirectDutyJournal.Inspect(f.Root);
        Assert.True(inspected.Complete);
        Assert.False(inspected.Corrupt);
        Assert.Equal(profile, inspected.Identity!.CapturedProfile);
        Assert.True(inspected.PermissionRestoredConfirmed);
        Assert.InRange(inspected.Records, 1, So6120DirectDutyJournal.MaximumRecords);
        Assert.InRange(new FileInfo(Path.Combine(f.Root, So6120DirectDutyJournal.FileName)).Length, 1, So6120DirectDutyJournal.MaximumFileBytes);
    }

    [Theory]
    [InlineData(PerformancePreset.Balanced, 5)]
    [InlineData(PerformancePreset.Balanced, 2)]
    [InlineData(PerformancePreset.Silent, 0)]
    public void DifferentProfileCannotProduceAPlan(PerformancePreset profile, byte observedSelector)
    {
        using var f = new Fixture(profile, observedSelector, preparing: true);
        using var session = f.Create();
        Assert.Throws<InvalidOperationException>(() => profile == PerformancePreset.Balanced
            ? session.PrepareBalancedIncrease(Hooks())
            : session.PrepareSilentIncrease(Hooks()));
        Assert.All(f.Port.Publications, publication => Assert.Equal(0xA0, publication.Command));
        Assert.Empty(f.Port.Writes);
    }

    [Fact]
    public void BalancedProfileDriftAfterApprovalCannotPublishASettingWrite()
    {
        using var f = new Fixture(PerformancePreset.Balanced, 0, preparing: true);
        using var session = f.Create();
        var plan = session.PrepareBalancedIncrease(Hooks());
        f.Port.Mapped[0x1A] = 5;
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.Equal(So6120DutyFailure.BaselineRejected, result.Entry!.Failure);
        Assert.True(result.NoSettingWritePublished);
        Assert.Null(result.Restoration);
        Assert.All(f.Port.Publications, publication => Assert.Equal(0xA0, publication.Command));
        Assert.Empty(f.Port.Writes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MissingHeadroomOrNonAutomaticBaselineCannotProducePlan(int change)
    {
        using var f = new Fixture(preparing: true);
        if (change == 0) { f.Port.Mapped[0xB0] = 180; }
        if (change == 1) { f.Port.Mapped[0x36] = 1; }
        if (change == 2) { f.Port.Mapped[0xF0] |= 0x40; }
        using var session = f.Create();
        Assert.Throws<InvalidOperationException>(() => session.PrepareSilentIncrease(Hooks()));
        Assert.Empty(f.Port.Writes);
    }

    [Fact]
    public void ChangedBaselineControlsRefuseWritesAndNeverRecalculate()
    {
        using var f = new Fixture(preparing: true);
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(Hooks());
        f.Port.Mapped[0xB8]++;
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.Equal(So6120DutyFailure.BaselineRejected, result.Entry!.Failure);
        Assert.False(result.Success);
        Assert.Null(result.Restoration);
        Assert.Empty(f.Port.Writes);
        Assert.Equal((byte)54, plan.RequestedDuty1);
        Assert.True(result.NoSettingWritePublished);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, -1)]
    [InlineData(false, 9)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    [InlineData(true, 9)]
    public void EitherFanOutsideTheIncreaseRangeRefusesInitialEntry(bool secondFan, int liveDelta)
    {
        using var f = new Fixture(PerformancePreset.Balanced, 0, preparing: true);
        using var session = f.Create();
        var plan = session.PrepareBalancedIncrease(Hooks());
        if (secondFan) { f.Port.Mapped[0xB1] = (byte)(plan.RequestedDuty2 - liveDelta); }
        else { f.Port.Mapped[0xB0] = (byte)(plan.RequestedDuty1 - liveDelta); }
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.Equal(So6120DutyFailure.BaselineRejected, result.Entry!.Failure);
        Assert.True(result.NoSettingWritePublished);
        Assert.Null(result.Restoration);
        Assert.All(f.Port.Publications, publication => Assert.Equal(0xA0, publication.Command));
    }

    [Fact]
    public void BoundedDriftAtEntryAndAfterPermissionUsesTheSameApprovedTargets()
    {
        using var f = new Fixture(PerformancePreset.Balanced, 0, preparing: true);
        using (var session = f.Create())
        {
            var plan = session.PrepareBalancedIncrease(Hooks());
            f.Port.Mapped[0xB0] = (byte)(plan.RequestedDuty1 - 1);
            f.Port.Mapped[0xB1] = (byte)(plan.RequestedDuty2 - 8);
            f.Port.AfterWrite = (register, value) =>
            {
                if (register != 0xF0 || value != 0x51) { return; }
                f.Port.Mapped[0xB0] = (byte)(plan.RequestedDuty1 - 8);
                f.Port.Mapped[0xB1] = (byte)(plan.RequestedDuty2 - 1);
            };

            var result = session.ExecuteApproved(plan, Approve(plan));
            Assert.True(result.Success);
            Assert.Equal(plan.RequestedDuty1, result.ObservationAfterDwell!.Snapshot.Actual1);
            Assert.Equal(plan.RequestedDuty2, result.ObservationAfterDwell.Snapshot.Actual2);
            Assert.Equal(new (byte Register, byte Value)[]
            {
                (0xB8, 54), (0xB9, 64), (0xF0, 0x51), (0x1A, 0xFE),
                (0x1A, 0), (0xF0, 0x11), (0xB8, 10), (0xB9, 20),
            }, f.Port.Writes);
            Assert.True(result.Restoration!.CleanupConfirmed);
        }
        var inspected = So6120DirectDutyJournal.Inspect(f.Root);
        Assert.True(inspected.Complete);
        Assert.False(inspected.Corrupt);
        Assert.Equal(2, inspected.FormatVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HalfCycleKeepsFixedOddCycleTargetsAsAutoPwmRises(bool driftBeforeEntry)
    {
        using var f = new Fixture(PerformancePreset.Balanced, 0, preparing: true);
        f.Port.Mapped[0xB0] = 0;
        f.Port.Mapped[0xB1] = 0;
        using (var session = f.Create())
        {
            var plan = session.PrepareBalancedHalfCycle(Hooks());
            Assert.Equal(So6120TrialTargetPolicy.HalfCycle, plan.TargetPolicy);
            Assert.False(plan.AllowBoundedPwmDrift);
            Assert.True(plan.RequiresPostIntentObservation);
            Assert.Equal((byte)91, plan.RequestedDuty1);
            Assert.Equal((byte)91, plan.RequestedDuty2);
            Assert.Empty(f.Port.Writes);

            if (driftBeforeEntry) { f.Port.Mapped[0xB0] = f.Port.Mapped[0xB1] = 38; }
            f.Port.AfterWrite = (register, value) =>
            {
                if (register == 0xF0 && value == 0x51) { f.Port.Mapped[0xB0] = f.Port.Mapped[0xB1] = 38; }
            };

            var result = session.ExecuteApproved(plan, Approve(plan));
            Assert.True(result.Success);
            Assert.Equal((byte)91, result.ObservationAfterDwell!.Snapshot.Actual1);
            Assert.Equal((byte)91, result.ObservationAfterDwell.Snapshot.Actual2);
            Assert.True(result.Restoration!.CleanupConfirmed);
            Assert.Equal(new (byte Register, byte Value)[]
            {
                (0xB8, 91), (0xB9, 91), (0xF0, 0x51), (0x1A, 0xFE),
                (0x1A, 0), (0xF0, 0x11), (0xB8, 10), (0xB9, 20),
            }, f.Port.Writes);
        }
        var inspected = So6120DirectDutyJournal.Inspect(f.Root);
        Assert.True(inspected.Complete);
        Assert.False(inspected.Corrupt);
        Assert.Equal(2, inspected.FormatVersion);
    }

    [Theory]
    [InlineData(false, 91)]
    [InlineData(false, 92)]
    [InlineData(true, 91)]
    [InlineData(true, 92)]
    public void HalfCycleRefusesEitherFanAtOrAboveTargetAfterPermission(bool secondFan, byte actual)
    {
        using var f = new Fixture(PerformancePreset.Balanced, 0, preparing: true);
        using var session = f.Create();
        var plan = session.PrepareBalancedHalfCycle(Hooks());
        f.Port.AfterWrite = (register, value) =>
        {
            if (register == 0xF0 && value == 0x51) { f.Port.Mapped[secondFan ? 0xB1 : 0xB0] = actual; }
        };
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.Equal(So6120SilentTrialStop.PwmNotObserved, result.StopReason);
        Assert.False(result.CommandsFenced);
        Assert.True(result.Restoration!.CleanupConfirmed);
        Assert.Equal(new (byte Register, byte Value)[]
        {
            (0xB8, 91), (0xB9, 91), (0xF0, 0x51), (0xF0, 0x11), (0xB8, 10), (0xB9, 20),
        }, f.Port.Writes);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(183, 91)]
    [InlineData(183, 92)]
    public void HalfCycleNeedsPositiveHeadroomAtPreparation(byte cycle, byte actual)
    {
        using var f = new Fixture(PerformancePreset.Balanced, 0, preparing: true);
        f.Port.Mapped[0xB2] = cycle;
        f.Port.Mapped[0xB0] = actual;
        f.Port.Mapped[0xB1] = 0;
        using var session = f.Create();
        Assert.Throws<InvalidOperationException>(() => session.PrepareBalancedHalfCycle(Hooks()));
        Assert.Empty(f.Port.Writes);
    }

    [Theory]
    [InlineData(0xB0, 91)]
    [InlineData(0xB1, 92)]
    [InlineData(0xB2, 182)]
    public void HalfCycleRefusesInitialTargetOrCycleDrift(byte register, byte value)
    {
        using var f = new Fixture(PerformancePreset.Balanced, 0, preparing: true);
        using var session = f.Create();
        var plan = session.PrepareBalancedHalfCycle(Hooks());
        f.Port.Mapped[register] = value;
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.Equal(So6120DutyFailure.BaselineRejected, result.Entry!.Failure);
        Assert.True(result.NoSettingWritePublished);
        Assert.Empty(f.Port.Writes);
    }

    [Fact]
    public void ForgedReconstructedOrExpiredPlanCannotExecute()
    {
        using var f = new Fixture(preparing: true);
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(Hooks());
        Assert.Throws<InvalidOperationException>(() => session.ExecuteApproved(plan with { }, Approve(plan)));
        Assert.Throws<InvalidOperationException>(() => session.ExecuteApproved(plan, Approve(plan) with { PlanSha256 = new('0', 64) }));
        f.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Throws<InvalidOperationException>(() => session.ExecuteApproved(plan, Approve(plan)));
        Assert.Empty(f.Port.Writes);
    }

    [Theory]
    [InlineData(0xB8)]
    [InlineData(0xF0)]
    public void CancellationAfterResolvedMutationStillConfirmsThenRestoresKnownPrefix(int stopRegister)
    {
        using var f = new Fixture(preparing: true);
        using var cancellation = new CancellationTokenSource();
        f.Port.AfterWrite = (register, _) => { if (register == stopRegister) { cancellation.Cancel(); } };
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(Hooks());
        var result = session.ExecuteApproved(plan, Approve(plan), cancellation.Token);
        Assert.Equal(So6120SilentTrialStop.Cancelled, result.StopReason);
        Assert.False(result.Success);
        Assert.True(result.CancelRequested);
        Assert.True(result.Restoration!.Success);
        Assert.False(result.Entry!.CommandsFenced);
        Assert.DoesNotContain(f.Port.Writes, w => w.Register == 0x1A && w.Value == 0xFE);
        Assert.Equal((byte)0x11, f.Port.Mapped[0xF0]);
        Assert.Equal((byte)10, f.Port.Mapped[0xB8]);
    }

    [Fact]
    public void ThermalRefusalAfterStagingIsKnownUnsubmittedAndAllowsPartialExit()
    {
        using var f = new Fixture(preparing: true);
        bool thermal = true;
        f.Port.BeforeFirstWritePublication = () => thermal = false;
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(new(() => true, () => thermal));
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.Equal(So6120SilentTrialStop.ThermalRefused, result.StopReason);
        Assert.Empty(f.Port.Writes);
        Assert.False(result.Entry!.CommandsFenced);
        Assert.True(result.Restoration!.Success);
    }

    [Fact]
    public void OptionalThermalRefreshRunsOnlyAfterAutoAndDoesNotRedefineConfirmedHandback()
    {
        using var f = new Fixture(preparing: true);
        int refresh = 0;
        bool restoredBeforeRefresh = false;
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(new(() =>
        {
            refresh++;
            if (refresh == 7)
            {
                restoredBeforeRefresh = f.Port.Mapped[0x1A] == 5 && f.Port.Mapped[0xF0] == 0x11;
                return false;
            }
            return true;
        }, () => true));
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.False(result.ThermalAfterRestoration);
        Assert.Equal(7, refresh);
        Assert.True(restoredBeforeRefresh);
        Assert.Equal(So6120SilentTrialStop.None, result.StopReason);
        Assert.True(result.Restoration!.Success);
        Assert.True(result.Success);
    }

    [Fact]
    public void ProgressFailureAfterEntryStillUsesApprovedExplicitHandback()
    {
        using var f = new Fixture(preparing: true);
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(Hooks(p => { if (p.Phase == "EntryFinished") { throw new IOException("synthetic output failure"); } }));
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.Equal(So6120SilentTrialStop.ProgressFailed, result.StopReason);
        Assert.True(result.Entry!.Success);
        Assert.True(result.Restoration!.Success);
        Assert.False(result.Success);
    }

    [Fact]
    public void AmbiguityCannotBecomeAutomaticCleanup()
    {
        using var f = new Fixture(preparing: true);
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(Hooks());
        f.Port.AmbiguousFirstWrite = true;
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.True(result.Entry!.CommandsFenced);
        Assert.Null(result.Restoration);
        Assert.Single(f.Port.Writes);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(160)]
    [InlineData(320)]
    public void RealisticMetadataCostFitsBudgetsAndFreshCachedThermalGates(int milliseconds)
    {
        using var f = new Fixture(preparing: true);
        f.Source.OnCapture = () => f.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        long thermalAt = 0;
        var hooks = new So6120SilentTrialHooks(() => { thermalAt = f.Clock.GetTimestamp(); return true; },
            () => f.Clock.GetElapsedTime(thermalAt) <= TimeSpan.FromSeconds(2));
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(hooks);
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.False(result.CommandsFenced);
        Assert.True(result.Restoration!.Success);
        Assert.True(result.Restoration.CleanupConfirmed);
        Assert.True(result.Success || result.StopReason == So6120SilentTrialStop.EntryBudget);
        Assert.True(f.Clock.GetElapsedTime(result.AutoStartedTimestamp!.Value, result.AutoCompletedTimestamp!.Value) < TimeSpan.FromSeconds(60));
        Assert.Equal((byte)0x11, f.Port.Mapped[0xF0]);
        Assert.Equal((byte)10, f.Port.Mapped[0xB8]);
        Assert.Equal((byte)20, f.Port.Mapped[0xB9]);
    }

    [Fact]
    public void DeadlineInsideConfirmationCannotMasqueradeAsSafeBudgetHandback()
    {
        using var f = new Fixture(preparing: true);
        bool advanced = false;
        f.Source.OnCapture = () =>
        {
            if (!advanced && f.Port.Writes.Count == 1) { advanced = true; f.Clock.Advance(TimeSpan.FromSeconds(46)); }
        };
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(Hooks());
        var result = session.ExecuteApproved(plan, Approve(plan));
        // Moving time inside an active A1 confirmation is an ambiguous deadline,
        // not a safe reserve handback. No retry/cleanup may hide that boundary.
        Assert.True(result.Entry!.CommandsFenced);
        Assert.Null(result.Restoration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9)]
    public void PwmOutsideTheRangeAfterPermissionSoftStopsAndRestoresConfirmedPrefix(int liveDelta)
    {
        using var f = new Fixture(preparing: true);
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(Hooks());
        f.Port.AfterWrite = (register, value) =>
        {
            if (register == 0xF0 && value == 0x51) { f.Port.Mapped[0xB1] = (byte)(plan.RequestedDuty2 - liveDelta); }
        };
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.Equal(So6120SilentTrialStop.PwmNotObserved, result.StopReason);
        Assert.False(result.CommandsFenced);
        Assert.True(result.Restoration!.Success);
        Assert.Equal(new (byte Register, byte Value)[]
        {
            (0xB8, 54), (0xB9, 64), (0xF0, 0x51), (0xF0, 0x11), (0xB8, 10), (0xB9, 20),
        }, f.Port.Writes);
    }

    [Fact]
    public void DwellPwmMismatchIsReportedSeparatelyFromSuccessfulAutoHandback()
    {
        using var f = new Fixture(preparing: true);
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(Hooks(p => { if (p.Phase == "EntryFinished") { f.Port.Mapped[0xB0]--; } }));
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.Equal(So6120SilentTrialStop.PwmNotObserved, result.StopReason);
        Assert.True(result.Entry!.Success);
        Assert.True(result.Restoration!.Success);
        Assert.False(result.Success);
        Assert.Equal((byte)53, result.ObservationAfterDwell!.Snapshot.Actual1);
    }

    [Fact]
    public void DefinitelyUnsubmittedDwellReadStillAllowsFreshConfirmedAutoHandback()
    {
        using var f = new Fixture(preparing: true);
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(Hooks(p => { if (p.Phase == "EntryFinished") { f.Port.FailNextReadUnperformed = true; } }));
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.Equal(So6120SilentTrialStop.ObservationFailed, result.StopReason);
        Assert.True(result.Entry!.Success);
        Assert.True(result.Restoration!.Success);
        Assert.False(result.CommandsFenced);
        Assert.False(result.Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateCancellationCompletesHandbackButNeverReportsTrialSuccess(bool duringAuto)
    {
        using var f = new Fixture(preparing: true);
        using var cancellation = new CancellationTokenSource();
        using var session = f.Create();
        f.Port.AfterWrite = (register, value) =>
        {
            if (duringAuto && register == 0x1A && value == 5) { cancellation.Cancel(); }
        };
        var plan = session.PrepareSilentIncrease(Hooks(progress =>
        {
            if (!duringAuto && progress.Phase == "DwellFinished") { cancellation.Cancel(); }
        }));
        var result = session.ExecuteApproved(plan, Approve(plan), cancellation.Token);
        Assert.True(result.Entry!.Success);
        Assert.True(result.Restoration!.Success);
        Assert.True(result.Restoration.CleanupConfirmed);
        Assert.True(result.CancelRequested);
        Assert.Equal(So6120SilentTrialStop.Cancelled, result.StopReason);
        Assert.False(result.Success);
        Assert.False(result.CommandsFenced);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(61)]
    [InlineData(121)]
    public void ResolvedEntryExpiryUsesIndependentAutoPhase(int elapsed)
    {
        using var f = new Fixture(preparing: true);
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(Hooks(p => { if (p.Phase == "EntryFinished") { f.Clock.Advance(TimeSpan.FromSeconds(elapsed)); } }));
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.True(result.Entry!.Success);
        Assert.False(result.Success);
        Assert.False(result.CommandsFenced);
        Assert.Equal(So6120SilentTrialStop.EntryBudget, result.StopReason);
        Assert.True(result.Restoration!.Success);
        Assert.NotNull(result.PermissionRestoredTimestamp);
    }

    [Fact]
    public void AutoPhaseExpiryStopsBeforeAnotherTransactionAndReportsIncompleteState()
    {
        using var f = new Fixture(preparing: true);
        using var session = f.Create();
        int publicationsAtAuto = 0;
        var plan = session.PrepareSilentIncrease(Hooks(p =>
        {
            if (p.Phase == "Restoring")
            { publicationsAtAuto = f.Port.Publications.Count; f.Clock.Advance(TimeSpan.FromSeconds(60)); }
        }));
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.Equal(So6120SilentTrialStop.AutoBudget, result.StopReason);
        Assert.False(result.Success);
        Assert.False(result.Restoration!.CleanupConfirmed);
        Assert.Equal(publicationsAtAuto, f.Port.Publications.Count);
        Assert.Null(result.PermissionRestoredTimestamp);
        Assert.False(result.NoSettingWritePublished);
    }

    [Fact]
    public void NearAutoCutoffDoesNotShrinkAlreadyAdmittedA1ConfirmationTimeout()
    {
        using var f = new Fixture(preparing: true);
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(Hooks(p =>
        { if (p.Phase == "Restoring") { f.Clock.Advance(TimeSpan.FromSeconds(59)); } }));
        f.Port.AfterWrite = (register, value) =>
        { if (register == 0x1A && value == 5) { f.Clock.Advance(TimeSpan.FromMilliseconds(1500)); } };
        var result = session.ExecuteApproved(plan, Approve(plan));
        var write = Assert.Single(result.Restoration!.Writes);
        Assert.True(write.Transaction!.WriteConfirmed);
        Assert.Equal(2, write.Transaction.CommandsCompleted);
        // The already-admitted core finishes with its full 2s budget. The next
        // full-state observation is refused by Auto's cap, so cleanup remains
        // unresolved rather than mislabeling the byte confirmation itself.
        Assert.Equal(So6120SilentTrialStop.AutoBudget, result.StopReason);
        Assert.True(result.CommandsFenced);
        Assert.Null(result.PermissionRestoredTimestamp);
    }

    [Fact]
    public void EntryPwmNotObservedKeepsSpecificStopAndRestores()
    {
        using var f = new Fixture(preparing: true);
        f.Port.UpdatePwm = false;
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(Hooks());
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.Equal(So6120DutyFailure.PwmNotObserved, result.Entry!.Failure);
        Assert.Equal(So6120SilentTrialStop.PwmNotObserved, result.StopReason);
        Assert.True(result.Restoration!.Success);
    }

    [Fact]
    public void PreEntryThermalRefusalIsKnownNoSettingWrite()
    {
        using var f = new Fixture(preparing: true);
        bool allowed = true;
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(new(() => allowed, () => allowed));
        allowed = false;
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.Equal(So6120SilentTrialStop.ThermalRefused, result.StopReason);
        Assert.True(result.NoSettingWritePublished);
        Assert.Empty(f.Port.Writes);
    }

    [Fact]
    public void AmbiguousA0DoesNotClaimKnownNoSettingWrite()
    {
        using var f = new Fixture(preparing: true);
        using var session = f.Create();
        var plan = session.PrepareSilentIncrease(Hooks());
        f.Port.AfterPublication = () => f.Clock.Advance(TimeSpan.FromSeconds(3));
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.True(result.CommandsFenced);
        Assert.False(result.NoSettingWritePublished);
        Assert.Empty(f.Port.Writes);
    }

    [Theory]
    [InlineData(0)] // Resolved pre-mutation observation after a confirmed prefix.
    [InlineData(1)] // Mandatory post-A1 full-state confirmation.
    [InlineData(2)] // Dwell observation after complete entry.
    public void AggregateSnapshotDeadlinePreservesResolvedReadsButNotMissingWriteConfirmation(int phase)
    {
        using var f = new Fixture(preparing: true);
        using var session = f.Create(options: new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)));
        int sinceWrite = 0, remaining = 0;
        bool armed = false;
        var plan = session.PrepareSilentIncrease(Hooks(p =>
        { if (phase == 2 && p.Phase == "EntryFinished") { remaining = 26; } }));
        f.Source.OnCapture = () =>
        {
            if (phase != 2 && f.Port.Writes.Count == 1 && !armed)
            {
                sinceWrite++;
                // Two captures finish the distinct A0 byte confirmation;
                // another 26 finish its mandatory full-state checkpoint.
                if (sinceWrite == (phase == 1 ? 3 : 29)) { armed = true; remaining = 26; }
            }
            if (remaining > 0) { remaining--; f.Clock.Advance(TimeSpan.FromMilliseconds(80)); }
        };
        var result = session.ExecuteApproved(plan, Approve(plan));
        Assert.False(result.Success);
        if (phase == 1)
        {
            Assert.True(result.CommandsFenced);
            Assert.Null(result.Restoration);
            Assert.Single(f.Port.Writes);
        }
        else
        {
            Assert.False(result.CommandsFenced);
            Assert.True(result.Restoration!.Success);
            Assert.Equal((byte)10, f.Port.Mapped[0xB8]);
        }
    }
}
