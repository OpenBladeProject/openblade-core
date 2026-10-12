using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadHostDemand2200TrialTests
{
    private static readonly string[] Commands = ["00:84:0000", "00:04:0300", "00:84:0000",
        "0D:10:000600", "0D:01:01052C", "0D:10:01012C", "0D:10:000600", "00:04:0000", "00:84:0000"];
    private static readonly string[] Phases = ["baselineRead", "modeAcquire", "candidateRead",
        "demandEntry", "hostDemand", "fixed", "auto", "modeRestore", "restorationRead"];

    [Fact]
    public async Task ExactSequence_HasAdjacentEntryAndDemand_AndFourPhysicalGates()
    {
        var session = new Session();
        var observations = new List<int>();
        Task<bool> Observe(CancellationToken _) { observations.Add(session.Commands.Count); return Task.FromResult(true); }
        var result = await Runner(session).RunAsync(Observe, Observe, Observe, Observe, default);
        Assert.True(result.Success);
        Assert.Equal(Commands, session.Commands);
        Assert.Equal(new[] { 5, 6, 7, 9 }, observations);
        Assert.Equal(9, session.Transactions.Distinct().Count());
        Assert.True(result.DemandEntryAcknowledged && result.HostDemandAcknowledged && result.HostDemandPhysicalConfirmed);
        Assert.False(result.SubmissionStateUnknown);
        Assert.False(result.ManualRecoveryRequired);
        Assert.False(result.FanStateConfirmed || result.OwnershipConfirmed || result.HandbackConfirmed || result.ProductionAdmitted);
        Assert.Equal("0000", result.RawBaseline);
        Assert.Equal("0000", result.RawRestored);
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
    [InlineData(8)]
    [InlineData(9)]
    public async Task WrongResponseStopsAtExactCommand_AndPreservesAttemptEvidence(int index)
    {
        var session = new Session { WrongAt = index };
        var result = await Run(Runner(session));
        Assert.Equal("UnexpectedPayload", result.Failure);
        Assert.Equal(Phases[index - 1], result.FailurePhase);
        Assert.Equal(Commands.Take(index), session.Commands);
        Assert.Equal(index > 1, result.ManualRecoveryRequired);
        Assert.Equal(index is 2 or 4 or 5 or 6 or 7 or 8, result.SubmissionStateUnknown);
        Assert.Equal(index >= 4, result.DemandEntryAttempted);
        Assert.Equal(index > 4, result.DemandEntryAcknowledged);
        Assert.Equal(index >= 5, result.HostDemandAttempted);
        Assert.Equal(index > 5, result.HostDemandAcknowledged);
        Assert.Equal(index > 5, result.HostDemandPhysicalConfirmed);
        Assert.Equal(1, session.DisposeCount);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public async Task EntryOrDemandExceptionLeavesSubmissionUnknown_WithoutRestoration(int index, bool canceled)
    {
        var session = new Session { ThrowAt = index, CancelException = canceled };
        var result = await Run(Runner(session));
        Assert.Equal(canceled ? "Canceled" : "NativeIoFailure", result.Failure);
        Assert.True(result.SubmissionStateUnknown && result.ManualRecoveryRequired);
        Assert.False(result.FixedAttempted || result.AutoAttempted || result.ModeRestoreAttempted);
        Assert.Equal(index, session.Commands.Count);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 6)]
    [InlineData(3, 7)]
    [InlineData(4, 9)]
    public async Task PhysicalRefusalStopsWithoutCleanup(int rejected, int commandCount)
    {
        var session = new Session();
        int observation = 0;
        Task<bool> Observe(CancellationToken _) => Task.FromResult(++observation != rejected);
        var result = await Runner(session).RunAsync(Observe, Observe, Observe, Observe, default);
        Assert.Equal("OperatorRejected", result.Failure);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.SubmissionStateUnknown);
        Assert.Equal(commandCount, session.Commands.Count);
        Assert.Equal(rejected == 4, result.RawRestorationConfirmed);
    }

    [Fact]
    public async Task CancellationBetweenEntryAndDemandDoesNotSubmitDemand()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new Session { AfterResponse = count => { if (count == 4) { cancellation.Cancel(); } } };
        var result = await Run(Runner(session), cancellation.Token);
        Assert.Equal("Canceled", result.Failure);
        Assert.True(result.DemandEntryAcknowledged);
        Assert.False(result.HostDemandAttempted || result.FixedAttempted);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(4, session.Commands.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LatePhysicalYesAfterTimeoutOrCancellationCannotResume(bool cancel)
    {
        var session = new Session();
        var pending = new PendingDisposalRegistry();
        var late = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var runner = Runner(session, pending, TimeSpan.FromMilliseconds(100));
        var running = runner.RunAsync(_ => { entered.SetResult(); return late.Task; }, Yes, Yes, Yes, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancel) { cancellation.Cancel(); }
        var result = await running.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(cancel ? "Canceled" : "OperatorTimeout", result.Failure);
        Assert.Equal(5, session.Commands.Count);
        Assert.True(result.ManualRecoveryRequired);
        late.SetResult(true);
        await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(5, session.Commands.Count);
    }

    private static Task<bool> Yes(CancellationToken _) => Task.FromResult(true);
    private static Task<CoolingPadHostDemand2200TrialResult> Run(CoolingPadHostDemand2200TrialRunner runner,
        CancellationToken token = default) => runner.RunAsync(Yes, Yes, Yes, Yes, token);
    private static CoolingPadHostDemand2200TrialRunner Runner(Session session,
        PendingDisposalRegistry? pending = null, TimeSpan? timeout = null) =>
        new(() => new ValidationRunContext(), new Factory(session), TimeProvider.System,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), timeout ?? TimeSpan.FromSeconds(30), pending);

    private sealed class Factory(Session session) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(new ValidationActor(session, new Battery()), null));
    }
    private sealed class Battery : IBatteryCommandClient
    {
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(byte transactionId, CancellationToken token) => throw new NotSupportedException();
        public Task<HidSessionResult<HidUnit>> SetAsync(byte transactionId, BatteryValidationSetting setting, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Session : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        internal List<string> Commands { get; } = [];
        internal List<TransactionIdAssignment> Transactions { get; } = [];
        internal int WrongAt { get; init; }
        internal int ThrowAt { get; init; }
        internal bool CancelException { get; init; }
        internal Action<int>? AfterResponse { get; init; }
        internal int DisposeCount { get; private set; }
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> transaction, TimeSpan timeout, CancellationToken token)
        {
            Commands.Add($"{command.ClassId:X2}:{command.CommandId:X2}:{Convert.ToHexString(command.Arguments.Span)}");
            Transactions.Add(transaction());
            int index = Commands.Count;
            if (index == ThrowAt)
            {
                if (CancelException) { throw new OperationCanceledException(); }
                throw new IOException();
            }
            byte[] payload = Convert.FromHexString(index switch
            { 2 or 3 => "0300", 4 or 7 => "000600", 5 => "01052C", 6 => "01012C", _ => "0000" });
            Assert.Equal(payload.Length, command.ExpectedPayloadLength);
            Assert.True(command.PayloadValidator!(payload));
            Assert.False(command.PayloadValidator!(new byte[] { 255 }));
            AfterResponse?.Invoke(index);
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(index == WrongAt ? new byte[] { 255 } : payload, null));
        }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
}
