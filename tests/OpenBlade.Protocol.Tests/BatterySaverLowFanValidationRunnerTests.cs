using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class BatterySaverLowFanValidationRunnerTests
{
    [Fact]
    public async Task PositiveRoundTrip_PreservesBatterySaverAndRestoresBothIndependentTargets()
    {
        var fixture = new Fixture();
        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.True(result.Success);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
        Assert.False(result.IndeterminatePartialWrite);
        Assert.Null(result.Failure);
        Assert.Equal(8, result.WritesAttempted);
        Assert.Equal(12, result.SafetyChecks);
        Assert.Equal(new FanRpmReadings(2400, 3100), result.BaselineTargets);
        Assert.Equal(new FanRpmReadings(2100, 2200), result.BaselineFanRpm);
        Assert.Equal(new FanRpmReadings(1500, 1500), result.CandidateTargets);
        Assert.Equal(result.Baseline, result.Restoration);
        Assert.Equal(result.BaselineTargets, result.RestorationTargets);
        Assert.Equal(3, result.Samples.Count);
        Assert.All(result.Samples, sample => Assert.Equal(new FanRpmReadings(1500, 1500), sample.Rpm));
        Assert.Equal(
            ["01010301", "01020301", "01010F", "01020F", "010118", "01021F", "01010300", "01020300"],
            fixture.Session.Writes);
        Assert.Equal([false, false, false, false, true, true, true, true], fixture.WriteAdmissions);
        Assert.Equal(8, fixture.Journal.Count(progress => progress.Stage == "WriteIntent"));
        Assert.Equal("Baseline", fixture.Events[0]);
        Assert.Equal(Enumerable.Range(1, 8).SelectMany(index => new[] { $"Intent:{index}", $"Write:{index}" }),
            fixture.Events.Skip(1));
        using JsonDocument serialized = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.True(serialized.RootElement.GetProperty("Restored").GetBoolean());
        Assert.Equal(3, serialized.RootElement.GetProperty("Samples").GetArrayLength());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedClamp_FailsExperimentAndStillRestores(bool storedTargetClamped)
    {
        var fixture = new Fixture();
        fixture.Session.ClampStoredCandidate = storedTargetClamped;
        fixture.Session.ClampActualCandidate = !storedTargetClamped;

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.False(result.Success);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
        Assert.False(result.IndeterminatePartialWrite);
        Assert.Equal(storedTargetClamped ? "StoredTargetClamped" : "ActualRpmDidNotSettleAt1500", result.Failure);
        Assert.Equal(3, result.Samples.Count);
        Assert.All(result.Samples, sample => Assert.Equal(new FanRpmReadings(2000, 2000), sample.Rpm));
        Assert.Equal(8, fixture.Session.Writes.Count);
    }

    [Fact]
    public async Task SamplesRetainOutliers_AndRequireThreeConsecutiveBothFanReadings()
    {
        var fixture = new Fixture(maximumSamples: 6);
        fixture.Session.TachSamples =
        [
            new(1500, 1500), new(1500, 1800), new(1500, 1500),
            new(1200, 1500), new(1500, 1500), new(1500, 1500),
        ];

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.False(result.Success);
        Assert.True(result.Restored);
        Assert.Equal(6, result.Samples.Count);
        Assert.Equal(1800, result.Samples[1].Rpm.Selector2Rpm);
        Assert.Equal(1200, result.Samples[3].Rpm.Selector1Rpm);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 1)]
    [InlineData(6, 0)]
    public async Task WrongBaseline_PerformsNoPreparatoryWrites(byte mode, byte fan)
    {
        var fixture = new Fixture();
        fixture.Session.SetMode(mode, fan);

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal("BaselineMustBeStableBatterySaverAutomatic", result.Failure);
        Assert.Equal(0, result.WritesAttempted);
        Assert.Empty(fixture.Session.Writes);
        Assert.False(result.ManualRecoveryRequired);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1900)]
    [InlineData(5500)]
    public async Task UnrestorableSavedTarget_AbortsWithZeroWrites(int target)
    {
        var fixture = new Fixture();
        fixture.Session.Targets[1] = target;

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal("BaselineTargetsNotRestorable", result.Failure);
        Assert.Empty(fixture.Session.Writes);
    }

    [Fact]
    public async Task BaselineMustRemainStableAcrossTwoIndependentPairReads()
    {
        var fixture = new Fixture();
        fixture.Session.ChangeBaselineAfterFirstPair = true;

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal("BaselineMustBeStableBatterySaverAutomatic", result.Failure);
        Assert.Empty(fixture.Session.Writes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AmbiguousCandidateWrite_StopsWithoutReplayOrRestore(int failedWrite)
    {
        var fixture = new Fixture();
        fixture.Session.FailWrite = failedWrite;

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal("CandidateWriteIndeterminate", result.Failure);
        Assert.True(result.IndeterminatePartialWrite);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.Restored);
        Assert.Equal(failedWrite, fixture.Session.Writes.Count);
    }

    [Theory]
    [InlineData(0x82)]
    [InlineData(0x81)]
    [InlineData(0x88)]
    public async Task IndependentCandidateReadFailure_StopsAllFurtherWrites(byte query)
    {
        var fixture = new Fixture();
        fixture.Session.FailQueryAfterWrite = 4;
        fixture.Session.FailedQuery = query;

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.True(result.ManualRecoveryRequired);
        Assert.True(result.IndeterminatePartialWrite);
        Assert.False(result.Restored);
        Assert.Equal(4, fixture.Session.Writes.Count);
    }

    [Fact]
    public async Task ModeMismatchAfterAcknowledgedWrites_IsAmbiguousAndNeverRestores()
    {
        var fixture = new Fixture();
        fixture.Session.IgnoreCandidateModes = true;

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal("CandidateModeReadAmbiguous", result.Failure);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(4, fixture.Session.Writes.Count);
    }

    [Fact]
    public async Task TemperatureAdmissionLossDuringSampling_RestoresUnderFreshRestorationGates()
    {
        var fixture = new Fixture();
        fixture.FailAdmissionOnCall = 6;

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal("SafetyAdmissionLost:CPU hot", result.Failure);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Empty(result.Samples);
        Assert.Equal([false, false, false, false, true, true, true, true], fixture.WriteAdmissions);
    }

    [Fact]
    public async Task RestorationAdmissionLoss_StopsWithoutAnyRestorationSubmission()
    {
        var fixture = new Fixture { FailRestorationAdmission = true };

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal("SafetyAdmissionLost:Ownership lost", result.Failure);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.IndeterminatePartialWrite);
        Assert.Equal(4, fixture.Session.Writes.Count);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task RestorationFailure_StopsWithoutContinuingOrReplay(int failedWrite)
    {
        var fixture = new Fixture();
        fixture.Session.FailWrite = failedWrite;

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal("RestoreWriteIndeterminate", result.Failure);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.Restored);
        Assert.Equal(failedWrite, fixture.Session.Writes.Count);
    }

    [Fact]
    public async Task RestorationReadMismatch_DoesNotClaimRestoration()
    {
        var fixture = new Fixture();
        fixture.Session.IgnoreRestoredTarget = true;

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal("RestorationNotConfirmed", result.Failure);
        Assert.False(result.Restored);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(8, fixture.Session.Writes.Count);
    }

    [Theory]
    [InlineData("Baseline", 0)]
    [InlineData("WriteIntent", 0)]
    [InlineData("WriteAcknowledged", 1)]
    public async Task DurableJournalFailure_StopsBeforeAnyFurtherSubmission(string stage, int writes)
    {
        var fixture = new Fixture { FailedJournalStage = stage };

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal("JournalFailed", result.Failure);
        Assert.Equal(writes, fixture.Session.Writes.Count);
        Assert.Equal(writes > 0, result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task CancellationWithPossibleSubmission_StopsWithoutCleanupWrites()
    {
        using var canceled = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.Session.CancelWrite = canceled;

        BatterySaverLowFanValidationResult result = await fixture.RunAsync(canceled.Token);

        Assert.Equal("Canceled", result.Failure);
        Assert.True(result.IndeterminatePartialWrite);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Single(fixture.Session.Writes);
    }

    [Fact]
    public async Task RunnerCannotRepeatTheExperiment()
    {
        var fixture = new Fixture();
        Assert.True((await fixture.RunAsync()).Success);
        BatterySaverLowFanValidationResult repeated = await fixture.RunAsync();
        Assert.Equal("AlreadyRun", repeated.Failure);
        Assert.Equal(8, fixture.Session.Writes.Count);
    }

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(5, 4, true)]
    public async Task AdmissionRevokedDuringDurableIntent_IsRejectedAtSubmissionWithoutReplayOrRestore(
        int deniedIntent, int actualWrites, bool restoring)
    {
        var fixture = new Fixture(useSubmissionGate: true) { RevokeSubmissionOnIntent = deniedIntent };

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal(restoring ? "RestoreWriteIndeterminate" : "CandidateWriteIndeterminate", result.Failure);
        Assert.Equal(actualWrites, fixture.Session.Writes.Count);
        Assert.Equal(actualWrites + 1, result.WritesAttempted);
        Assert.Single(fixture.Session.DeniedSubmissions);
        Assert.Equal(restoring, fixture.SubmissionChecks.Last());
        Assert.Equal(deniedIntent, fixture.Journal.Count(progress => progress.Stage == "WriteIntent"));
        Assert.False(result.Restored);
        Assert.True(result.ManualRecoveryRequired);
    }
    [Fact]
    public async Task SpinDown_ConfirmsPhysical2000BeforeTargetOnly1500_ThenRestoresExactBaseline()
    {
        var fixture = new Fixture(confirmSpinningAt2000: true);
        fixture.Session.PreparationTachSamples = [new(1800, 2200), new(2000, 1900), new(2100, 2000)];

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.True(result.Success);
        Assert.True(result.PreparationConfirmed);
        Assert.True(result.SpunDown);
        Assert.True(result.Restored);
        Assert.Equal("2000-to-1500-spin-down", result.Scenario);
        Assert.Equal(new FanRpmReadings(2000, 2000), result.PreparationTargets);
        Assert.Equal(3, result.PreparationSamples!.Count);
        Assert.Equal(3, fixture.Session.PreparationSamplesBefore1500);
        Assert.Equal(3, result.Samples.Count);
        Assert.All(result.Samples, sample => Assert.Equal(new FanRpmReadings(0, 0), sample.Rpm));
        Assert.Equal(result.BaselineTargets, result.RestorationTargets);
        Assert.Equal(result.Baseline, result.Restoration);
        Assert.Equal(17, result.SafetyChecks);
        Assert.Equal(
            ["01010301", "01020301", "010114", "010214", "01010F", "01020F",
                "010118", "01021F", "01010300", "01020300"], fixture.Session.Writes);
        Assert.Equal([false, false, false, false, false, false, true, true, true, true], fixture.WriteAdmissions);
        Assert.Equal(3, fixture.Journal.Count(progress => progress.Stage == "PreparationSample"));
        Assert.Single(fixture.Journal, progress => progress.Stage == "PreparationConfirmed");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2000, 0)]
    [InlineData(0, 2000)]
    [InlineData(1700, 2000)]
    public async Task SpinDown_PreparationMustPhysicallySpinBothFans_No1500OnKnownFailure(int first, int second)
    {
        var fixture = new Fixture(confirmSpinningAt2000: true);
        fixture.Session.PreparationTachSamples = [new(first, second)];

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal("PreparationRpmDidNotSettleAt2000", result.Failure);
        Assert.False(result.PreparationConfirmed);
        Assert.False(result.SpunDown);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(3, result.PreparationSamples!.Count);
        Assert.Empty(result.Samples);
        Assert.Null(result.Candidate);
        Assert.DoesNotContain("01010F", fixture.Session.Writes);
        Assert.DoesNotContain("01020F", fixture.Session.Writes);
        Assert.Equal(result.BaselineTargets, result.RestorationTargets);
        Assert.Equal(result.Baseline, result.Restoration);
        Assert.Equal(8, fixture.Session.Writes.Count);
    }

    [Fact]
    public async Task SpinDown_PreparationStoredClamp_RestoresWithout1500OrInferringPhysical2000()
    {
        var fixture = new Fixture(confirmSpinningAt2000: true);
        fixture.Session.ClampPreparationTarget = true;

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal("PreparationStoredTargetClamped", result.Failure);
        Assert.Equal(new FanRpmReadings(2500, 2500), result.PreparationTargets);
        Assert.False(result.PreparationConfirmed);
        Assert.True(result.Restored);
        Assert.Empty(result.PreparationSamples!);
        Assert.Empty(result.Samples);
        Assert.DoesNotContain("01010F", fixture.Session.Writes);
    }

    [Fact]
    public async Task SpinDown_RequiresThreeConsecutiveBothZeroReadings_AndRetainsNonzeroSamples()
    {
        var fixture = new Fixture(maximumSamples: 5, confirmSpinningAt2000: true);
        fixture.Session.TachSamples = [new(0, 0), new(0, 100), new(0, 0), new(0, 0), new(0, 0)];

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.True(result.Success);
        Assert.True(result.SpunDown);
        Assert.Equal(5, result.Samples.Count);
        Assert.Equal(100, result.Samples[1].Rpm.Selector2Rpm);
    }

    [Theory]
    [InlineData(1500, 1500)]
    [InlineData(0, 1500)]
    public async Task SpinDown_NonzeroActualRpmIsRecordedAsNotStopped_ThenRestored(int first, int second)
    {
        var fixture = new Fixture(maximumSamples: 30, confirmSpinningAt2000: true);
        fixture.Session.CandidateActual = new(first, second);

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.True(result.PreparationConfirmed);
        Assert.False(result.SpunDown);
        Assert.False(result.Success);
        Assert.Equal("FansDidNotSpinDownAt1500", result.Failure);
        Assert.Equal(30, result.Samples.Count);
        Assert.All(result.Samples, sample => Assert.Equal(new FanRpmReadings(first, second), sample.Rpm));
        Assert.True(result.Restored);
        Assert.Equal(result.BaselineTargets, result.RestorationTargets);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task SpinDown_KnownAdmissionLossRestoresOnlyFromIndependentManualReadback(int failedAdmission)
    {
        var fixture = new Fixture(confirmSpinningAt2000: true) { FailAdmissionOnCall = failedAdmission };

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        if (failedAdmission == 3)
        {
            // Only the first mode was changed: the channels disagree, so even a
            // known rejection cannot authorize a guessed restoration sequence.
            Assert.Equal("RestorePreconditionReadAmbiguous", result.Failure);
            Assert.True(result.ManualRecoveryRequired);
            Assert.Single(fixture.Session.Writes);
            Assert.False(result.Restored);
        }
        else
        {
            Assert.Equal("SafetyAdmissionLost:CPU hot", result.Failure);
            Assert.True(result.Restored);
            Assert.False(result.ManualRecoveryRequired);
            Assert.Equal(result.BaselineTargets, result.RestorationTargets);
        }
        Assert.False(result.SpunDown);
        if (failedAdmission <= 9)
        {
            Assert.DoesNotContain("01010F", fixture.Session.Writes);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task SpinDown_AmbiguousPreparationOrCandidateWrite_StopsWithoutRestoration(int failedWrite)
    {
        var fixture = new Fixture(confirmSpinningAt2000: true);
        fixture.Session.FailWrite = failedWrite;

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.True(result.ManualRecoveryRequired);
        Assert.True(result.IndeterminatePartialWrite);
        Assert.False(result.Restored);
        Assert.Equal(failedWrite, fixture.Session.Writes.Count);
    }

    [Theory]
    [InlineData(0x82)]
    [InlineData(0x81)]
    [InlineData(0x88)]
    public async Task SpinDown_AmbiguousPreparationGet_StopsWithout1500OrRestoration(byte query)
    {
        var fixture = new Fixture(confirmSpinningAt2000: true);
        fixture.Session.FailQueryAfterWrite = 4;
        fixture.Session.FailedQuery = query;

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.True(result.ManualRecoveryRequired);
        Assert.True(result.IndeterminatePartialWrite);
        Assert.False(result.Restored);
        Assert.False(result.PreparationConfirmed);
        Assert.Equal(4, fixture.Session.Writes.Count);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(5, false)]
    [InlineData(7, true)]
    public async Task SpinDown_NativeAdmissionRevocationBlocksPreparationCandidateAndRestore(int deniedIntent, bool restoring)
    {
        var fixture = new Fixture(useSubmissionGate: true, confirmSpinningAt2000: true)
        {
            RevokeSubmissionOnIntent = deniedIntent,
        };

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal(deniedIntent - 1, fixture.Session.Writes.Count);
        Assert.Single(fixture.Session.DeniedSubmissions);
        Assert.Equal(restoring, fixture.SubmissionChecks.Last());
        Assert.False(result.Restored);
        Assert.True(result.ManualRecoveryRequired);
    }

    [Theory]
    [InlineData("PreparationReadback")]
    [InlineData("PreparationSample")]
    [InlineData("PreparationConfirmed")]
    public async Task SpinDown_PreparationJournalFailureStopsBefore1500AndRestoration(string failedStage)
    {
        var fixture = new Fixture(confirmSpinningAt2000: true) { FailedJournalStage = failedStage };

        BatterySaverLowFanValidationResult result = await fixture.RunAsync();

        Assert.Equal("JournalFailed", result.Failure);
        Assert.Equal(4, fixture.Session.Writes.Count);
        Assert.False(result.Restored);
        Assert.True(result.ManualRecoveryRequired);
    }
    private sealed class Fixture
    {
        private bool latestRestoring;
        private int admissionCalls;
        private int intents;
        private readonly BatterySaverLowFanValidationRunner runner;

        internal Fixture(int maximumSamples = 3, bool useSubmissionGate = false,
            bool confirmSpinningAt2000 = false)
        {
            Session = new StatefulSession { SpinDownScenario = confirmSpinningAt2000 };
            Session.BeforeWrite = count =>
            {
                WriteAdmissions.Add(latestRestoring);
                Events.Add($"Write:{count}");
            };
            runner = new(new ActorFactory(Session), CreateCatalog(), AdmitAsync, Record,
                TimeProvider.System, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5),
                sampleInterval: TimeSpan.Zero, maximumSamples: maximumSamples,
                honorFanInterCommandTiming: false,
                submissionAllowed: useSubmissionGate ? AdmitSubmission : null,
                confirmSpinningAt2000: confirmSpinningAt2000);
        }

        internal StatefulSession Session { get; }
        internal List<LowFanValidationProgress> Journal { get; } = [];
        internal List<string> Events { get; } = [];
        internal List<bool> WriteAdmissions { get; } = [];
        internal List<bool> SubmissionChecks { get; } = [];
        internal bool AllowSubmission { get; private set; } = true;
        internal int? RevokeSubmissionOnIntent { get; init; }
        internal int? FailAdmissionOnCall { get; set; }
        internal bool FailRestorationAdmission { get; init; }
        internal string? FailedJournalStage { get; init; }

        internal Task<BatterySaverLowFanValidationResult> RunAsync(CancellationToken token = default) =>
            runner.RunAsync(token);

        private bool AdmitSubmission(bool restoring)
        {
            SubmissionChecks.Add(restoring);
            return AllowSubmission;
        }
        private Task<string?> AdmitAsync(bool restoring, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            latestRestoring = restoring;
            admissionCalls++;
            return Task.FromResult(restoring && FailRestorationAdmission ? "Ownership lost"
                : admissionCalls == FailAdmissionOnCall ? "CPU hot" : null);
        }

        private void Record(LowFanValidationProgress progress)
        {
            if (progress.Stage == FailedJournalStage)
            {
                throw new IOException("Simulated durable journal failure.");
            }
            Journal.Add(progress);
            if (progress.Stage == "Baseline")
            {
                Events.Add("Baseline");
            }
            if (progress.Stage == "WriteIntent")
            {
                Events.Add($"Intent:{++intents}");
                if (intents == RevokeSubmissionOnIntent)
                {
                    AllowSubmission = false;
                }
            }
        }
    }

    private static DeviceCommandCatalog CreateCatalog()
    {
        var target = new DeviceIdentity(TargetBladeDevice.Model, TargetBladeDevice.VendorId,
            TargetBladeDevice.ProductId, "4.01");
        return DeviceCommandCatalog.Create(target,
        [
            Captured(target, ProductionBatteryPort.FanMaximumStateFirstAction, 0x02, "01010001"),
            Captured(target, ProductionBatteryPort.FanMaximumStateSecondAction, 0x02, "01020001"),
            Captured(target, ProductionBatteryPort.FanMaximumSpeedFirstAction, 0x01, "010136"),
            Captured(target, ProductionBatteryPort.FanMaximumSpeedSecondAction, 0x01, "010236"),
        ]);
    }

    private static CapturedCommandFixture Captured(DeviceIdentity target, string action, byte id,
        string payload) => new(target, action, 0x0D, id, Convert.FromHexString(payload),
            Captured: true, Decoded: true, Applied: true, ReadBack: true, Restored: true);

    private sealed class ActorFactory(IRazerCommandSession session) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(
                new ValidationActor(session, new BatteryCommandClient(session, TimeSpan.FromSeconds(3))), null));
    }

    private sealed class StatefulSession : IRazerCommandSession
    {
        private readonly byte[][] thermal = [[1, 1, 3, 0], [1, 2, 3, 0]];
        private int pairedModeReads;
        private int tachSamples;
        private bool saw1500Target;
        internal bool SpinDownScenario { get; init; }
        internal bool ClampPreparationTarget { get; set; }
        internal FanRpmReadings[]? PreparationTachSamples { get; set; }
        internal FanRpmReadings CandidateActual { get; set; } = new(0, 0);
        internal int CompletedPreparationSamples { get; private set; }
        internal int? PreparationSamplesBefore1500 { get; private set; }
        internal int[] Targets { get; } = [2400, 3100];
        internal List<string> Writes { get; } = [];
        internal List<string> DeniedSubmissions { get; } = [];
        internal Action<int>? BeforeWrite { get; set; }
        internal int? FailWrite { get; set; }
        internal int? FailQueryAfterWrite { get; set; }
        internal byte FailedQuery { get; set; }
        internal bool ClampStoredCandidate { get; set; }
        internal bool ClampActualCandidate { get; set; }
        internal bool ChangeBaselineAfterFirstPair { get; set; }
        internal bool IgnoreCandidateModes { get; set; }
        internal bool IgnoreRestoredTarget { get; set; }
        internal CancellationTokenSource? CancelWrite { get; set; }
        internal FanRpmReadings[]? TachSamples { get; set; }

        internal void SetMode(byte mode, byte fan)
        {
            thermal[0] = [1, 1, mode, fan];
            thermal[1] = [1, 2, mode, fan];
        }

        public RazerCommandSessionDisposition Disposition { get; private set; } = RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = assignTransactionId();
            byte[] payload = command.Arguments.ToArray();
            Assert.Equal(0x0D, command.ClassId);
            int index = payload[1] - 1;
            if (command.CommandId is 0x82 or 0x81 or 0x88)
            {
                if (Writes.Count == FailQueryAfterWrite && command.CommandId == FailedQuery)
                {
                    return Failure();
                }
                if (command.CommandId == 0x82)
                {
                    byte[] response = thermal[index].ToArray();
                    if (index == 1 && ++pairedModeReads == 1 && ChangeBaselineAfterFirstPair)
                    {
                        SetMode(6, 0);
                    }
                    return Success(response);
                }
                if (command.CommandId == 0x81)
                {
                    Assert.Equal(new byte[] { 1, payload[1], 0 }, payload);
                    return Success([1, payload[1], checked((byte)(Targets[index] / 100))]);
                }
                int rpm;
                if (Writes.Count == 0)
                {
                    rpm = index == 0 ? 2100 : 2200;
                }
                else if (SpinDownScenario && !saw1500Target)
                {
                    FanRpmReadings sample = PreparationTachSamples is { } preparationSamples
                        ? preparationSamples[Math.Min(CompletedPreparationSamples, preparationSamples.Length - 1)]
                        : new(2000, 2000);
                    rpm = index == 0 ? sample.Selector1Rpm : sample.Selector2Rpm;
                    if (index == 1)
                    {
                        CompletedPreparationSamples++;
                    }
                }
                else if (TachSamples is { } samples)
                {
                    FanRpmReadings sample = samples[Math.Min(tachSamples, samples.Length - 1)];
                    rpm = index == 0 ? sample.Selector1Rpm : sample.Selector2Rpm;
                    if (index == 1)
                    {
                        tachSamples++;
                    }
                }
                else
                {
                    rpm = SpinDownScenario
                        ? index == 0 ? CandidateActual.Selector1Rpm : CandidateActual.Selector2Rpm
                        : ClampActualCandidate ? 2000 : Targets[index];
                }
                return Success([1, payload[1], checked((byte)(rpm / 100))]);
            }
            if (command.SubmissionAllowed?.Invoke() == false)
            {
                DeniedSubmissions.Add(Convert.ToHexString(payload));
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(
                    default, HidSessionFailureKind.ConflictBlocked));
            }
            Writes.Add(Convert.ToHexString(payload));
            BeforeWrite?.Invoke(Writes.Count);
            if (CancelWrite is { } canceled)
            {
                canceled.Cancel();
                throw new OperationCanceledException(canceled.Token);
            }
            if (Writes.Count == FailWrite)
            {
                return Failure();
            }
            if (command.CommandId == 0x02)
            {
                if (!IgnoreCandidateModes || Writes.Count > 4)
                {
                    thermal[index] = payload;
                }
            }
            else
            {
                Assert.Equal(0x01, command.CommandId);
                if (SpinDownScenario)
                {
                    if (payload[2] == 15)
                    {
                        PreparationSamplesBefore1500 ??= CompletedPreparationSamples;
                        saw1500Target = true;
                        Targets[index] = ClampStoredCandidate ? 2000 : 1500;
                    }
                    else if (Writes.Count <= 4)
                    {
                        Targets[index] = ClampPreparationTarget ? 2500 : payload[2] * 100;
                    }
                    else if (!IgnoreRestoredTarget)
                    {
                        Targets[index] = payload[2] * 100;
                    }
                }
                else if (Writes.Count <= 4)
                {
                    Targets[index] = ClampStoredCandidate ? 2000 : payload[2] * 100;
                }
                else if (!IgnoreRestoredTarget)
                {
                    Targets[index] = payload[2] * 100;
                }
            }
            return Success(payload);
        }

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }

        private static Task<HidSessionResult<ReadOnlyMemory<byte>>> Success(byte[] payload) =>
            Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));

        private static Task<HidSessionResult<ReadOnlyMemory<byte>>> Failure() =>
            Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout));
    }
}
