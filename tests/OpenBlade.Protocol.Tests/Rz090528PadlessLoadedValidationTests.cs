using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528PadlessLoadedValidationTests
{
    [Fact]
    public async Task QualificationUsesOnlyExternalReadsAndDeadlineExcludesCleanupResidency()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock);
        var run = Create(clock, telemetry);
        var source = new Source();
        Assert.True(await run.PrepareAsync(source, CancellationToken.None));
        Assert.Equal(1, source.Reads);
        Assert.Equal(3, run.Result!.Qualification.Count);
        Assert.True(run.Result.Qualification[1].ElapsedMilliseconds - run.Result.Qualification[0].ElapsedMilliseconds >= 2000);
        run.MarkApplyStarted();
        clock.Advance(TimeSpan.FromMilliseconds(700));
        run.MarkCandidateConfirmed();
        var result = await run.RunAsync(source, CancellationToken.None);
        Assert.True(result.Completed);
        Assert.Equal(5, result.Samples.Count);
        Assert.Null(result.Samples[^1].Fans);
        Assert.Equal(5, source.Reads);
        Assert.Equal(10000, result.ObservationElapsedMilliseconds);
        Assert.Equal(700, result.CandidateApplyMilliseconds);
        clock.Advance(TimeSpan.FromMilliseconds(600));
        run.MarkRestorationReadback();
        Assert.Equal(10600, run.Result!.ConfirmedCandidateToRestorationReadbackMilliseconds);
    }

    [Fact]
    public async Task EightSecondPeakAbortsWithoutAnotherProtocolSnapshot()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock) { HotAt = 11 };
        var run = Create(clock, telemetry);
        var source = new Source();
        Assert.True(await run.PrepareAsync(source, CancellationToken.None));
        run.MarkApplyStarted();
        run.MarkCandidateConfirmed();
        var result = await run.RunAsync(source, CancellationToken.None);
        Assert.False(result.Completed);
        Assert.Equal("ExperimentalTemperatureTripwire", result.Failure);
        Assert.Equal(8000, result.ObservationElapsedMilliseconds);
        Assert.Equal(5, source.Reads);
        Assert.Null(result.Samples[^1].Fans);
        Assert.True(result.PermitKnownStateRestore);
    }

    [Theory]
    [InlineData(71, 65, 0)]
    [InlineData(60, 73, 0)]
    [InlineData(60, 65, 4001)]
    public async Task EntryEnvelopeAndFreshnessRefuseBeforeProtocolRead(double cpu, int gpu, double age)
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock) { Cpu = cpu, Gpu = gpu, Age = age };
        var source = new Source();
        Assert.False(await Create(clock, telemetry).PrepareAsync(source, CancellationToken.None));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task SourceMustAdvanceAcrossPreflightReads()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var run = Create(clock, new Telemetry(clock) { Frozen = true });
        Assert.False(await run.PrepareAsync(new Source(), CancellationToken.None));
        Assert.Equal("CpuSourceDidNotAdvance", run.Result!.Failure);
    }

    [Theory]
    [InlineData("hot")]
    [InlineData("lost")]
    [InlineData("workload")]
    public async Task CandidateExternalAbortPrecedesAnyFurtherProtocolSnapshot(string fault)
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock);
        var run = Create(clock, telemetry);
        var source = new Source();
        Assert.True(await run.PrepareAsync(source, CancellationToken.None));
        run.MarkApplyStarted();
        run.MarkCandidateConfirmed();
        telemetry.Fault = fault;
        var result = await run.RunAsync(source, CancellationToken.None);
        Assert.False(result.Completed);
        Assert.True(result.PermitKnownStateRestore);
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task InsufficientRemainingBudgetNeverStartsAnotherSnapshot()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock);
        var run = Create(clock, telemetry);
        var source = new Source();
        Assert.True(await run.PrepareAsync(source, CancellationToken.None));
        run.MarkApplyStarted();
        run.MarkCandidateConfirmed();
        clock.Advance(TimeSpan.FromMilliseconds(8001));
        var result = await run.RunAsync(source, CancellationToken.None);
        Assert.False(result.Completed);
        Assert.True(result.PermitKnownStateRestore);
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task FinalEntryFaultMakesNoSetterAttempt()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock) { FailAt = 6 };
        var session = new Session();
        var runner = Runner(session, Create(clock, telemetry));
        var result = await runner.RunAsync(PerformanceFanValidationTarget.HyperBoostFromPerformance, CancellationToken.None);
        Assert.False(result.ApplyAttempted);
        Assert.Empty(session.Writes);
        Assert.True(runner.HardwareOpened && runner.DisposalConfirmed);
    }

    [Fact]
    public async Task JsonLossAfterFirstAcknowledgementDoesNotSplitPairAndAllowsKnownRestore()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock);
        var session = new Session { AfterWrite = count => { if (count == 1) { telemetry.Fault = "lost"; } } };
        var runner = Runner(session, Create(clock, telemetry));
        var result = await runner.RunAsync(PerformanceFanValidationTarget.HyperBoostFromPerformance, CancellationToken.None);
        Assert.False(result.Success);
        Assert.True(result.ApplyAcknowledged && result.CandidateConfirmed && result.Restored);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(new[] { "01010700", "01020700", "01010200", "01020200" }, session.Writes);
        Assert.True(runner.DisposalConfirmed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AmbiguousNativeSubmissionNeverReplaysOrRestores(int failAt)
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var session = new Session { FailWriteAt = failAt };
        var runner = Runner(session, Create(clock, new Telemetry(clock)));
        var result = await runner.RunAsync(PerformanceFanValidationTarget.HyperBoostFromPerformance, CancellationToken.None);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.Restored);
        Assert.Equal(failAt, session.Writes.Count);
    }

    [Fact]
    public async Task SecondChannelNativeGuardRefusalNeverReplaysPartialCandidate()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        bool isolated = true;
        var session = new Session { AfterWrite = count => { if (count == 1) { isolated = false; } } };
        var runner = new Rz090528PadlessHyperBoostValidation(new Actors(session), new Pad(),
            () => isolated, loaded: Create(clock, new Telemetry(clock)));
        var result = await runner.RunAsync(PerformanceFanValidationTarget.HyperBoostFromPerformance, CancellationToken.None);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.Restored);
        Assert.Single(session.Writes);
        Assert.Equal("01010700", session.Writes[0]);
        Assert.Empty(runner.LoadedResult!.Samples);
    }

    [Fact]
    public async Task CandidatePairAmbiguityStopsBeforeThermalObservationAndCleanup()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var session = new Session { DriftCandidate = true };
        var runner = Runner(session, Create(clock, new Telemetry(clock)));
        var result = await runner.RunAsync(PerformanceFanValidationTarget.HyperBoostFromPerformance, CancellationToken.None);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(2, session.Writes.Count);
        Assert.Empty(runner.LoadedResult!.Samples);
    }

    [Fact]
    public async Task FailedActorDisposalIsNotInferredFromRestoration()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var session = new Session { FailDispose = true };
        var runner = Runner(session, Create(clock, new Telemetry(clock)));
        _ = await runner.RunAsync(PerformanceFanValidationTarget.HyperBoostFromPerformance, CancellationToken.None);
        Assert.True(runner.HardwareOpened);
        Assert.False(runner.DisposalConfirmed);
    }

    [Fact]
    public void ReviewedPlanRejectsAnyByteChange()
    {
        byte[] plan = Rz090528PadlessLoadedValidation.ReadPlan();
        plan[0] ^= 1;
        Assert.Throws<InvalidOperationException>(() => Rz090528PadlessLoadedValidation.ValidatePlan(plan));
    }

    [Fact]
    public async Task StockWarmupCoolsBeforeThreeEligibleSamplesAndFinalAdmission()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock) { WarmupFrom = 3, WarmupThrough = 4 };
        var run = Create(clock, telemetry);
        var source = new Source();
        Assert.True(await run.PrepareAsync(source, CancellationToken.None));
        Assert.Equal(3, run.Result!.Qualification.Count);
        Assert.All(run.Result.Qualification, sample => Assert.Equal(60, sample.Reading.CpuCelsius));
        Assert.Equal(4000, run.Result.Qualification[0].ElapsedMilliseconds);
        Assert.Equal(1, source.Reads);
        Assert.Empty(run.Result.Samples);
    }

    [Fact]
    public async Task PerpetualStockWarmupTimesOutWithoutAnySetterAttempt()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock) { WarmupFrom = 3, WarmupThrough = int.MaxValue };
        var session = new Session();
        var runner = Runner(session, Create(clock, telemetry));
        var result = await runner.RunAsync(PerformanceFanValidationTarget.HyperBoostFromPerformance, CancellationToken.None);
        Assert.False(result.ApplyAttempted);
        Assert.Empty(session.Writes);
        Assert.Equal("WorkloadQualificationTimedOut", runner.LoadedResult!.Failure);
        Assert.Empty(runner.LoadedResult.Qualification);
    }

    [Fact]
    public async Task HotSampleCannotConcealLossOfPreviouslyBoundWorkload()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock) { WarmupFrom = 4, WarmupThrough = 4, LostAt = 4 };
        var run = Create(clock, telemetry);
        var source = new Source();
        Assert.False(await run.PrepareAsync(source, CancellationToken.None));
        Assert.Equal("BoundWorkloadLost", run.Result!.Failure);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task LateThirdQualificationCannotCrossArmingDeadline()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock) { AdvanceAt = 5, AdvanceBy = TimeSpan.FromSeconds(177) };
        var run = Create(clock, telemetry);
        Assert.False(await run.PrepareAsync(new Source(), CancellationToken.None));
        Assert.Equal("WorkloadQualificationTimedOut", run.Result!.Failure);
    }

    [Fact]
    public async Task StalledExternalReadAfterCandidateStillPermitsKnownStateRestoration()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock);
        var session = new Session { AfterWrite = count => { if (count == 1) { telemetry.Stall = true; } } };
        var runner = Runner(session, Create(clock, telemetry));
        var result = await runner.RunAsync(PerformanceFanValidationTarget.HyperBoostFromPerformance, CancellationToken.None);
        Assert.False(result.Success);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(4, session.Writes.Count);
    }

    [Fact]
    public async Task BlockedSnapshotPrefixCannotEnqueueAfterTimeoutAndActorDisposal()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var run = Create(clock, new Telemetry(clock));
        Assert.True(await run.PrepareAsync(new Source(), CancellationToken.None));
        run.MarkApplyStarted();
        run.MarkCandidateConfirmed();
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var native = new Session();
        var guarded = new Rz090528PadlessHyperBoostValidation.GuardedSession(native,
            new ValidationRunContext(), new Pad(), () => { entered.Set(); release.Wait(); return true; });
        try
        {
            Task<Rz090528LoadedResult> observation = run.RunAsync(guarded, CancellationToken.None);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
            var result = await observation;
            Assert.False(result.PermitKnownStateRestore);
            Assert.True(result.ObservationTimingUncertain);
            Assert.False(run.SnapshotQuiesced);
            await guarded.DisposeAsync();
            release.Set();
            for (int index = 0; index < 100 && !run.SnapshotQuiesced; index++) { await Task.Delay(10); }
            Assert.True(run.SnapshotQuiesced);
            Assert.Equal(0, native.Commands);
            Assert.Empty(native.Writes);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task SixtySecondLoadedProfileTakesThirtySamplesWithinItsTripwire()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock);
        var run = new Rz090528PadlessLoadedValidation(telemetry.Read, _ => null, clock: clock, delay: clock.Wait,
            profile: Rz090528LoadedProfile.SixtySecondLoaded);
        var source = new Source();
        Assert.True(await run.PrepareAsync(source, CancellationToken.None));
        run.MarkApplyStarted();
        run.MarkCandidateConfirmed();
        telemetry.Cpu = 84.9;
        telemetry.Gpu = 79;
        var result = await run.RunAsync(source, CancellationToken.None);
        Assert.True(result.Completed);
        Assert.Equal(30, result.Samples.Count);
        Assert.Equal(30, source.Reads);
        Assert.Null(result.Samples[^1].Fans);
        Assert.Equal(58000, result.Samples[^1].ElapsedMilliseconds);
        Assert.Equal(60000, result.ObservationElapsedMilliseconds);
    }

    [Theory]
    [InlineData(85, 70)]
    [InlineData(60, 80)]
    public async Task SixtySecondProfilesUseTheApprovedTripwire(double cpu, int gpu)
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock);
        var run = new Rz090528PadlessLoadedValidation(telemetry.Read, _ => null, clock: clock, delay: clock.Wait,
            profile: Rz090528LoadedProfile.SixtySecondLoaded);
        var source = new Source();
        Assert.True(await run.PrepareAsync(source, CancellationToken.None));
        run.MarkApplyStarted();
        run.MarkCandidateConfirmed();
        telemetry.Cpu = cpu;
        telemetry.Gpu = gpu;
        var result = await run.RunAsync(source, CancellationToken.None);
        Assert.False(result.Completed);
        Assert.Equal("ExperimentalTemperatureTripwire", result.Failure);
        Assert.True(result.PermitKnownStateRestore);
    }

    [Fact]
    public void TenSecondProfileKeepsTheOriginalSeventyFiveDegreeTripwire()
    {
        Assert.Equal(75, Rz090528LoadedProfile.TenSecondLoaded.AbortCpuCelsius);
        Assert.Equal(75, Rz090528LoadedProfile.TenSecondLoaded.AbortGpuCelsius);
        Assert.Equal(10000, Rz090528LoadedProfile.TenSecondLoaded.ObservationMilliseconds);
        Assert.Equal(Rz090528PadlessLoadedValidation.PlanSha256, Rz090528LoadedProfile.TenSecondLoaded.PlanSha256);
        Assert.NotEmpty(Rz090528PadlessLoadedValidation.ReadPlan(Rz090528LoadedProfile.SixtySecondLoaded));
        Assert.NotEmpty(Rz090528PadlessLoadedValidation.ReadPlan(Rz090528LoadedProfile.SixtySecondNoLoad));
        Assert.Throws<InvalidOperationException>(() => Rz090528PadlessLoadedValidation.ValidatePlan(
            Rz090528PadlessLoadedValidation.ReadPlan(), Rz090528LoadedProfile.SixtySecondLoaded));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SixtySecondProfilesRunEndToEndThroughTheGuardedSessionWithinTheTransactionBudget(bool loadedProfile)
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = loadedProfile ? new Telemetry(clock) : new Telemetry(clock) { Running = false, Utilization = 2 };
        var session = new Session();
        var loaded = new Rz090528PadlessLoadedValidation(telemetry.Read, _ => null, clock: clock, delay: clock.Wait,
            profile: loadedProfile ? Rz090528LoadedProfile.SixtySecondLoaded : Rz090528LoadedProfile.SixtySecondNoLoad);
        var runner = Runner(session, loaded);
        var result = await runner.RunAsync(PerformanceFanValidationTarget.HyperBoostFromPerformance, CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(new[] { "01010700", "01020700", "01010200", "01020200" }, session.Writes);
        Assert.True(loaded.Result!.Completed);
        Assert.Equal(30, loaded.Result.Samples.Count);
        Assert.Equal(29, loaded.Result.Samples.Count(sample => sample.Fans is not null));
        Assert.True(runner.DisposalConfirmed);
    }

    [Fact]
    public async Task NoLoadEntryAcceptsStoppedIdleFansButEverySnapshotInModeSevenNeedsSpinningFans()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock) { Running = false, Utilization = 0 };
        var run = new Rz090528PadlessLoadedValidation(telemetry.Read, _ => null, clock: clock, delay: clock.Wait,
            profile: Rz090528LoadedProfile.SixtySecondNoLoad);
        var source = new Source { FansAt = read => read == 1 ? new(0, 0) : new(2600, 2400) };
        Assert.True(await run.PrepareAsync(source, CancellationToken.None));
        run.MarkApplyStarted();
        run.MarkCandidateConfirmed();
        Assert.True((await run.RunAsync(source, CancellationToken.None)).Completed);
    }

    [Fact]
    public async Task NoLoadAllowsFirmwareStoppedFansInModeSeven()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock) { Running = false, Utilization = 0 };
        var run = new Rz090528PadlessLoadedValidation(telemetry.Read, _ => null, clock: clock, delay: clock.Wait,
            profile: Rz090528LoadedProfile.SixtySecondNoLoad);
        var source = new Source { FansAt = read => read % 3 == 0 ? new(0, 500) : new(0, 0) };
        Assert.True(await run.PrepareAsync(source, CancellationToken.None));
        run.MarkApplyStarted();
        run.MarkCandidateConfirmed();
        Assert.True((await run.RunAsync(source, CancellationToken.None)).Completed);
    }

    [Fact]
    public async Task NoLoadMissingFanReadbackInModeSevenAbortsToKnownRestore()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock) { Running = false, Utilization = 0 };
        var run = new Rz090528PadlessLoadedValidation(telemetry.Read, _ => null, clock: clock, delay: clock.Wait,
            profile: Rz090528LoadedProfile.SixtySecondNoLoad);
        var source = new Source { FansAt = read => read == 5 ? new(-1, 0) : new(0, 0) };
        Assert.True(await run.PrepareAsync(source, CancellationToken.None));
        run.MarkApplyStarted();
        run.MarkCandidateConfirmed();
        var result = await run.RunAsync(source, CancellationToken.None);
        Assert.False(result.Completed);
        Assert.Equal("FanReadbackInvalid", result.Failure);
        Assert.True(result.PermitKnownStateRestore);
    }

    [Fact]
    public async Task LoadedStoppedFanInModeSevenStillAborts()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock);
        var run = new Rz090528PadlessLoadedValidation(telemetry.Read, _ => null, clock: clock, delay: clock.Wait,
            profile: Rz090528LoadedProfile.SixtySecondLoaded);
        var source = new Source { FansAt = read => read >= 4 ? new(0, 1800) : new(2500, 2500) };
        Assert.True(await run.PrepareAsync(source, CancellationToken.None));
        run.MarkApplyStarted();
        run.MarkCandidateConfirmed();
        var result = await run.RunAsync(source, CancellationToken.None);
        Assert.False(result.Completed);
        Assert.Equal("FanNotPositive", result.Failure);
        Assert.True(result.PermitKnownStateRestore);
    }

    [Fact]
    public async Task LoadedEntryStillRequiresSpinningFans()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock);
        var run = new Rz090528PadlessLoadedValidation(telemetry.Read, _ => null, clock: clock, delay: clock.Wait,
            profile: Rz090528LoadedProfile.SixtySecondLoaded);
        var source = new Source { FansAt = _ => new(0, 0) };
        Assert.False(await run.PrepareAsync(source, CancellationToken.None));
        Assert.Equal("EntryFanNotPositive", run.Result!.Failure);
    }

    [Fact]
    public async Task NoLoadProfileAdmitsWithoutArmingAndCompletes()
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock) { Running = false, Utilization = 3 };
        int arms = 0;
        var run = new Rz090528PadlessLoadedValidation(telemetry.Read, _ => { arms++; return null; }, clock: clock,
            delay: clock.Wait, profile: Rz090528LoadedProfile.SixtySecondNoLoad);
        var source = new Source();
        Assert.True(await run.PrepareAsync(source, CancellationToken.None));
        Assert.Equal(0, arms);
        Assert.Empty(run.Result!.Qualification);
        Assert.Equal(1, source.Reads);
        run.MarkApplyStarted();
        run.MarkCandidateConfirmed();
        var result = await run.RunAsync(source, CancellationToken.None);
        Assert.True(result.Completed);
        Assert.Equal(30, result.Samples.Count);
    }

    [Theory]
    [InlineData(true, 3u)]
    [InlineData(false, 11u)]
    public async Task NoLoadProfileRefusesEntryWithBenchmarkOrBusyGpu(bool running, uint utilization)
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock) { Running = running, Utilization = utilization };
        var run = new Rz090528PadlessLoadedValidation(telemetry.Read, _ => null, clock: clock, delay: clock.Wait,
            profile: Rz090528LoadedProfile.SixtySecondNoLoad);
        var source = new Source();
        Assert.False(await run.PrepareAsync(source, CancellationToken.None));
        Assert.Equal("NoLoadConditionLost", run.Result!.Failure);
        Assert.Equal(0, source.Reads);
    }

    [Theory]
    [InlineData(true, 3u)]
    [InlineData(false, 11u)]
    public async Task NoLoadProfileAbortsToKnownRestoreWhenLoadAppears(bool running, uint utilization)
    {
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Telemetry(clock) { Running = false, Utilization = 3 };
        var run = new Rz090528PadlessLoadedValidation(telemetry.Read, _ => null, clock: clock, delay: clock.Wait,
            profile: Rz090528LoadedProfile.SixtySecondNoLoad);
        var source = new Source();
        Assert.True(await run.PrepareAsync(source, CancellationToken.None));
        run.MarkApplyStarted();
        run.MarkCandidateConfirmed();
        telemetry.Running = running;
        telemetry.Utilization = utilization;
        var result = await run.RunAsync(source, CancellationToken.None);
        Assert.False(result.Completed);
        Assert.Equal("NoLoadConditionLost", result.Failure);
        Assert.True(result.PermitKnownStateRestore);
    }

    private static Rz090528PadlessLoadedValidation Create(Rz090528PadlessNoLoadValidationTests.Clock clock, Telemetry telemetry) =>
        new(telemetry.Read, _ => null, clock: clock, delay: clock.Wait);
    private static Rz090528PadlessHyperBoostValidation Runner(Session session, Rz090528PadlessLoadedValidation loaded) =>
        new(new Actors(session), new Pad(), () => true, loaded: loaded);
    private sealed class Telemetry(Rz090528PadlessNoLoadValidationTests.Clock clock)
    {
        internal double Cpu = 60, Age;
        internal int Gpu = 65, FailAt, HotAt;
        internal bool Frozen, Stall;
        internal int AdvanceAt, WarmupFrom = int.MaxValue, WarmupThrough, LostAt;
        internal TimeSpan AdvanceBy;
        internal string? Fault;
        internal bool Running = true;
        internal uint Utilization = 90;
        private int reads;
        internal ValueTask<Rz090528LoadedReading> Read(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads++;
            if (Stall) { return new ValueTask<Rz090528LoadedReading>(new TaskCompletionSource<Rz090528LoadedReading>().Task); }
            if (reads == AdvanceAt) { clock.Advance(AdvanceBy); }
            return ValueTask.FromResult(new Rz090528LoadedReading(Frozen ? DateTimeOffset.UnixEpoch : clock.GetUtcNow(), Age,
                reads >= WarmupFrom && reads <= WarmupThrough ? 85 : Fault == "hot" || reads == HotAt ? 75 : Cpu, Gpu, Fault != "workload" && reads != LostAt && Running, "exact-bound-process", Utilization,
                Fault == "lost" || reads == FailAt ? "CpuJsonLost" : null));
        }
    }
    private sealed class Source : IRz090528NoLoadSnapshotSource
    {
        internal int Reads;
        internal Func<int, FanRpmReadings>? FansAt;
        public Task<Rz090528NoLoadSnapshot> ReadAsync(bool baseline, CancellationToken token)
        {
            Reads++;
            return Task.FromResult(new Rz090528NoLoadSnapshot(FansAt?.Invoke(Reads) ?? new(3000, 3000), null, true));
        }
    }
    private sealed class Pad : ICoolingPadDiscoverySource
    {
        public CoolingPadConnectionState Observe() => CoolingPadConnectionState.Absent;
    }
    private sealed class Actors(Session session) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(new ValidationActor(session,
                new BatteryCommandClient(session, TimeSpan.FromSeconds(3))), null));
    }
    private sealed class Session : IRazerCommandSession
    {
        internal List<string> Writes = [];
        internal Action<int>? AfterWrite;
        internal int FailWriteAt, Commands;
        internal bool DriftCandidate, FailDispose;
        private readonly byte[] modes = [2, 2];
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> transaction, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Commands++;
            _ = transaction();
            byte[] response;
            if (command.ClassId == 7) { response = [0x11, 0x11]; }
            else if (command.CommandId == 0x88) { response = [1, command.Arguments.Span[1], 30]; }
            else if (command.CommandId == 0x82)
            {
                byte channel = command.Arguments.Span[1];
                response = [1, channel, DriftCandidate && Writes.Count == 2 ? (byte)0 : modes[channel - 1], 0];
            }
            else
            {
                if (command.SubmissionAllowed?.Invoke() == false)
                {
                    return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.ConflictBlocked));
                }
                response = command.Arguments.ToArray();
                Writes.Add(Convert.ToHexString(response));
                modes[response[1] - 1] = response[2];
                AfterWrite?.Invoke(Writes.Count);
                if (Writes.Count == FailWriteAt)
                {
                    return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout));
                }
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
        }
        public ValueTask DisposeAsync() => FailDispose ? ValueTask.FromException(new IOException("Simulated disposal failure")) : ValueTask.CompletedTask;
    }
}
