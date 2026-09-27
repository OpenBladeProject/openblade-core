using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528PadlessHyperBoostValidationTests
{
    [Fact]
    public void PlanHashRejectsTampering()
    {
        byte[] plan = Rz090528PadlessHyperBoostValidation.ReadPlan();
        plan[0] ^= 1;
        Assert.Throws<InvalidOperationException>(() => Rz090528PadlessHyperBoostValidation.ValidatePlan(plan));
    }

    [Fact]
    public async Task ImmediateSequenceHasTwoAbsentSamplesAndTwoBaselinePairs()
    {
        var session = new Session();
        var runner = Create(session, new Pad());
        var result = await Run(runner);
        Assert.True(result.Success);
        Assert.True(runner.FirstAbsenceConfirmed && runner.SecondAbsenceConfirmed);
        Assert.True(runner.AbsenceSampleElapsedMilliseconds >= 500);
        Assert.Equal(new[] { "01010700", "01020700", "01010200", "01020200" }, session.Writes);
        Assert.Equal(4, session.QueriesAtFirstWrite);
        Assert.Equal(4, session.PowerQueries);
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.PresentExact)]
    [InlineData(CoolingPadConnectionState.Ambiguous)]
    [InlineData(CoolingPadConnectionState.ObservationFailed)]
    public async Task PresentOrUncertainPadNeverOpens(CoolingPadConnectionState state)
    {
        var session = new Session();
        Assert.False((await Run(Create(session, new Pad { State = state }))).Success);
        Assert.Equal(0, session.Queries);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task BaselineDriftBeforeFirstSetterNeverWrites()
    {
        var session = new Session { DriftAtQuery = 4 };
        Assert.False((await Run(Create(session, new Pad()))).Success);
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task LostAbsenceBeforeEnableNeverWrites(int observation)
    {
        var session = new Session();
        Assert.False((await Run(Create(session, new Pad { PresentAtObservation = observation }))).Success);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task PadArrivalBetweenEnableChannelsStopsWithoutBlindRestore()
    {
        var pad = new Pad();
        var session = new Session { AfterWrite = count => { if (count == 1) { pad.State = CoolingPadConnectionState.PresentExact; } } };
        var runner = Create(session, pad);
        var result = await Run(runner);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Single(session.Writes);
        Assert.Equal(new PerformanceFanThermalPair("01010700", "01020200"), runner.FailureReconciliation);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task PowerFailureBeforeEachSetterStopsTheSequence(int failAt)
    {
        var session = new Session { RejectPowerAt = failAt };
        Assert.False((await Run(Create(session, new Pad()))).Success);
        Assert.Equal(failAt - 1, session.Writes.Count);
    }

    [Fact]
    public async Task PadReappearanceAfterCandidateDoesNotBlockPerformanceEscape()
    {
        var pad = new Pad();
        var session = new Session { AfterWrite = writes => { if (writes == 2) { pad.State = CoolingPadConnectionState.PresentExact; } } };
        Assert.True((await Run(Create(session, pad))).Success);
        Assert.Equal(4, session.Writes.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AnyPartialSubmissionStopsAllFurtherWrites(int failAt)
    {
        var session = new Session { FailWriteAt = failAt };
        var result = await Run(Create(session, new Pad()));
        Assert.False(result.Success);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(failAt, session.Writes.Count);
    }

    [Fact]
    public async Task CancellationAfterConfirmedCandidateUsesFreshIndependentRestoration()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new Session { AfterQuery = count => { if (count == 6) { cancellation.Cancel(); } } };
        var result = await Create(session, new Pad()).RunAsync(
            PerformanceFanValidationTarget.HyperBoostFromPerformance, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.Restored);
        Assert.Equal(4, session.Writes.Count);
        Assert.Equal(10, session.Queries);
    }

    [Fact]
    public async Task DriftBeforeIndependentRestoreStopsWithoutEscapeWrite()
    {
        var session = new Session { DriftAtQuery = 7 };
        var result = await Run(Create(session, new Pad()));
        Assert.False(result.Success);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(2, session.Writes.Count);
    }

    private static Rz090528PadlessHyperBoostValidation Create(Session session, Pad pad) => new(new Actors(session), pad, () => true);
    private static Task<PerformanceFanSetterValidationResult> Run(Rz090528PadlessHyperBoostValidation runner) =>
        runner.RunAsync(PerformanceFanValidationTarget.HyperBoostFromPerformance, CancellationToken.None);

    private sealed class Pad : ICoolingPadDiscoverySource
    {
        internal CoolingPadConnectionState State = CoolingPadConnectionState.Absent;
        internal int PresentAtObservation;
        private int observations;
        public CoolingPadConnectionState Observe() => ++observations == PresentAtObservation
            ? CoolingPadConnectionState.PresentExact : State;
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
        internal int Queries, QueriesAtFirstWrite, PowerQueries, FailWriteAt, DriftAtQuery, RejectPowerAt;
        internal Action<int>? AfterWrite, AfterQuery;
        private readonly byte[] modes = [2, 2];
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> transaction, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = transaction();
            byte[] response;
            if (command.ClassId == 7)
            {
                PowerQueries++;
                response = PowerQueries == RejectPowerAt ? [0, 0] : [0x11, 0x11];
            }
            else if (command.CommandId == 0x82)
            {
                Queries++;
                byte channel = command.Arguments.Span[1];
                response = [1, channel, Queries == DriftAtQuery ? (byte)0 : modes[channel - 1], 0];
                AfterQuery?.Invoke(Queries);
            }
            else
            {
                if (command.SubmissionAllowed?.Invoke() == false)
                {
                    return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.ConflictBlocked));
                }

                if (Writes.Count == 0)
                {
                    QueriesAtFirstWrite = Queries;
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
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
