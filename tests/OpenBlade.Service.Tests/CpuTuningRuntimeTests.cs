using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuTuningRuntimeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.CpuTuning.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("inactive")]
    [InlineData("low-power")]
    [InlineData("unverified")]
    [InlineData("unsupported")]
    [InlineData("no-recent-device")]
    public async Task ObservationAdmissionFailureSkipsCpuSourceAndPublication(string failure)
    {
        TestContext context = await CreateContextAsync(supported: failure != "unsupported",
            verified: failure != "unverified");
        if (failure == "low-power") { context.Gate.Enter(); }
        if (failure == "no-recent-device") { context.Provider.Available = false; }
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        int publications = context.Hub.Published.Count;

        ControlOperationResult result = await context.Runtime.ObserveCpuTuningAsync(true,
            () => failure != "inactive", CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, context.Source.Calls);
        Assert.Equal(before, context.Runtime.GetStatus());
        Assert.Equal(publications, context.Hub.Published.Count);
        Assert.Empty(context.Device.BatterySetterCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionOrLowPowerChangeAfterObservationPreventsPublication(bool suspend)
    {
        TestContext context = await CreateContextAsync();
        bool active = true;
        context.Source.AfterInspection = () =>
        {
            if (suspend) { context.Gate.Enter(); }
            else { active = false; }
        };
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        int publications = context.Hub.Published.Count;

        ControlOperationResult result = await context.Runtime.ObserveCpuTuningAsync(true,
            () => active, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(3, context.Source.Calls);
        Assert.Equal(before, context.Runtime.GetStatus());
        Assert.Equal(publications, context.Hub.Published.Count);
    }

    [Fact]
    public async Task PassiveObservationPublishesStatusWithoutAdmittingWritesOrRestoration()
    {
        TestContext context = await CreateContextAsync();

        ControlOperationResult observation = await context.Runtime.ObserveCpuTuningAsync(true,
            () => true, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, observation.Status);
        Assert.Equal(observation.CpuTuning, context.Runtime.GetStatus().CpuTuning);
        CpuTuningStatus status = Assert.IsType<CpuTuningStatus>(observation.CpuTuning);
        Assert.Null(status.LiveState);
        Assert.Equal(CpuTuningPendingBoot.Unknown, status.PendingBoot);
        Assert.False(status.RestorePending);
        Assert.False(status.Firmware.CanWrite);
        Assert.False(status.Live.CanWrite);
        int calls = context.Source.Calls;

        foreach (CpuTuningTarget target in Enum.GetValues<CpuTuningTarget>())
        {
            Assert.Equal(ControlOperationStatus.Blocked,
                (await context.Runtime.SetCpuTuningAsync(target, new(65, new(false, -17)),
                    () => true, CancellationToken.None)).Status);
            Assert.Equal(ControlOperationStatus.Blocked,
                (await context.Runtime.RestoreCpuTuningAsync(target,
                    () => true, CancellationToken.None)).Status);
        }
        Assert.Equal(calls, context.Source.Calls);
        Assert.Empty(context.Device.BatterySetterCalls);
    }

    [Theory]
    [InlineData(CpuTuningTarget.FirmwareNextBoot, true)]
    [InlineData(CpuTuningTarget.FirmwareNextBoot, false)]
    [InlineData(CpuTuningTarget.LiveNow, true)]
    [InlineData(CpuTuningTarget.LiveNow, false)]
    public async Task SetAndRestoreRemainBlockedWithoutCallingCpuSource(CpuTuningTarget target, bool active)
    {
        TestContext context = await CreateContextAsync();

        ControlOperationResult set = await context.Runtime.SetCpuTuningAsync(target,
            new(65, new(true, -1)), () => active, CancellationToken.None);
        ControlOperationResult restore = await context.Runtime.RestoreCpuTuningAsync(target,
            () => active, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, set.Status);
        Assert.Equal(ControlOperationStatus.Blocked, restore.Status);
        Assert.Equal(0, context.Source.Calls);
        Assert.Empty(context.Device.BatterySetterCalls);
        Assert.Null(context.Runtime.GetStatus().CpuTuning);
    }

    [Fact]
    public async Task AdmittedFakeSavedWorkflowPublishesOwnershipAndRestoresOriginalBytes()
    {
        TestContext context = await CreateContextAsync(admittedSavedBackend: true);
        byte[] baseline = (byte[])context.Source.Bytes!.Clone();
        ControlOperationResult saved = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(false, -10)), () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, saved.Status);
        Assert.True(context.Runtime.GetStatus().CpuTuning!.RestorePending);
        Assert.Equal(CpuTuningPendingBoot.Requested, saved.CpuTuning!.PendingBoot);
        Assert.Null(saved.CpuTuning.LiveState);
        ControlOperationResult restored = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.False(context.Runtime.GetStatus().CpuTuning!.RestorePending);
        Assert.Equal(baseline, context.Source.Bytes);
        Assert.Equal(2, context.Source.Submissions);
    }

    [Fact]
    public async Task CurveOnlyRecoveryIgnoresUnownedObservedOnlyPowerLimitAndCannotRestoreLive()
    {
        TestContext context = await CreateContextAsync(admittedSavedBackend: true, powerAdmitted: false);
        byte[] baseline = (byte[])context.Source.Bytes!.Clone();
        ControlOperationResult applied = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(true, -12)), () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, applied.Status);
        Assert.Equal(new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer), applied.CpuTuning!.FirmwareRecovery);
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.RestoreCpuTuningAsync(
            CpuTuningTarget.LiveNow, () => true, CancellationToken.None)).Status);
        Assert.Equal(1, context.Source.Submissions);
        ControlOperationResult restored = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.Equal(2, context.Source.Submissions);
        Assert.Equal(baseline, context.Source.Bytes);
    }

    [Fact]
    public async Task SessionLossAfterInitialGateStopsFakeSavedSubmission()
    {
        TestContext context = await CreateContextAsync(admittedSavedBackend: true);
        bool active = true;
        context.Source.BeforeIdentity = () => active = false;
        ControlOperationResult result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(false, -1)), () => active, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, context.Source.Submissions);
        Assert.False(result.CpuTuning!.RestorePending);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CompletedLowPowerBoundaryRevokesPendingSavedOperation(bool restoring, bool beforeSend)
    {
        TestContext context = await CreateContextAsync(admittedSavedBackend: true);
        if (restoring)
        {
            Assert.Equal(ControlOperationStatus.Stored, (await context.Runtime.SetCpuTuningAsync(
                CpuTuningTarget.FirmwareNextBoot, new(null, new(false, -10)), () => true, default)).Status);
        }
        byte[] before = (byte[])context.Source.Bytes!.Clone();
        int submissions = context.Source.Submissions;
        Action boundary = () =>
        {
            context.Source.BeforeIdentity = null;
            context.Source.BeforeSavedSend = null;
            Assert.True(context.Gate.Enter());
            Assert.True(context.Gate.Exit());
        };
        if (beforeSend) { context.Source.BeforeSavedSend = boundary; }
        else { context.Source.BeforeIdentity = boundary; }

        ControlOperationResult result = restoring
            ? await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot, () => true, default)
            : await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
                new(null, new(false, -10)), () => true, default);

        Assert.False(context.Gate.IsActive);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(submissions, context.Source.Submissions);
        Assert.Equal(before, context.Source.Bytes);
        // Final admission denial retains an already prepared restoration journal.
        Assert.Equal(restoring || beforeSend, result.CpuTuning!.RestorePending);

        if (beforeSend && !restoring)
        {
            // Retire the prepared, never-submitted journal before a new apply.
            Assert.Equal(ControlOperationStatus.Stored, (await context.Runtime.RestoreCpuTuningAsync(
                CpuTuningTarget.FirmwareNextBoot, () => true, default)).Status);
            Assert.Equal(submissions, context.Source.Submissions);
            Assert.Equal(before, context.Source.Bytes);
        }
        // A fresh operation may admit the new generation; the interrupted one cannot.
        ControlOperationResult next = restoring
            ? await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot, () => true, default)
            : await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
                new(null, new(false, -10)), () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, next.Status);
        Assert.Equal(submissions + 1, context.Source.Submissions);
    }

    [Fact]
    public async Task LiveCurveUsesRetainedOwnershipAndRestoresWithoutPowerAuthority()
    {
        var live = new LiveFixture(curve: true, power: false);
        var context = await CreateContextAsync(powerAdmitted: false, live: live);
        var first = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow, new(null, new(true, -2)), () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, first.Status);
        Assert.Equal(-2, first.CpuTuning!.LiveState!.UniformCurveOptimizerValue);
        Assert.Null(first.CpuTuning.LiveState.PowerLimits);
        Assert.Equal(new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer), first.CpuTuning.LiveRecovery);
        var original = live.CurveStore!.Journal!.OriginalBaseline;
        await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow, new(null, new(true, -3)), () => true, default);
        Assert.Equal(original, live.CurveStore.Journal!.OriginalBaseline);
        var restored = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow, () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.Equal(-1, restored.CpuTuning!.LiveState!.UniformCurveOptimizerValue);
        Assert.Equal(CpuTuningRecoveryState.None, restored.CpuTuning.LiveRecovery!.State);
        Assert.False(restored.CpuTuning.RestorePending);
        Assert.False(restored.CpuTuning.Live.CanWrite);
        Assert.Equal(0, context.Source.Calls);
    }

    [Fact]
    public async Task LivePowerRestoresBothExactFractionalOriginalsWithoutInventingCurveState()
    {
        var live = new LiveFixture(curve: false, power: true);
        var original = live.Power!.Pair;
        var context = await CreateContextAsync(live: live);
        var observed = await context.Runtime.ObserveCpuTuningAsync(true, () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, observed.Status);
        Assert.True(observed.CpuTuning!.IsWellFormed);
        Assert.Empty(observed.CpuTuning.LiveState!.CoreValues);
        Assert.Equal(new(null, original.Fast.Milliwatts, original.Slow.Milliwatts), observed.CpuTuning.LiveState.PowerLimits);
        Assert.Equal(observed.CpuTuning, context.Runtime.GetStatus().CpuTuning);
        Assert.False(observed.CpuTuning.Live.CanWrite);
        var applied = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow, new(45, null), () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, applied.Status);
        Assert.Equal(45000u, live.Power.Pair.Fast.Milliwatts);
        Assert.Equal(45000u, live.Power.Pair.Slow.Milliwatts);
        Assert.Equal(original.StapmRawBits, live.Power.Pair.StapmRawBits);
        Assert.True(applied.CpuTuning!.IsWellFormed);
        Assert.Empty(applied.CpuTuning.LiveState!.CoreValues);
        Assert.Equal(new(null, 45000, 45000), applied.CpuTuning.LiveState.PowerLimits);
        Assert.Equal(applied.CpuTuning, context.Runtime.GetStatus().CpuTuning);
        Assert.Equal(new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.PowerLimit), applied.CpuTuning.LiveRecovery);
        var restored = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow, () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.Equal(original, live.Power.Pair);
        Assert.Equal(4, live.Power.Sent.Count);
        Assert.Equal(CpuLivePowerOwnershipState.Restored, live.PowerStore!.Journal!.State);
        Assert.False(restored.CpuTuning!.RestorePending);
        Assert.True(restored.CpuTuning.IsWellFormed);
        Assert.Empty(restored.CpuTuning.LiveState!.CoreValues);
        Assert.Null(restored.CpuTuning.LiveState.UniformCurveOptimizerValue);
        Assert.Equal(new(null, original.Fast.Milliwatts, original.Slow.Milliwatts), restored.CpuTuning.LiveState.PowerLimits);
        Assert.Equal(restored.CpuTuning, context.Runtime.GetStatus().CpuTuning);
    }

    [Fact]
    public async Task CombinedIntentAndLaterSingleFamilyEditRestoreBothOriginalsThroughRuntime()
    {
        var live = new LiveFixture(curve: true, power: true);
        var context = await CreateContextAsync(live: live);
        var original = live.Power!.Pair;
        var combined = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow, new(45, new(true, -2)), () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, combined.Status);
        Assert.True(combined.CpuTuning!.IsWellFormed);
        Assert.Equal(-2, combined.CpuTuning.LiveState!.UniformCurveOptimizerValue);
        Assert.Equal(new(null, 45000, 45000), combined.CpuTuning.LiveState.PowerLimits);
        Assert.Equal(combined.CpuTuning, context.Runtime.GetStatus().CpuTuning);
        var second = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow, new(60, null), () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, second.Status);
        Assert.Equal(-2, second.CpuTuning!.LiveState!.UniformCurveOptimizerValue);
        Assert.Equal(ControlOperationStatus.Stored,
            (await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow, () => true, default)).Status);
        Assert.Equal(original, live.Power.Pair);
        Assert.All(live.Curve!.Values, core => Assert.Equal(-1, core.Offset));
        Assert.Equal(0, context.Source.Submissions);
    }

    [Fact]
    public async Task LiveObservationRequiresExplicitInstalledChoiceAndCannotAdmitWrites()
    {
        var live = new LiveFixture(curve: true, power: true);
        var context = await CreateContextAsync(live: live);
        await context.Runtime.ObserveCpuTuningAsync(false, () => true, default);
        Assert.Equal(0, live.Curve!.Reads); Assert.Equal(0, live.Power!.Reads);
        var observed = await context.Runtime.ObserveCpuTuningAsync(true, () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, observed.Status);
        Assert.True(observed.CpuTuning!.IsWellFormed);
        Assert.Equal(-1, observed.CpuTuning.LiveState!.UniformCurveOptimizerValue);
        Assert.Equal(live.Power.Pair.Fast.Milliwatts, observed.CpuTuning.LiveState.PowerLimits!.Message32Milliwatts);
        Assert.Equal(live.Power.Pair.Slow.Milliwatts, observed.CpuTuning.LiveState.PowerLimits.Message33Milliwatts);
        Assert.Null(observed.CpuTuning.LiveState.PowerLimits.EffectiveWatts);
        Assert.False(observed.CpuTuning.Live.CanWrite);
        Assert.Equal(CpuTuningRecoveryState.None, observed.CpuTuning.LiveRecovery!.State);
        Assert.Empty(live.Curve.Sent); Assert.Empty(live.Power.Sent);
    }

    [Fact]
    public async Task SuspendAndResumeInsideAdmissionCannotReviveTheOriginalRuntimeLease()
    {
        var live = new LiveFixture(curve: false, power: true);
        var context = await CreateContextAsync(live: live);
        live.Power!.Admit = (_, _) => { context.Gate.Enter(); context.Gate.Exit(); return true; };
        var result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow, new(45, null), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Empty(live.Power.Sent); Assert.Null(live.PowerStore!.Journal);
        Assert.False(result.CpuTuning!.RestorePending);
    }

    [Fact]
    public async Task CancelledPendingPowerSenderBlocksBothFamiliesAndLateCompletionCannotRestoreAuthority()
    {
        var live = new LiveFixture(curve: true, power: true);
        var context = await CreateContextAsync(live: live);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CpuLivePowerSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        live.Power!.OnSend = _ => { entered.SetResult(); return release.Task; };
        using var cancellation = new CancellationTokenSource();
        Task<ControlOperationResult> pending = context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            new(45, null), () => true, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        Assert.Equal(ControlOperationStatus.Failed, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(CpuLivePowerOwnershipState.PossibleSubmission, live.PowerStore!.Journal!.State);
        int reads = live.Power.Reads;
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            new(null, new(true, -2)), () => true, default)).Status);
        await context.Runtime.ObserveCpuTuningAsync(true, () => true, default);
        Assert.Equal(0, live.Curve!.Reads); Assert.Equal(reads, live.Power.Reads);
        release.SetResult(CpuLivePowerSubmission.Acknowledged);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow, () => true, default)).Status);
        Assert.Equal(CpuLivePowerOwnershipState.PossibleSubmission, live.PowerStore.Journal.State);
        Assert.Single(live.Power.Sent); Assert.Empty(live.Curve.Sent);
    }

    [Fact]
    public async Task SavedAndLiveRecoveryRemainSeparateWhenEitherTargetChanges()
    {
        var live = new LiveFixture(curve: true, power: false);
        var context = await CreateContextAsync(admittedSavedBackend: true, live: live);
        await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot, new(null, new(false, -1)), () => true, default);
        var applied = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow, new(null, new(true, -2)), () => true, default);
        Assert.Equal(CpuTuningPendingBoot.Requested, applied.CpuTuning!.PendingBoot);
        Assert.Equal(CpuTuningRecoveryState.RestoreReady, applied.CpuTuning.FirmwareRecovery!.State);
        var savedRestore = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot, () => true, default);
        Assert.Equal(CpuTuningRecoveryState.None, savedRestore.CpuTuning!.FirmwareRecovery!.State);
        Assert.Equal(CpuTuningRecoveryState.RestoreReady, savedRestore.CpuTuning.LiveRecovery!.State);
        Assert.True(savedRestore.CpuTuning.RestorePending);
        Assert.Equal(-2, savedRestore.CpuTuning.LiveState!.UniformCurveOptimizerValue);
        var liveRestore = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow, () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, liveRestore.Status);
        Assert.False(liveRestore.CpuTuning!.RestorePending);
        Assert.Equal(2, context.Source.Submissions);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInitialPrepareRetainsFamilyUncertaintyAcrossLiveOperationsButNotSaved(bool curveFailure)
    {
        var live = new LiveFixture(curve: true, power: true);
        var context = await CreateContextAsync(admittedSavedBackend: true, live: live);
        if (curveFailure) { live.CurveStore!.FailWrite = 1; }
        else { live.PowerStore!.FailWrite = 1; }
        var failed = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            curveFailure ? new(null, new(true, -2)) : new(45, null), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, failed.Status);
        Assert.Null(live.CurveStore!.Journal); Assert.Null(live.PowerStore!.Journal);
        var feature = curveFailure ? CpuTuningFeatures.CurveOptimizer : CpuTuningFeatures.PowerLimit;
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, feature), failed.CpuTuning!.LiveRecovery);
        Assert.True(failed.CpuTuning.RestorePending);
        Assert.True(failed.CpuTuning.IsWellFormed);
        int curveReads = live.Curve!.Reads, powerReads = live.Power!.Reads;
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            new(null, new(true, -2)), () => true, default)).Status);
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            new(45, null), () => true, default)).Status);
        var observed = await context.Runtime.ObserveCpuTuningAsync(true, () => true, default);
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, feature), observed.CpuTuning!.LiveRecovery);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow, () => true, default)).Status);
        Assert.Equal(curveReads, live.Curve.Reads); Assert.Equal(powerReads, live.Power.Reads);
        Assert.Empty(live.Curve.Sent); Assert.Empty(live.Power.Sent);
        var saved = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(false, -1)), () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, saved.Status);
        Assert.Equal(1, context.Source.Submissions);
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, feature), saved.CpuTuning!.LiveRecovery);
    }
    [Fact]
    public async Task SavedFailedPrepareRemainsBlockedAcrossRecreatedTransactionsAndReadOnlyObservation()
    {
        var journal = new FailingSavedStore();
        var context = await CreateContextAsync(admittedSavedBackend: true, savedJournal: journal);
        var failed = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(false, -1)), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, failed.Status);
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.CurveOptimizer), failed.CpuTuning!.FirmwareRecovery);
        Assert.True(failed.CpuTuning.RestorePending); Assert.True(failed.CpuTuning.IsWellFormed);
        Assert.Null(journal.Journal); Assert.Equal(0, context.Source.Submissions);
        journal.Fail = false;
        var observed = await context.Runtime.ObserveCpuTuningAsync(false, () => true, default);
        Assert.NotNull(observed.CpuTuning!.SavedConfiguration);
        Assert.Equal(failed.CpuTuning.FirmwareRecovery, observed.CpuTuning.FirmwareRecovery);
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(false, -1)), () => true, default)).Status);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot, () => true, default)).Status);
        Assert.Equal(1, journal.Writes); Assert.Equal(0, context.Source.Submissions);
    }

    private sealed class FailingSavedStore : ICpuTuningOwnershipJournalStore
    {
        internal bool Fail = true;
        internal int Writes;
        internal CpuTuningOwnershipJournal? Journal;
        private string? hash;
        public Task<CpuTuningOwnershipJournalLoadResult> LoadAsync(CancellationToken token) =>
            Task.FromResult(new CpuTuningOwnershipJournalLoadResult(Journal, false, hash));
        public Task<string?> TryWriteAsync(string? expectedSha256, CpuTuningOwnershipJournal journal, CancellationToken token)
        {
            Writes++;
            if (Fail || expectedSha256 != hash) { return Task.FromResult<string?>(null); }
            Assert.True(journal.IsWellFormed); Journal = journal; hash = Writes.ToString();
            return Task.FromResult<string?>(hash);
        }
    }

    private sealed class LiveFixture
    {
        internal readonly CpuLiveCurveTuningTransactionTests.MemoryStore? CurveStore;
        internal readonly CpuLiveCurveTuningTransactionTests.FakeBackend? Curve;
        internal readonly CpuLivePowerTuningTransactionTests.MemoryStore? PowerStore;
        internal readonly CpuLivePowerTuningTransactionTests.FakeBackend? Power;
        internal LiveFixture(bool curve, bool power)
        {
            if (curve) { CurveStore = new(); Curve = new(CurveStore); }
            if (power) { PowerStore = new(); Power = new(PowerStore); }
        }
    }
    private async Task<TestContext> CreateContextAsync(bool supported = true, bool verified = true,
        bool admittedSavedBackend = false, bool powerAdmitted = true, LiveFixture? live = null,
        ICpuTuningOwnershipJournalStore? savedJournal = null)
    {
        var store = new BatteryConfigurationStore(Path.Combine(directory, "profiles.json"),
            Path.Combine(directory, "preferences.json"), Path.Combine(directory, "commit.json"));
        BatteryConfiguration configuration = await store.LoadAsync(CancellationToken.None);
        bool writable = admittedSavedBackend || live is not null;
        var device = new BatteryTestDevice();
        device.Capabilities = device.Capabilities with
        {
            CpuPowerLimit = supported ? new(new(45, 95, 1), writable && powerAdmitted
                ? DeviceFeatureAccess.ReadWrite : DeviceFeatureAccess.ObservedOnly) : null,
            CpuVoltageOptimizer = writable ? new(new(-30, -1, 1), DeviceFeatureAccess.ReadWrite) : null,
        };
        var provider = new FakeProvider(device) { Available = verified };
        var source = new FakeObservationSource();
        if (admittedSavedBackend) { source.Bytes = CpuSavedTuningTransactionTests.Baseline(); }
        var gate = new LowPowerGate();
        var hub = new FakeHub();
        var runtime = new ServiceControlRuntime(store,
            new PersistentOverrideStore(Path.Combine(directory, "overrides.json")),
            new FakePowerState { Power = writable ? PowerSource.PluggedIn : PowerSource.Battery },
            hub, provider, new DiagnosticHistory(), gate,
            new FakePresenceMonitor(), new FakeFirmwareSource(),
            static (_, _) => Task.CompletedTask, ProfileSetValidator.CreateUnverifiedSafeDefaults(),
            cpuTuningController: new CpuTuningController(source,
                admittedSavedBackend ? savedJournal ?? new CpuTuningOwnershipJournalStore(Path.Combine(directory, "cpu.json")) : null,
                admittedSavedBackend ? safety => new FakeSavedBackend(source, safety) : null,
                live?.Curve, live?.CurveStore, live?.Power, live?.PowerStore));
        runtime.Initialize(configuration, writable ? PowerSource.PluggedIn : PowerSource.Battery);
        await runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(verified, runtime.GetStatus().DeviceVerified);
        return new(runtime, device, provider, source, gate, hub);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }

    private sealed record TestContext(ServiceControlRuntime Runtime, BatteryTestDevice Device,
        FakeProvider Provider, FakeObservationSource Source, LowPowerGate Gate, FakeHub Hub);

    private sealed class FakeObservationSource : ICpuTuningObservationSource
    {
        public int Calls { get; private set; }
        public Action? AfterInspection { get; set; }
        internal Action? BeforeIdentity;
        internal Action? BeforeSavedSend;
        internal byte[]? Bytes;
        internal int Submissions;
        public DeviceIdentity? ReadIdentity()
        {
            Calls++;
            BeforeIdentity?.Invoke();
            return new("RZ09-0528", 0x1532, 0x02C6, "2.02");
        }
        public SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity)
        {
            Calls++;
            if (Bytes is not null)
            {
                return new(SavedCpuFirmwareReadStatus.Observed, Rz090528CpuFirmwareCodec.DecodeSaved(Bytes),
                    Rz090528CpuFirmwareCodec.DecodeSaved(Bytes), 0, 0);
            }
            return new(SavedCpuFirmwareReadStatus.Unavailable, null, null, 5, 5);
        }
        public CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool useInstalledBackend)
        {
            Calls++;
            AfterInspection?.Invoke();
            return new(DeviceFeatureAccess.ReadWrite, CpuTuningBackendAvailability.Available);
        }
    }

    private sealed class FakeProvider(IBladeDevice device) : IVerifiedBladeDeviceProvider
    {
        public bool Available { get; set; } = true;
        public FirmwareCompatibilityStatus FirmwareCompatibility => FirmwareCompatibilityStatus.Tested;
        public bool RequiresUntestedFirmwareOverride => false;
        public void SetAllowUntestedFirmware(bool enabled) { }
        public bool TryGetVerifiedDevice(out IBladeDevice result)
        {
            result = device;
            return Available;
        }
    }

    private sealed class FakePowerState : IPowerStateProvider
    {
        internal PowerSource Power = PowerSource.Battery;
        public PowerSource CapturePowerSource() => Power;
        public int? CaptureBatteryPowerFlowMilliwatts() => null;
    }

    private sealed class FakeSavedBackend(FakeObservationSource source,
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> safety) : ICpuSavedTuningBackend
    {
        public Task<bool> CheckWriteAdmissionAsync(DeviceIdentity identity, CpuSavedTuningCommand command, CancellationToken token) =>
            Task.FromResult(safety(identity, command));
        public Task<SavedCpuFirmwareReadResult> ReadAsync(DeviceIdentity identity, CancellationToken token) =>
            Task.FromResult(source.ReadSaved(identity));
        public Task<CpuSavedTuningSubmission> SendAsync(DeviceIdentity identity, CpuSavedTuningCommand command, CancellationToken token)
        {
            source.BeforeSavedSend?.Invoke();
            if (!safety(identity, command)) { return Task.FromResult(CpuSavedTuningSubmission.NotSubmitted); }
            source.Bytes = CpuSavedTuningPlan.Simulate(source.Bytes!, command);
            source.Submissions++;
            return Task.FromResult(CpuSavedTuningSubmission.Acknowledged);
        }
    }

    private sealed class FakePresenceMonitor : IControllerPresenceMonitor
    {
        public ControllerPresenceObservation Observe() => new([], null);
    }

    private sealed class FakeFirmwareSource : IFirmwareVersionSource
    {
        public FirmwareVersions Capture() => FirmwareVersions.Unavailable();
    }

    private sealed class FakeHub : IStatusSubscriptionHub
    {
        public List<ServiceStatusSnapshot> Published { get; } = [];
        public void Publish(ServiceStatusSnapshot snapshot) => Published.Add(snapshot);
        public StatusSubscriptionLease Subscribe(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
