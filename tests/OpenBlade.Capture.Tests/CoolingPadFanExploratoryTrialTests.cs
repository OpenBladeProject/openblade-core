using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadFanExploratoryTrialTests
{
    private static readonly string[] ExpectedCommands =
        ["00:84:0000", "00:04:0300", "00:84:0000", "0D:10:01012C", "0D:10:000600", "00:04:0000", "00:84:0000"];
    private static readonly string[] Phases =
        ["baselineRead", "modeAcquire", "candidateRead", "fixed", "auto", "modeRestore", "restorationRead"];
    private static readonly string[] PhysicalPhases =
        ["fixedPhysicalConfirmation", "autoPhysicalConfirmation", "finalPhysicalConfirmation"];

    [Fact]
    public async Task Success_UsesOnlySevenExactCommandsAndWaitsForEachPhysicalObservation()
    {
        var session = new Session();
        var callbacks = new Callbacks(session);
        var result = await Run(Runner(session), callbacks);
        Assert.True(result.Success);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(ExpectedCommands, session.Commands.Select(Describe));
        Assert.Equal(7, session.Transactions.Distinct().Count());
        Assert.Equal(new[] { 4, 5, 7 }, callbacks.CommandCounts);
        Assert.Equal("0000", result.RawBaseline);
        Assert.Equal("0300", result.RawCandidate);
        Assert.Equal("0000", result.RawRestored);
        Assert.True(result.FixedPhysicalConfirmed && result.AutoPhysicalConfirmed && result.FinalPhysicalConfirmed);
        Assert.Equal(1, session.DisposeCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task ExchangeFailure_StopsAllLaterWrites(int index)
    {
        var session = new Session { FailureAt = index };
        var result = await Run(Runner(session), new Callbacks(session));
        Assert.Equal("NativeIoFailure", result.Failure);
        Assert.Equal(Phases[index - 1], result.FailurePhase);
        Assert.Equal(index, session.Commands.Count);
        Assert.Equal(index > 1, result.ManualRecoveryRequired);
        Assert.False(result.FinalPhysicalConfirmed);
        Assert.Equal(1, session.DisposeCount);
    }

    [Theory]
    [InlineData(1, "0100", "UnexpectedPayload")]
    [InlineData(1, "0300", "BaselineNot0000")]
    [InlineData(1, "00", "UnexpectedPayload")]
    [InlineData(2, "0000", "UnexpectedPayload")]
    [InlineData(3, "0000", "CandidateMismatch")]
    [InlineData(3, "0301", "UnexpectedPayload")]
    [InlineData(4, "01012D", "UnexpectedPayload")]
    [InlineData(4, "0101", "UnexpectedPayload")]
    [InlineData(5, "000601", "UnexpectedPayload")]
    [InlineData(6, "0300", "UnexpectedPayload")]
    [InlineData(7, "0300", "RestorationMismatch")]
    [InlineData(7, "0001", "UnexpectedPayload")]
    public async Task BadEchoOrQuery_NeverAdvances(int index, string raw, string failure)
    {
        var session = new Session { OverrideAt = index, OverridePayload = raw };
        var result = await Run(Runner(session), new Callbacks(session));
        Assert.Equal(failure, result.Failure);
        Assert.Equal(Phases[index - 1], result.FailurePhase);
        Assert.Equal(index, session.Commands.Count);
        Assert.Equal(index > 1, result.ManualRecoveryRequired);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PhysicalRefusal_StopsEvenWhenRawRestored(int physicalIndex)
    {
        var session = new Session();
        var callbacks = new Callbacks(session) { RejectAt = physicalIndex };
        var result = await Run(Runner(session), callbacks);
        Assert.Equal("OperatorRejected", result.Failure);
        Assert.Equal(PhysicalPhases[physicalIndex - 1], result.FailurePhase);
        Assert.Equal(physicalIndex == 1 ? 4 : physicalIndex == 2 ? 5 : 7, session.Commands.Count);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.FinalPhysicalConfirmed);
        Assert.Equal(physicalIndex == 3, result.RawRestorationConfirmed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PhysicalException_StopsWithoutAutomaticCleanup(int physicalIndex)
    {
        var session = new Session();
        var result = await Run(Runner(session), new Callbacks(session) { ThrowAt = physicalIndex });
        Assert.Equal("OperatorException", result.Failure);
        Assert.Equal(PhysicalPhases[physicalIndex - 1], result.FailurePhase);
        Assert.Equal(physicalIndex == 1 ? 4 : physicalIndex == 2 ? 5 : 7, session.Commands.Count);
        Assert.True(result.ManualRecoveryRequired);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PhysicalTimeout_BoundsIgnoringTokenAndObservesLateCompletionWithoutResuming(int physicalIndex)
    {
        var session = new Session();
        var pending = new PendingDisposalRegistry();
        var late = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = new Callbacks(session) { WaitAt = physicalIndex, WaitTask = late.Task };
        var runner = Runner(session, pending, TimeSpan.FromMilliseconds(100));
        var result = await Run(runner, callbacks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("OperatorTimeout", result.Failure);
        Assert.Equal(PhysicalPhases[physicalIndex - 1], result.FailurePhase);
        int commandCount = physicalIndex == 1 ? 4 : physicalIndex == 2 ? 5 : 7;
        Assert.Equal(commandCount, session.Commands.Count);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(1, pending.Count);
        late.SetException(new IOException("late callback"));
        await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(commandCount, session.Commands.Count);
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public async Task SynchronouslyBlockedCallback_IsAlsoBounded()
    {
        var session = new Session();
        using var release = new ManualResetEventSlim();
        var pending = new PendingDisposalRegistry();
        var runner = Runner(session, pending, TimeSpan.FromMilliseconds(100));
        try
        {
            var result = await runner.RunAsync(_ =>
            {
                release.Wait();
                return Task.FromResult(true);
            }, _ => Task.FromResult(true), _ => Task.FromResult(true), default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("OperatorTimeout", result.Failure);
            Assert.Equal(4, session.Commands.Count);
            Assert.True(result.ManualRecoveryRequired);
        }
        finally
        {
            release.Set();
            await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(4, session.Commands.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task CancellationDuringExchange_StopsAtThatSubmission(int index)
    {
        var session = new Session { CancelAt = index };
        var result = await Run(Runner(session), new Callbacks(session));
        Assert.Equal("Canceled", result.Failure);
        Assert.Equal(Phases[index - 1], result.FailurePhase);
        Assert.Equal(index, session.Commands.Count);
        Assert.Equal(index > 1, result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task BlockingObserverCancellationRegistration_DoesNotBlockTimeoutOrCallerCancellation()
    {
        var session = new Session();
        var pending = new PendingDisposalRegistry();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var runner = Runner(session, pending, TimeSpan.FromMilliseconds(100));
        try
        {
            var running = runner.RunAsync(token =>
            {
                _ = token.Register(() =>
                {
                    registrationEntered.TrySetResult();
                    release.Wait();
                });
                callbackEntered.TrySetResult();
                return late.Task;
            }, _ => Task.FromResult(true), _ => Task.FromResult(true), cancellation.Token);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("OperatorTimeout", result.Failure);
            await registrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Caller cancellation remains independent of the blocked observer.
            cancellation.Cancel();
            Assert.Equal(4, session.Commands.Count);
            Assert.True(result.ManualRecoveryRequired);
        }
        finally
        {
            release.Set();
            late.TrySetResult(true);
            await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(4, session.Commands.Count);
    }

    [Fact]
    public async Task ConfirmationTrueAtDeadline_IsRejectedWithoutAdvancingWrites()
    {
        var session = new Session();
        var clock = new Clock();
        var runner = new CoolingPadFanExploratoryTrialRunner(() => new ValidationRunContext(),
            new ActorFactory(session), clock, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
        var result = await runner.RunAsync(_ =>
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            return Task.FromResult(true);
        }, _ => Task.FromResult(true), _ => Task.FromResult(true), default);
        Assert.Equal("OperatorTimeout", result.Failure);
        Assert.Equal("fixedPhysicalConfirmation", result.FailurePhase);
        Assert.Equal(4, session.Commands.Count);
        Assert.True(result.ManualRecoveryRequired);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CancellationDuringPhysicalCallback_StopsEvenIfCallbackReturnsTrue(int physicalIndex)
    {
        using var cancellation = new CancellationTokenSource();
        var session = new Session();
        var callbacks = new Callbacks(session)
        { AfterObservation = index => { if (index == physicalIndex) { cancellation.Cancel(); } } };
        var result = await Run(Runner(session), callbacks, cancellation.Token);
        Assert.Equal("Canceled", result.Failure);
        Assert.Equal(PhysicalPhases[physicalIndex - 1], result.FailurePhase);
        Assert.Equal(physicalIndex == 1 ? 4 : physicalIndex == 2 ? 5 : 7, session.Commands.Count);
        Assert.True(result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task CancellationAfterCandidate_DoesNotSubmitFixedOrCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new Session { AfterResponse = index => { if (index == 3) { cancellation.Cancel(); } } };
        var result = await Run(Runner(session), new Callbacks(session), cancellation.Token);
        Assert.Equal("Canceled", result.Failure);
        Assert.Equal(3, session.Commands.Count);
        Assert.True(result.CandidateConfirmed && result.ManualRecoveryRequired);
        Assert.False(result.FixedAttempted || result.AutoAttempted || result.ModeRestoreAttempted);
    }

    [Fact]
    public async Task CancellationBeforeOpening_SubmitsNothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var session = new Session();
        var factory = new ActorFactory(session);
        var result = await Run(Runner(session, factory: factory), new Callbacks(session), cancellation.Token);
        Assert.Equal("Canceled", result.Failure);
        Assert.False(factory.Opened || result.ManualRecoveryRequired);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task BudgetBelowSeven_DoesNotOpen()
    {
        var context = new ValidationRunContext();
        while (context.RemainingTransactions > 6)
        {
            Assert.True(context.TryAllocateTransaction(out _));
        }
        var session = new Session();
        var factory = new ActorFactory(session);
        var result = await Run(Runner(session, factory: factory, context: context), new Callbacks(session));
        Assert.Equal("Exhausted", result.Failure);
        Assert.False(factory.Opened || result.ManualRecoveryRequired);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task ExceptionAndCleanupException_AreContainedWithoutFurtherWrites()
    {
        var session = new Session { ThrowAt = 5, ThrowDisposal = true };
        var result = await Run(Runner(session), new Callbacks(session));
        Assert.Equal("NativeIoFailure", result.Failure);
        Assert.Equal("auto", result.FailurePhase);
        Assert.Equal(5, session.Commands.Count);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task RunnerIsSingleUse()
    {
        var session = new Session();
        var runner = Runner(session);
        Assert.True((await Run(runner, new Callbacks(session))).Success);
        Assert.Equal("Disposed", (await Run(runner, new Callbacks(session))).Failure);
        Assert.Equal(7, session.Commands.Count);
    }

    [Fact]
    public void Runner_KeepsNativeCallsAndLaptopSemanticsInApprovedComposition()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "OpenBlade.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }
        Assert.NotNull(root);
        string source = File.ReadAllText(Path.Combine(root, "src/OpenBlade.Capture/CoolingPadFanExploratoryTrial.cs"));
        Assert.DoesNotContain("WindowsHid", source);
        Assert.DoesNotContain("DeviceModeProtocol", source);
        Assert.DoesNotContain("Bios", source, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<CoolingPadFanExploratoryTrialResult> Run(CoolingPadFanExploratoryTrialRunner runner,
        Callbacks callbacks, CancellationToken token = default) => runner.RunAsync(callbacks.Observe, callbacks.Observe, callbacks.Observe, token);
    private static CoolingPadFanExploratoryTrialRunner Runner(Session session,
        PendingDisposalRegistry? pending = null, TimeSpan? operatorTimeout = null,
        ActorFactory? factory = null, ValidationRunContext? context = null) =>
        new(() => context ?? new ValidationRunContext(), factory ?? new ActorFactory(session), TimeProvider.System,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), operatorTimeout ?? TimeSpan.FromSeconds(60), pending);
    private static string Describe(RazerCommandSpec c) => $"{c.ClassId:X2}:{c.CommandId:X2}:{Convert.ToHexString(c.Arguments.Span)}";

    private sealed class Callbacks(Session session)
    {
        internal List<int> CommandCounts { get; } = [];
        internal int RejectAt { get; init; }
        internal int ThrowAt { get; init; }
        internal int WaitAt { get; init; }
        internal Task<bool>? WaitTask { get; init; }
        internal Action<int>? AfterObservation { get; init; }
        internal Task<bool> Observe(CancellationToken token)
        {
            CommandCounts.Add(session.Commands.Count);
            int index = CommandCounts.Count;
            AfterObservation?.Invoke(index);
            if (index == ThrowAt)
            {
                throw new IOException();
            }
            if (index == WaitAt)
            {
                return WaitTask!;
            }
            return Task.FromResult(index != RejectAt);
        }
    }
    private sealed class ActorFactory(Session session) : IValidationActorFactory
    {
        internal bool Opened { get; private set; }
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            Opened = true;
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(new ValidationActor(session, new Battery()), null));
        }
    }
    private sealed class Battery : IBatteryCommandClient
    {
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(byte transactionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HidSessionResult<HidUnit>> SetAsync(byte transactionId, BatteryValidationSetting setting, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref timestamp, duration.Ticks);
    }
    private sealed class Session : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal List<TransactionIdAssignment> Transactions { get; } = [];
        internal int FailureAt { get; init; }
        internal int CancelAt { get; init; }
        internal int ThrowAt { get; init; }
        internal int OverrideAt { get; init; }
        internal string OverridePayload { get; init; } = "";
        internal bool ThrowDisposal { get; init; }
        internal Action<int>? AfterResponse { get; init; }
        internal int DisposeCount { get; private set; }
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            Transactions.Add(assignTransactionId());
            int index = Commands.Count;
            Assert.Equal(ExpectedCommands[index - 1], Describe(command));
            Assert.Equal(TimeSpan.FromSeconds(3), exchangeTimeout);
            byte[] normalPayload = Convert.FromHexString(index switch
            { 2 or 3 => "0300", 4 => "01012C", 5 => "000600", _ => "0000" });
            Assert.Equal(normalPayload.Length, command.ExpectedPayloadLength);
            Assert.True(command.PayloadValidator!(normalPayload));
            Assert.False(command.PayloadValidator!(new byte[] { 0x01 }));
            if (index == CancelAt)
            {
                throw new OperationCanceledException();
            }
            if (index == ThrowAt)
            {
                throw new IOException();
            }
            if (index == FailureAt)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.NativeIoFailure));
            }
            AfterResponse?.Invoke(index);
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(
                index == OverrideAt ? Convert.FromHexString(OverridePayload) : normalPayload, null));
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (ThrowDisposal)
            {
                throw new IOException();
            }
            return ValueTask.CompletedTask;
        }
    }
}
