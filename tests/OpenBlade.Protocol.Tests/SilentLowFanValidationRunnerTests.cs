using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class SilentLowFanValidationRunnerTests
{
    [Fact]
    public async Task Stop1500_StableSpinningIsReportedWithoutFalseStopSuccess()
    {
        var fixture = new Fixture { Keep1500Spinning = true };
        var result = await fixture.Run(SilentLowFanScenario.FanStop1500);
        Assert.False(result.Success);
        Assert.True(result.Restored);
        Assert.Equal("FansDidNotStop", Assert.Single(result.Steps).Observation);
        Assert.Equal(30, result.Steps[0].Samples.Count);
    }

    [Fact]
    public async Task CancellationAfterPossibleSubmission_NeverRestoresOrReplays()
    {
        var fixture = new Fixture { CancelWrite = true };
        var result = await fixture.Run(SilentLowFanScenario.FanStop1500);
        Assert.Equal("Canceled", result.Failure);
        Assert.True(result.IndeterminatePartialWrite);
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task FailedRetirement_NeverOpensAnotherActor()
    {
        var fixture = new Fixture { FailRetirement = true };
        var result = await fixture.Run(SilentLowFanScenario.FanStop1500);
        Assert.Equal("ActorRetirementUnconfirmed", result.Failure);
        Assert.Equal(1, fixture.Opens);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task Stop1500_ConfirmsManualBeforeTargetsAndRestoresExactBaseline()
    {
        var fixture = new Fixture();
        var result = await fixture.Run(SilentLowFanScenario.FanStop1500);
        Assert.True(result.Success, result.Failure);
        Assert.True(result.Restored);
        Assert.Equal(new FanRpmReadings(2400, 3100), result.RestorationTargets);
        Assert.Equal("BothStopped", Assert.Single(result.Steps).Observation);
        Assert.Equal(new[] { "01010501", "01020501", "010114", "010214", "01010F", "01020F", "010118", "01021F", "01010500", "01020500" }, fixture.Writes);
        Assert.True(fixture.ManualReadBeforeFirstTarget);
        Assert.All(fixture.Writes, w => Assert.DoesNotContain(w, new[] { "010100", "010200", "010101", "010201" }));
    }

    [Fact]
    public async Task Calibration_WaitsTwentySecondsAndStopsOnDelayedSingleFanStop()
    {
        var fixture = new Fixture { DelayedSingleStop = true };
        var result = await fixture.Run(SilentLowFanScenario.LowestSpinning);
        Assert.True(result.Success, result.Failure);
        Assert.Equal(1700, result.LowestObservedSpinningTargetRpm);
        Assert.Equal("EitherStopped", result.Steps[^1].Observation);
        Assert.Equal(1600, result.Steps[^1].RequestedRpm);
        Assert.DoesNotContain(fixture.Writes, w => w == "01010F");
        Assert.All(result.Steps.Where(x => x.Observation == "StableSpinning"), x => Assert.True(x.Samples.Count >= 20));
        Assert.True(result.Steps[^1].Samples.Count >= 13);
    }

    [Theory]
    [InlineData("mode", 2)]
    [InlineData("target", 4)]
    [InlineData("tach", 4)]
    [InlineData("write", 3)]
    public async Task AmbiguousReadOrWrite_StopsAllSubsequentWrites(string fault, int expectedWrites)
    {
        var fixture = new Fixture { Fault = fault };
        var result = await fixture.Run(SilentLowFanScenario.FanStop1500);
        Assert.False(result.Success);
        Assert.True(result.IndeterminatePartialWrite);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(expectedWrites, fixture.Writes.Count);
    }

    [Fact]
    public async Task Clamp_StopsCandidatesAndRestoresKnownState()
    {
        var fixture = new Fixture { Clamp = true };
        var result = await fixture.Run(SilentLowFanScenario.LowestSpinning);
        Assert.False(result.Success);
        Assert.True(result.Restored);
        Assert.Equal("StoredTargetClamped", Assert.Single(result.Steps).Observation);
        Assert.DoesNotContain("010112", fixture.Writes);
    }

    [Fact]
    public async Task ThermalLoss_RestoresWithoutTrialTemperatureCeiling()
    {
        var fixture = new Fixture { LoseThermalAfterPreparation = true };
        var result = await fixture.Run(SilentLowFanScenario.LowestSpinning);
        Assert.False(result.Success);
        Assert.True(result.Restored, result.Failure);
        Assert.Contains(true, fixture.Admissions);
    }

    [Fact]
    public async Task JournalFailureBeforeFirstIntent_SubmitsNothing()
    {
        var fixture = new Fixture { FailIntent = true };
        var result = await fixture.Run(SilentLowFanScenario.FanStop1500);
        Assert.Equal("JournalFailed", result.Failure);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task BoundaryRevocationAfterIntent_DoesNotSubmitOrRestore()
    {
        var fixture = new Fixture { RevokeAfterIntent = true };
        var result = await fixture.Run(SilentLowFanScenario.FanStop1500);
        Assert.Empty(fixture.Writes);
        Assert.True(result.IndeterminatePartialWrite);
    }

    [Fact]
    public async Task AdapterLossAfterPreparation_DoesNotRestoreUnderUnknownPower()
    {
        var fixture = new Fixture { LoseAdapter = true };
        var result = await fixture.Run(SilentLowFanScenario.FanStop1500);
        Assert.Equal(4, fixture.Writes.Count);
        Assert.True(result.IndeterminatePartialWrite);
        Assert.False(result.Restored);
    }

    private sealed class Fixture : IValidationActorFactory
    {
        private readonly AdvancingTime time = new();
        private byte[] manual = [0, 0];
        private int[] targets = [2400, 3100];
        private int candidateSamples;
        private bool allow = true;
        internal string? Fault;
        internal bool Clamp, DelayedSingleStop, LoseThermalAfterPreparation, FailIntent, RevokeAfterIntent, LoseAdapter;
        internal bool Keep1500Spinning, CancelWrite, FailRetirement;
        internal int Opens;
        internal bool ManualReadBeforeFirstTarget;
        internal List<string> Writes { get; } = [];
        internal List<bool> Admissions { get; } = [];
        internal async Task<SilentLowFanValidationResult> Run(SilentLowFanScenario scenario)
        {
            var target = new DeviceIdentity(TargetBladeDevice.Model, TargetBladeDevice.VendorId, TargetBladeDevice.ProductId, "4.01");
            string[] actions = [ProductionBatteryPort.FanMaximumStateFirstAction, ProductionBatteryPort.FanMaximumStateSecondAction,
                ProductionBatteryPort.FanMaximumSpeedFirstAction, ProductionBatteryPort.FanMaximumSpeedSecondAction];
            string[] payloads = ["01010001", "01020001", "010136", "010236"];
            var catalog = DeviceCommandCatalog.Create(target, actions.Select((action, i) => new CapturedCommandFixture(target,
                action, 0x0D, (byte)(i < 2 ? 2 : 1), Convert.FromHexString(payloads[i]),
                Captured: true, Decoded: true, Applied: true, ReadBack: true, Restored: true)).ToArray());
            var runner = new SilentLowFanValidationRunner(this, catalog, (restoring, _) =>
            {
                Admissions.Add(restoring);
                return Task.FromResult<string?>(!restoring && LoseThermalAfterPreparation && Writes.Count >= 4 ? "Hot" : null);
            }, _ => allow, progress =>
            {
                if (progress.Stage == "WriteIntent" && FailIntent)
                {
                    throw new IOException();
                }

                if (progress.Stage == "WriteIntent" && RevokeAfterIntent)
                {
                    allow = false;
                }
            }, time, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), honorFanInterCommandTiming: false);
            return await runner.RunAsync(scenario, CancellationToken.None);
        }
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            Opens++;
            var session = new Session(this);
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(new ValidationActor(session,
                new BatteryCommandClient(session, TimeSpan.FromSeconds(3))), null));
        }
        private sealed class Session(Fixture owner) : IRazerCommandSession
        {
            public RazerCommandSessionDisposition Disposition { get; private set; } = RazerCommandSessionDisposition.Healthy;
            public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
                Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
            {
                _ = assignTransactionId();
                if (command.SubmissionAllowed?.Invoke() == false)
                {
                    return Failure();
                }

                var payload = command.Arguments.ToArray();
                if (command.ClassId == 7)
                {
                    return Success(owner.LoseAdapter && owner.Writes.Count >= 4 ? [0, 0x11] : [0x11, 0x11]);
                }

                int index = payload[1] - 1;
                if (command.CommandId == 0x82)
                {
                    if (owner.Fault == "mode" && owner.Writes.Count >= 2)
                    {
                        return Success([1, payload[1], 2, 1]);
                    }

                    if (owner.Writes.Count == 2 && index == 1)
                    {
                        owner.ManualReadBeforeFirstTarget = true;
                    }

                    return Success([1, payload[1], 5, owner.manual[index]]);
                }
                if (command.CommandId == 0x81)
                {
                    if (owner.Fault == "target" && owner.Writes.Count >= 4)
                    {
                        return Failure();
                    }

                    return Success([1, payload[1], (byte)(owner.targets[index] / 100)]);
                }
                if (command.CommandId == 0x88)
                {
                    if (owner.Fault == "tach" && owner.Writes.Count >= 4)
                    {
                        return Failure();
                    }

                    int target = owner.targets[index];
                    int rpm = target >= 2000 ? 2000 : target == 1500 && !owner.Keep1500Spinning ? 0 : target;
                    if (owner.DelayedSingleStop && target == 1600 && owner.candidateSamples >= 10 && index == 0)
                    {
                        rpm = 0;
                    }

                    if (index == 1)
                    {
                        owner.candidateSamples++;
                    }

                    return Success([1, payload[1], (byte)(rpm / 100)]);
                }
                owner.Writes.Add(Convert.ToHexString(payload));
                if (owner.CancelWrite)
                {
                    throw new OperationCanceledException();
                }
                if (owner.Fault == "write" && owner.Writes.Count == 3)
                {
                    return Failure();
                }

                if (command.CommandId == 2)
                {
                    owner.manual[index] = payload[3];
                }
                else
                {
                    owner.targets[index] = owner.Clamp && payload[2] == 19 ? 2000 : payload[2] * 100;
                    owner.candidateSamples = 0;
                }
                return Success(payload);
            }
            public ValueTask DisposeAsync()
            {
                if (!owner.FailRetirement)
                {
                    Disposition = RazerCommandSessionDisposition.FullyDisposed;
                }
                return ValueTask.CompletedTask;
            }
            private static Task<HidSessionResult<ReadOnlyMemory<byte>>> Success(byte[] value) => Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(value, null));
            private static Task<HidSessionResult<ReadOnlyMemory<byte>>> Failure() => Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout));
        }
    }
    private sealed class AdvancingTime : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new AdvancingTimer(this, callback, state, dueTime);
        private sealed class AdvancingTimer : ITimer
        {
            private readonly Timer timer;
            internal AdvancingTimer(AdvancingTime owner, TimerCallback callback, object? state, TimeSpan due)
            {
                timer = new Timer(_ => { Interlocked.Add(ref owner.ticks, due.Ticks); callback(state); }, null, 1, Timeout.Infinite);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => timer.Dispose();
            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }
}
