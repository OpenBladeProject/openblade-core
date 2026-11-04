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
    private sealed class Fixture
    {
        private bool latestRestoring;
        private int admissionCalls;
        private int intents;
        private readonly BatterySaverLowFanValidationRunner runner;

        internal Fixture(int maximumSamples = 3, bool useSubmissionGate = false)
        {
            Session = new StatefulSession();
            Session.BeforeWrite = count =>
            {
                WriteAdmissions.Add(latestRestoring);
                Events.Add($"Write:{count}");
            };
            runner = new(new ActorFactory(Session), CreateCatalog(), AdmitAsync, Record,
                TimeProvider.System, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5),
                sampleInterval: TimeSpan.Zero, maximumSamples: maximumSamples,
                honorFanInterCommandTiming: false,
                submissionAllowed: useSubmissionGate ? AdmitSubmission : null);
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
                    rpm = ClampActualCandidate ? 2000 : Targets[index];
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
                if (Writes.Count <= 4)
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
