using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadFanSessionContinuityTests
{
    private static readonly string[] Sequence =
        ["00:84:0000", "00:04:0300", "00:84:0000", "0D:10:01012C", "00:84:0000", "0D:10:000600", "00:04:0000", "00:84:0000"];
    private static string[] Admission => ["--confirm-target", CoolingPadFanSessionContinuityCommand.ExactTargetConfirmation,
        "--confirm-recovery", CoolingPadFanSessionContinuityCommand.ExactRecoveryConfirmation];

    [Fact]
    public async Task Success_ClosesFirstSessionBeforeFreshOpenAndPreservesOriginalBaseline()
    {
        var fixture = new Fixture();
        List<int> observations = [];
        var result = await Run(Runner(fixture), token =>
        {
            observations.Add(fixture.Commands.Count);
            return Task.FromResult(true);
        });
        Assert.True(result.Success);
        Assert.True(result.FirstSessionClosed && result.FreshSessionOpened && result.FreshCandidateConfirmed);
        Assert.Equal("0000", result.RawBaseline);
        Assert.Equal("0300", result.RawCandidate);
        Assert.Equal("0300", result.RawIntermediate);
        Assert.Equal("0000", result.RawRestored);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(Sequence, fixture.Commands);
        Assert.Equal(new[] { 4, 5, 7, 8 }, fixture.CommandsAtTransitions);
        Assert.Equal(new[] { 4, 6, 8 }, observations);
        Assert.Equal(8, fixture.Transactions.Distinct().Count());
        Assert.Equal(4, fixture.First.CommandCount);
        Assert.Equal(4, fixture.Second.CommandCount);
        Assert.Equal(1, fixture.First.DisposeCount);
        Assert.Equal(1, fixture.Second.DisposeCount);
    }

    [Theory]
    [InlineData("fault", "CloseFailed")]
    [InlineData("incomplete", "CloseNotFullyDisposed")]
    public async Task FailedOrIncompleteFirstClose_NeverOpensFreshOrWritesAgain(string kind, string failure)
    {
        var fixture = new Fixture();
        fixture.First.ThrowClose = kind == "fault";
        fixture.First.NoFullyDisposed = kind == "incomplete";
        var result = await Run(Runner(fixture));
        Assert.Equal(failure, result.Failure);
        Assert.Equal("firstSessionClose", result.FailurePhase);
        Assert.True(result.FirstSessionCloseAttempted && result.ManualRecoveryRequired);
        Assert.False(result.FirstSessionClosed || result.FreshSessionOpenAttempted || result.AutoAttempted);
        Assert.Equal(1, fixture.OpenCount);
        Assert.Equal(4, fixture.Commands.Count);
        Assert.Equal(1, fixture.First.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstCloseTimeout_BoundsAsyncAndSynchronousDisposalAndNeverResumes(bool synchronous)
    {
        var fixture = new Fixture();
        var pending = new PendingDisposalRegistry();
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        if (synchronous) { fixture.First.BlockClose = release; }
        else { fixture.First.CloseTask = late.Task; }
        try
        {
            var result = await Run(Runner(fixture, pending, closeTimeout: TimeSpan.FromMilliseconds(100)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("CloseTimeout", result.Failure);
            Assert.Equal("firstSessionClose", result.FailurePhase);
            Assert.False(result.FirstSessionClosed || result.FreshSessionOpened || result.AutoAttempted);
            Assert.True(result.ManualRecoveryRequired);
            Assert.Equal(4, fixture.Commands.Count);
            Assert.Equal(1, fixture.OpenCount);
        }
        finally
        {
            release.Set();
            late.TrySetResult();
            await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, fixture.OpenCount);
        Assert.Equal(4, fixture.Commands.Count);
    }

    [Fact]
    public async Task AsynchronousFirstCloseFault_NeverOpensFreshSession()
    {
        var fixture = new Fixture();
        fixture.First.CloseTask = Task.FromException(new IOException());
        var result = await Run(Runner(fixture));
        Assert.Equal("CloseFailed", result.Failure);
        Assert.False(result.FirstSessionClosed || result.FreshSessionOpened);
        Assert.Equal(1, fixture.OpenCount);
        Assert.Equal(4, fixture.Commands.Count);
        Assert.True(result.ManualRecoveryRequired);
    }

    [Theory]
    [InlineData(false, "ConflictBlocked")]
    [InlineData(true, "ProtocolFailure")]
    public async Task FreshControllerConflictOrRetainedActorContext_StopsAfterClose(bool retainedContext, string failure)
    {
        var fixture = new Fixture { FreshConflict = !retainedContext };
        fixture.First.HoldLease = retainedContext;
        var result = await Run(Runner(fixture));
        Assert.True(result.FirstSessionClosed);
        Assert.True(result.FreshSessionOpenAttempted && result.ManualRecoveryRequired);
        Assert.False(result.FreshSessionOpened || result.AutoAttempted);
        Assert.Equal(failure, result.Failure);
        Assert.Equal("freshSessionOpen", result.FailurePhase);
        Assert.Equal(4, fixture.Commands.Count);
        fixture.First.Lease?.Dispose();
    }

    [Fact]
    public async Task FactoryReturningOldSession_IsRejectedBeforeFreshQuery()
    {
        var fixture = new Fixture { ReuseOldSession = true };
        var result = await Run(Runner(fixture));
        Assert.Equal("FreshSessionNotFresh", result.Failure);
        Assert.Equal(4, fixture.Commands.Count);
        Assert.Equal(1, fixture.First.DisposeCount);
        Assert.False(result.FreshSessionOpened || result.AutoAttempted);
    }

    [Theory]
    [InlineData(1, "0300", "BaselineNot0000")]
    [InlineData(1, "0100", "UnexpectedPayload")]
    [InlineData(2, "0000", "UnexpectedPayload")]
    [InlineData(3, "0000", "CandidateMismatch")]
    [InlineData(4, "01012D", "UnexpectedPayload")]
    [InlineData(5, "0000", "FreshCandidateMismatch")]
    [InlineData(5, "0301", "UnexpectedPayload")]
    [InlineData(6, "000601", "UnexpectedPayload")]
    [InlineData(7, "0300", "UnexpectedPayload")]
    [InlineData(8, "0300", "RestorationMismatch")]
    public async Task EchoOrReadMismatch_StopsAtThatExchange(int index, string payload, string failure)
    {
        var fixture = new Fixture { FaultAt = index, FaultPayload = payload };
        var result = await Run(Runner(fixture));
        Assert.Equal(failure, result.Failure);
        Assert.Equal(index, fixture.Commands.Count);
        Assert.Equal(index > 1, result.ManualRecoveryRequired);
        Assert.Equal(index > 4, result.FreshSessionOpened);
        if (index == 5) { Assert.False(result.AutoAttempted); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task ExchangeFailure_StopsWithoutCleanupWrites(int index)
    {
        var fixture = new Fixture { FaultAt = index };
        var result = await Run(Runner(fixture));
        Assert.Equal("NativeIoFailure", result.Failure);
        Assert.Equal(index, fixture.Commands.Count);
        Assert.Equal(index > 1, result.ManualRecoveryRequired);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task CancellationDuringExchange_StopsWithoutCleanupWrites(int index)
    {
        var fixture = new Fixture { CancelAt = index };
        var result = await Run(Runner(fixture));
        Assert.Equal("Canceled", result.Failure);
        Assert.Equal(index, fixture.Commands.Count);
        Assert.Equal(index > 1, result.ManualRecoveryRequired);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PhysicalRefusal_StopsAtTheCorrespondingSessionStage(int stage)
    {
        var fixture = new Fixture();
        int observations = 0;
        var result = await Run(Runner(fixture), _ => Task.FromResult(++observations != stage));
        Assert.Equal("OperatorRejected", result.Failure);
        Assert.Equal(stage == 1 ? 4 : stage == 2 ? 6 : 8, fixture.Commands.Count);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(stage > 1, result.FirstSessionClosed);
    }

    [Fact]
    public async Task LateFixedObservation_AfterTimeoutCannotCloseOrOpenSessions()
    {
        var fixture = new Fixture();
        var pending = new PendingDisposalRegistry();
        var late = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await Run(Runner(fixture, pending, operatorTimeout: TimeSpan.FromMilliseconds(100)), _ => late.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("OperatorTimeout", result.Failure);
        Assert.False(result.FirstSessionCloseAttempted || result.FreshSessionOpened);
        late.SetResult(true);
        await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.OpenCount);
        Assert.Equal(4, fixture.Commands.Count);
    }

    [Fact]
    public async Task CancellationDuringFirstClose_PreventsFreshOpen()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture();
        var pending = new PendingDisposalRegistry();
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.First.CloseTask = close.Task;
        fixture.First.OnClose = cancellation.Cancel;
        var result = await Run(Runner(fixture, pending), token: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Canceled", result.Failure);
        Assert.Equal("firstSessionClose", result.FailurePhase);
        Assert.Equal(1, fixture.OpenCount);
        close.SetResult();
        await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, fixture.Commands.Count);
    }

    [Fact]
    public async Task CancellationBeforeOpenAndInsufficientBudget_NeverOpen()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal("Canceled", (await Run(Runner(fixture), token: cancellation.Token)).Failure);
        var context = new ValidationRunContext();
        while (context.RemainingTransactions > 7) { Assert.True(context.TryAllocateTransaction(out _)); }
        Assert.Equal("Exhausted", (await Run(Runner(fixture, context: context))).Failure);
        Assert.Equal(0, fixture.OpenCount);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task RunnerIsSingleUse()
    {
        var fixture = new Fixture();
        var runner = Runner(fixture);
        Assert.True((await Run(runner)).Success);
        Assert.Equal("Disposed", (await Run(runner)).Failure);
        Assert.Equal(8, fixture.Commands.Count);
    }

    [Theory]
    [InlineData("ManualPowerCycleAndVendorRecovery")]
    [InlineData("manualpowercycleonly")]
    [InlineData("")]
    public async Task CliAdmission_RejectsRecoveryAlternativesAndExtraPayloadArguments(string recovery)
    {
        int calls = 0;
        var command = new CoolingPadFanSessionContinuityCommand((_, _, _, _) =>
        { calls++; throw new InvalidOperationException(); }, new StringReader("YES"), new StringWriter(), new StringWriter());
        Assert.Equal(1, await command.RunAsync([Admission[0], Admission[1], Admission[2], recovery], default));
        Assert.Equal(1, await command.RunAsync(Admission.Concat(new[] { "--payload", "000600" }).ToArray(), default));
        Assert.Equal(1, await command.RunAsync(["--confirm-target", "RZ09-05289EN4:1532:0F43:0201:Medium2200-CloseFreshSession-Auto", Admission[2], Admission[3]], default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Cli_OutputDistinguishesSessionBoundaryAndPhysicalEvidenceFromFanReadback()
    {
        var fixture = new Fixture();
        var output = new StringWriter();
        var prompts = new StringWriter();
        var command = new CoolingPadFanSessionContinuityCommand(Runner(fixture).RunAsync,
            new StringReader("YES\nYES\nYES\n"), output, prompts);
        Assert.Equal(0, await command.RunAsync(Admission, default));
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal("normal-close-and-fresh-session-in-same-process", root.GetProperty("continuityBoundary").GetString());
        Assert.False(root.GetProperty("processCrashTested").GetBoolean());
        Assert.False(root.GetProperty("newProcessTested").GetBoolean());
        Assert.False(root.GetProperty("fanStateConfirmed").GetBoolean());
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean());
        Assert.False(root.GetProperty("ownershipConfirmed").GetBoolean());
        Assert.False(root.GetProperty("handbackConfirmed").GetBoolean());
        Assert.Equal("ManualPowerCycleOnly", root.GetProperty("recoveryAcknowledgement").GetString());
        Assert.Equal(8, root.GetProperty("capturedSequence").GetArrayLength());
        Assert.Equal("0300", root.GetProperty("result").GetProperty("rawIntermediate").GetString());
        Assert.Contains("fresh session", prompts.ToString());
        Assert.DoesNotContain("FIXED_CONFIRM", output.ToString());
    }

    private static Task<CoolingPadFanSessionContinuityResult> Run(CoolingPadFanSessionContinuityRunner runner,
        Func<CancellationToken, Task<bool>>? observation = null, CancellationToken token = default)
    {
        observation ??= _ => Task.FromResult(true);
        return runner.RunAsync(observation, observation, observation, token);
    }
    private static CoolingPadFanSessionContinuityRunner Runner(Fixture fixture, PendingDisposalRegistry? pending = null,
        TimeSpan? closeTimeout = null, TimeSpan? operatorTimeout = null, ValidationRunContext? context = null) =>
        new(() => context ?? new ValidationRunContext(), fixture, TimeProvider.System,
            TimeSpan.FromSeconds(3), closeTimeout ?? TimeSpan.FromSeconds(5), operatorTimeout ?? TimeSpan.FromSeconds(180), pending);

    private sealed class Fixture : IValidationActorFactory
    {
        internal Session First { get; }
        internal Session Second { get; }
        internal List<string> Commands { get; } = [];
        internal List<int> CommandsAtTransitions { get; } = [];
        internal List<TransactionIdAssignment> Transactions { get; } = [];
        internal int OpenCount { get; private set; }
        internal int FaultAt { get; init; }
        internal int CancelAt { get; init; }
        internal string? FaultPayload { get; init; }
        internal bool FreshConflict { get; init; }
        internal bool ReuseOldSession { get; init; }
        internal Fixture() { First = new(this); Second = new(this); }
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            OpenCount++;
            if (OpenCount == 2)
            {
                Assert.Equal(1, First.DisposeCount);
                Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, First.Disposition);
                if (FreshConflict) { return ValueTask.FromResult(new HidSessionResult<ValidationActor>(null, HidSessionFailureKind.ConflictBlocked)); }
                if (ReuseOldSession) { return ValueTask.FromResult(new HidSessionResult<ValidationActor>(new ValidationActor(First, new Battery()), null)); }
            }
            if (!context.TryAcquireActor(out var lease))
            {
                return ValueTask.FromResult(new HidSessionResult<ValidationActor>(null, HidSessionFailureKind.ProtocolFailure));
            }
            Session session = OpenCount == 1 ? First : Second;
            session.Lease = lease;
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(new ValidationActor(session, new Battery()), null));
        }
    }
    private sealed class Session(Fixture fixture) : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition { get; private set; } = RazerCommandSessionDisposition.Healthy;
        internal ValidationRunContext.ActorLease? Lease { get; set; }
        internal bool HoldLease { get; set; }
        internal bool NoFullyDisposed { get; set; }
        internal bool ThrowClose { get; set; }
        internal Task? CloseTask { get; set; }
        internal ManualResetEventSlim? BlockClose { get; set; }
        internal Action? OnClose { get; set; }
        internal int CommandCount { get; private set; }
        internal int DisposeCount { get; private set; }
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string description = $"{command.ClassId:X2}:{command.CommandId:X2}:{Convert.ToHexString(command.Arguments.Span)}";
            fixture.Commands.Add(description);
            fixture.Transactions.Add(assignTransactionId());
            CommandCount++;
            int index = fixture.Commands.Count;
            Assert.Equal(Sequence[index - 1], description);
            Assert.Equal(TimeSpan.FromSeconds(3), exchangeTimeout);
            if (index >= 5) { Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, fixture.First.Disposition); }
            if (index == fixture.CancelAt) { throw new OperationCanceledException(); }
            if (index is 5 or 7 or 8) { fixture.CommandsAtTransitions.Add(index); }
            if (index == fixture.FaultAt && fixture.FaultPayload is null)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.NativeIoFailure));
            }
            byte[] payload = Convert.FromHexString(index == fixture.FaultAt ? fixture.FaultPayload! : index switch
            { 2 or 3 or 5 => "0300", 4 => "01012C", 6 => "000600", _ => "0000" });
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            OnClose?.Invoke();
            BlockClose?.Wait();
            if (ThrowClose) { throw new IOException(); }
            return new ValueTask(CompleteCloseAsync());
        }
        private async Task CompleteCloseAsync()
        {
            if (CloseTask is not null) { await CloseTask.ConfigureAwait(false); }
            if (!NoFullyDisposed) { Disposition = RazerCommandSessionDisposition.FullyDisposed; }
            if (!HoldLease) { Lease?.Dispose(); }
            if (ReferenceEquals(this, fixture.First)) { fixture.CommandsAtTransitions.Add(fixture.Commands.Count); }
        }
    }
    private sealed class Battery : IBatteryCommandClient
    {
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(byte transactionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HidSessionResult<HidUnit>> SetAsync(byte transactionId, BatteryValidationSetting setting, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
