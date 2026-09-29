using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528PadlessHyperBoostValidationTests
{
    [Fact]
    public async Task StockBaselineEntryQueriesPerformanceWithoutAnySetter()
    {
        var inner = new Session();
        var runner = Create(inner, new Pad());
        Assert.True(runner.IsStockBaselineEligible());
        await using (var queries = await runner.OpenStockBaselineQueriesAsync())
        {
            var sample = await queries.ReadAsync(CancellationToken.None);
            Assert.Null(sample.Failure);
            Assert.Equal(new FanRpmReadings(3000, 3000), sample.Fans);
        }
        Assert.Empty(inner.Writes);
        Assert.Equal(2, inner.Queries);
        Assert.Equal(1, inner.PowerQueries);
    }

    [Fact]
    public async Task StockBaselineRefusesPadPresenceBeforeOpeningActor()
    {
        var inner = new Session();
        var runner = Create(inner, new Pad { State = CoolingPadConnectionState.PresentExact });
        Assert.False(runner.IsStockBaselineEligible());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.OpenStockBaselineQueriesAsync());
        Assert.Empty(inner.Writes);
        Assert.Equal(0, inner.Queries);
    }

    [Fact]
    public async Task UncertainPendingSnapshotBlocksWritesAndOptionalDisposalQueries()
    {
        var inner = new Session();
        var session = new Rz090528PadlessHyperBoostValidation.GuardedSession(inner,
            new ValidationRunContext(), new Pad(), () => true);
        session.MarkSnapshotUncertain();
        var result = await session.ExecuteAsync(new RazerCommandSpec(0x0D, 2,
            new byte[] { 1, 1, 7, 0 }, 4), () => TransactionIdAssignment.Valid(1),
            TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(result.Failure);
        await session.DisposeAsync();
        Assert.Empty(inner.Writes);
        Assert.Equal(0, inner.Queries);
    }

    [Fact]
    public async Task NoLoadTemperatureAbortStillRestoresFreshlyKnownPerformance()
    {
        var session = new Session();
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var telemetry = new Rz090528PadlessNoLoadValidationTests.Telemetry { Fault = "cpu" };
        var noLoad = new Rz090528PadlessNoLoadValidation(telemetry, clock, clock.Wait);
        var runner = new Rz090528PadlessHyperBoostValidation(new Actors(session), new Pad(), () => true, noLoad);
        var result = await Run(runner);
        Assert.False(result.Success);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal("CpuExperimentalTripwire", runner.NoLoadResult!.Failure);
        Assert.Equal(4, session.Writes.Count);
    }

    [Fact]
    public async Task NoLoadReadBudgetKeepsEightTransactionsForRestoration()
    {
        var context = new ValidationRunContext();
        for (int i = 0; i < 243; i++)
        {
            Assert.True(context.TryAllocateTransaction(out _));
        }

        var inner = new Session();
        await using var session = new Rz090528PadlessHyperBoostValidation.GuardedSession(inner, context, new Pad(), () => true);
        var sample = await session.ReadAsync(false, CancellationToken.None);
        Assert.Equal("CleanupTransactionReserve", sample.Failure);
        Assert.True(sample.PermitKnownStateRestore);
        Assert.Equal(12, context.RemainingTransactions);
        Assert.Equal(0, inner.Queries);
    }

    [Fact]
    public async Task NoLoadThermalAmbiguityNeverAttemptsPerformanceExit()
    {
        var session = new Session { DriftAtQuery = 13 };
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var observation = new Rz090528PadlessNoLoadValidation(
            new Rz090528PadlessNoLoadValidationTests.Telemetry(), clock, clock.Wait);
        var runner = new Rz090528PadlessHyperBoostValidation(new Actors(session), new Pad(), () => true, observation);
        var result = await Run(runner);
        Assert.False(result.Success);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.RestoreAttempted);
        Assert.Equal(2, session.Writes.Count);
    }
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
    [InlineData(PerformanceFanValidationTarget.HyperBoost, 0, "00")]
    [InlineData(PerformanceFanValidationTarget.HyperBoostFromSilent, 5, "05")]
    public async Task ImmediateRoundTripRestoresTheExactStartingMode(
        PerformanceFanValidationTarget target, byte mode, string restored)
    {
        var session = new Session();
        session.modes[0] = session.modes[1] = mode;
        var runner = Create(session, new Pad());

        var result = await runner.RunAsync(target, CancellationToken.None);

        Assert.True(result.Success, result.Failure.ToString());
        Assert.Equal(new[] { "01010700", "01020700", $"0101{restored}00", $"0102{restored}00" }, session.Writes);
    }

    [Fact]
    public async Task ImmediateRoundTripRefusesAStartingModeThatDoesNotMatchTheTarget()
    {
        var session = new Session();
        var runner = Create(session, new Pad());

        var result = await runner.RunAsync(PerformanceFanValidationTarget.HyperBoost, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(session.Writes);
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

    [Theory]
    [InlineData(2, 2, false)]
    [InlineData(7, 4, true)]
    [InlineData(3, 2, false)]
    public async Task BoundaryReturnsOnlyThroughConfirmedPerformanceOrGuardedEscape(byte returnMode, int writes, bool restoreWrite)
    {
        var session = new Session();
        var clock = new Rz090528PadlessNoLoadValidationTests.Clock();
        var boundary = new Rz090528PadlessBatteryBoundaryValidation(
            _ => ValueTask.FromResult(new Rz090528BatteryBoundaryExternal(clock.GetUtcNow(), 0, 45, 40, 0,
                session.Power == 0x11, true, true, null)),
            signal =>
            {
                if (signal == Rz090528BatteryBoundarySignal.ReadyToUnplug)
                {
                    session.Power = 0; session.modes[0] = session.modes[1] = 3;
                }
                else { session.Power = 0x11; session.modes[0] = session.modes[1] = returnMode; }
            }, clock, clock.Wait, readWindowsAc: () => session.Power == 0x11);
        var runner = new Rz090528PadlessHyperBoostValidation(new Actors(session), new Pad(), () => true,
            batteryBoundary: boundary, batteryReadIsolation: () => true);
        var result = await Run(runner);
        Assert.Equal(writes, session.Writes.Count);
        Assert.Equal(restoreWrite, result.RestoreAttempted);
        Assert.Equal(restoreWrite, result.RestoreAcknowledged);
        Assert.Equal(returnMode != 3, result.Restored);
        Assert.Equal(returnMode == 3, result.ManualRecoveryRequired);
        Assert.True(session.Disposed);
        Assert.True(runner.DisposalConfirmed);
        Assert.Equal(result.Restored, runner.BatteryBoundaryResult!.Restored);
        Assert.True(runner.BatteryBoundaryResult.HardwareQuiesced);
        if (returnMode != 3) { Assert.True(result.Success); }
    }

    [Fact]
    public async Task BoundaryBatteryReadDoesNotRequireGpuWriteEligibility()
    {
        var inner = new Session { Power = 0 };
        inner.modes[0] = inner.modes[1] = 3;
        await using var session = new Rz090528PadlessHyperBoostValidation.GuardedSession(inner,
            new ValidationRunContext(), new Pad(), () => false, boundaryReadIsolation: () => true, boundaryMode: true);
        var observed = await session.ReadBatteryBoundaryAsync(CancellationToken.None);
        Assert.Null(observed.Failure);
        Assert.Equal("0011", observed.PowerBefore);
        Assert.Equal(new PerformanceFanThermalPair("01010300", "01020300"), observed.Pair);
        Assert.Equal(2, inner.PowerQueries);
        Assert.Equal(2, inner.Queries);
        var write = await session.ExecuteAsync(new RazerCommandSpec(0x0D, 2, new byte[] { 1, 1, 7, 0 }, 4),
            () => TransactionIdAssignment.Valid(1), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(write.Failure);
        Assert.Empty(inner.Writes);
    }

    [Fact]
    public async Task BoundaryWellFormedMixedPairIsReturnedWithoutPoisoningWrites()
    {
        var inner = new Session();
        inner.modes[1] = 3;
        await using var session = new Rz090528PadlessHyperBoostValidation.GuardedSession(inner,
            new ValidationRunContext(), new Pad(), () => true, boundaryReadIsolation: () => true, boundaryMode: true);
        var observed = await session.ReadBatteryBoundaryAsync(CancellationToken.None);
        Assert.Null(observed.Failure);
        Assert.Equal(new PerformanceFanThermalPair("01010200", "01020300"), observed.Pair);
        Assert.Empty(inner.Writes);
    }

    [Fact]
    public async Task BoundaryMalformedPowerPoisonsWritesAndSkipsDisposalQueries()
    {
        var inner = new Session();
        inner.RejectPowerAt = 2;
        var session = new Rz090528PadlessHyperBoostValidation.GuardedSession(inner,
            new ValidationRunContext(), new Pad(), () => true, boundaryReadIsolation: () => true, boundaryMode: true);
        Assert.NotNull((await session.ReadBatteryBoundaryAsync(CancellationToken.None)).Failure);
        int queries = inner.Queries, powerQueries = inner.PowerQueries;
        var write = await session.ExecuteAsync(new RazerCommandSpec(0x0D, 2, new byte[] { 1, 1, 7, 0 }, 4),
            () => TransactionIdAssignment.Valid(1), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(write.Failure);
        await session.DisposeAsync();
        Assert.Empty(inner.Writes);
        Assert.Equal(queries, inner.Queries);
        Assert.Equal(powerQueries, inner.PowerQueries);
    }

    [Fact]
    public async Task BoundaryReserveRejectsBeforeAnyQuery()
    {
        var context = new ValidationRunContext();
        for (int i = 0; i < 244; i++) { Assert.True(context.TryAllocateTransaction(out _)); }
        var inner = new Session();
        await using var session = new Rz090528PadlessHyperBoostValidation.GuardedSession(inner,
            context, new Pad(), () => true, boundaryReadIsolation: () => true, boundaryMode: true);
        Assert.Equal("CleanupTransactionReserve", (await session.ReadBatteryBoundaryAsync(CancellationToken.None)).Failure);
        Assert.Equal(0, inner.Queries + inner.PowerQueries);
    }

    [Fact]
    public async Task BoundaryCanceledIsolationPrefixCannotEnqueueNativeQuery()
    {
        using var canceled = new CancellationTokenSource();
        var inner = new Session();
        await using var session = new Rz090528PadlessHyperBoostValidation.GuardedSession(inner,
            new ValidationRunContext(), new Pad(), () => true,
            boundaryReadIsolation: () => { canceled.Cancel(); return true; }, boundaryMode: true);
        Assert.NotNull((await session.ReadBatteryBoundaryAsync(canceled.Token)).Failure);
        Assert.Equal(0, inner.Queries + inner.PowerQueries);
        Assert.Empty(inner.Writes);
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
        internal readonly byte[] modes = [2, 2];
        internal byte Power = 0x11;
        internal bool Disposed;
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
                response = PowerQueries == RejectPowerAt ? [0, 0] : [Power, 0x11];
            }
            else if (command.CommandId == 0x88)
            {
                response = [1, command.Arguments.Span[1], 30];
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
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
