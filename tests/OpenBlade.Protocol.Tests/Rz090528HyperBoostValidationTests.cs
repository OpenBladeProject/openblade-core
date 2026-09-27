using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528HyperBoostValidationTests
{
    [Fact]
    public async Task CapturedPairIsConfirmedAndRestoredWithFreshPowerBeforeEveryWrite()
    {
        var session = new Session();
        PerformanceFanSetterValidationResult result = await Run(session);
        Assert.True(result.Success);
        Assert.Equal(new[] { "01010700", "01020700", "01010200", "01020200" }, session.Writes);
        Assert.Equal(4, session.PowerQueries);
        Assert.Equal(6, session.ThermalQueries);
        Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task NonPerformanceBaselineNeverWrites(byte mode)
    {
        var session = new Session { Mode = mode };
        Assert.False((await Run(session)).Success);
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task PowerLossBeforeAnyWriteStopsRemainingSequence(int powerQuery)
    {
        var session = new Session { RejectPowerAt = powerQuery };
        Assert.False((await Run(session)).Success);
        Assert.Equal(powerQuery - 1, session.Writes.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task IndeterminateWriteNeverRetriesOrBlindlyRestores(int write)
    {
        var session = new Session { FailWriteAt = write };
        PerformanceFanSetterValidationResult result = await Run(session);
        Assert.False(result.Success);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(write, session.Writes.Count);
    }

    [Fact]
    public async Task LostPadAfterPowerReadBlocksSubmission()
    {
        var pad = new Pad();
        var session = new Session { AfterPowerQuery = () => pad.State = CoolingPadConnectionState.Absent };
        Assert.False((await Run(session, pad)).Success);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task LostIsolationAfterPowerReadBlocksSubmission()
    {
        bool isolated = true;
        var session = new Session { AfterPowerQuery = () => isolated = false };
        Assert.False((await Run(session, isolated: () => isolated)).Success);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task AbsentPadNeverOpensHardware()
    {
        var session = new Session();
        Assert.False((await Run(session, new Pad { State = CoolingPadConnectionState.Absent })).Success);
        Assert.Equal(0, session.ThermalQueries);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task MismatchedCandidatePairStopsWithoutRestoration()
    {
        var session = new Session { MismatchCandidate = true };
        PerformanceFanSetterValidationResult result = await Run(session);
        Assert.False(result.Success);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(2, session.Writes.Count);
    }

    [Fact]
    public async Task FailedWriteAllowsReadOnlyReconciliationButNeverAnotherWrite()
    {
        var inner = new Session { FailWriteAt = 1 };
        await using var session = new Rz090528HyperBoostValidation.GuardedSession(inner, new Pad(), () => true);
        var write = new RazerCommandSpec(0x0D, 0x02, new byte[] { 1, 1, 7, 0 }, 4);
        var read = new RazerCommandSpec(0x0D, 0x82, new byte[] { 1, 1, 0, 0 }, 4);
        Assert.NotNull((await session.ExecuteAsync(write, () => default, TimeSpan.FromSeconds(3), CancellationToken.None)).Failure);
        Assert.Null((await session.ExecuteAsync(read, () => default, TimeSpan.FromSeconds(3), CancellationToken.None)).Failure);
        Assert.NotNull((await session.ExecuteAsync(write, () => default, TimeSpan.FromSeconds(3), CancellationToken.None)).Failure);
        Assert.Single(inner.Writes);
        Assert.Equal(1, inner.ThermalQueries);
    }

    [Fact]
    public async Task ExistingSubmissionGuardIsPreserved()
    {
        var inner = new Session();
        await using var session = new Rz090528HyperBoostValidation.GuardedSession(inner, new Pad(), () => true);
        var write = new RazerCommandSpec(0x0D, 0x02, new byte[] { 1, 1, 7, 0 }, 4)
        {
            SubmissionAllowed = () => false,
        };
        Assert.NotNull((await session.ExecuteAsync(write, () => default, TimeSpan.FromSeconds(3), CancellationToken.None)).Failure);
        Assert.Empty(inner.Writes);
    }

    [Fact]
    public async Task CanceledBeforeStartNeverOpensHardware()
    {
        var session = new Session();
        PerformanceFanSetterValidationResult result = await Create(session, new Pad(), () => true)
            .RunAsync(PerformanceFanValidationTarget.HyperBoostFromPerformance, new CancellationToken(true));
        Assert.Equal(PerformanceFanValidationFailure.Canceled, result.Failure);
        Assert.Equal(0, session.ThermalQueries);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task OtherTargetsAreNotExposed()
    {
        var runner = Create(new Session(), new Pad(), () => true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunAsync(PerformanceFanValidationTarget.HyperBoost, CancellationToken.None));
    }

    private static Task<PerformanceFanSetterValidationResult> Run(
        Session session, Pad? pad = null, Func<bool>? isolated = null) =>
        Create(session, pad ?? new Pad(), isolated ?? (() => true)).RunAsync(
            PerformanceFanValidationTarget.HyperBoostFromPerformance, CancellationToken.None);

    private static IPerformanceFanSetterValidationRunner Create(Session session, Pad pad, Func<bool> isolated) =>
        Rz090528HyperBoostValidation.Create(new Actors(session), pad, isolated, TimeSpan.FromSeconds(3));

    private sealed class Pad : ICoolingPadDiscoverySource
    {
        internal CoolingPadConnectionState State = CoolingPadConnectionState.PresentExact;
        public CoolingPadConnectionState Observe() => State;
    }

    private sealed class Actors(Session session) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(
                new ValidationActor(session, new BatteryCommandClient(session, TimeSpan.FromSeconds(3))), null));
    }

    private sealed class Session : IRazerCommandSession
    {
        internal byte Mode = 2;
        private readonly byte[] channelModes = [2, 2];
        internal List<string> Writes { get; } = [];
        internal int PowerQueries;
        internal int ThermalQueries;
        internal int RejectPowerAt;
        internal int FailWriteAt;
        internal bool MismatchCandidate;
        internal bool Disposed;
        internal Action? AfterPowerQuery;
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command, Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            _ = assignTransactionId();
            byte[] response;
            if (command.ClassId == 7 && command.CommandId == 0x8C)
            {
                PowerQueries++;
                response = PowerQueries == RejectPowerAt ? [0, 0] : [0x11, 0x11];
                AfterPowerQuery?.Invoke();
            }
            else if (command.CommandId == 0x82)
            {
                ThermalQueries++;
                byte channel = command.Arguments.Span[1];
                byte mode = Writes.Count == 0 ? Mode : channelModes[channel - 1];
                if (MismatchCandidate && Writes.Count == 2 && channel == 2)
                {
                    mode = 2;
                }
                response = [1, channel, mode, 0];
            }
            else
            {
                Assert.Equal(0x02, command.CommandId);
                if (command.SubmissionAllowed?.Invoke() == false)
                {
                    return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.ConflictBlocked));
                }
                response = command.Arguments.ToArray();
                Writes.Add(Convert.ToHexString(response));
                if (Writes.Count == FailWriteAt)
                {
                    return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout));
                }
                channelModes[response[1] - 1] = response[2];
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
