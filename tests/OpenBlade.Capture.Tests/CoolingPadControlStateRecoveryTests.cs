using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadControlStateRecoveryTests
{
    private static string[] Admission => ["--confirm-target", CoolingPadControlStateRecoveryCommand.ExactTargetConfirmation];

    [Theory]
    [InlineData("0300", 3, true)]
    [InlineData("0000", 1, false)]
    public async Task KnownBaseline_OnlyRestores0300AndRequiresPresentPhysicalConfirmation(string baseline, int count, bool attempted)
    {
        var session = new Session { Baseline = baseline };
        int observedCount = 0;
        var result = await Runner(session).RunAsync(_ =>
        {
            observedCount = session.Commands.Count;
            return Task.FromResult(true);
        }, default);
        Assert.True(result.Success);
        Assert.Equal(count, session.Commands.Count);
        Assert.Equal(count, observedCount);
        Assert.Equal(baseline, result.RawBaseline);
        Assert.Equal("0000", result.RawRestored);
        Assert.Equal(attempted, result.RestoreAttempted);
        Assert.Equal(attempted, result.RestoreAcknowledged);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(count, session.Transactions.Distinct().Count());
        Assert.Equal(1, session.DisposeCount);
    }

    [Theory]
    [InlineData(1, null, "NativeIoFailure", "baselineRead")]
    [InlineData(1, "0100", "UnexpectedPayload", "baselineRead")]
    [InlineData(1, "0001", "UnexpectedPayload", "baselineRead")]
    [InlineData(2, null, "NativeIoFailure", "restore")]
    [InlineData(2, "0300", "UnexpectedPayload", "restore")]
    [InlineData(2, "00", "UnexpectedPayload", "restore")]
    [InlineData(3, null, "NativeIoFailure", "restorationRead")]
    [InlineData(3, "0300", "RestorationMismatch", "restorationRead")]
    [InlineData(3, "0001", "UnexpectedPayload", "restorationRead")]
    public async Task FailureOrUnexpectedPayload_StopsWithoutRetryOrFinallyWrite(int index, string? raw, string failure, string phase)
    {
        var session = new Session { FaultAt = index, FaultPayload = raw };
        int callbacks = 0;
        var result = await Runner(session).RunAsync(_ => { callbacks++; return Task.FromResult(true); }, default);
        Assert.Equal(failure, result.Failure);
        Assert.Equal(phase, result.FailurePhase);
        Assert.Equal(index, session.Commands.Count);
        Assert.Equal(0, callbacks);
        Assert.True(result.ManualRecoveryRequired);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CancellationDuringExchange_Stops(int index)
    {
        var session = new Session { CancelAt = index };
        var result = await Runner(session).RunAsync(_ => Task.FromResult(true), default);
        Assert.Equal("Canceled", result.Failure);
        Assert.Equal(index, session.Commands.Count);
        Assert.True(result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task PhysicalRefusal_DoesNotClaimRecoveryDespiteRaw0000()
    {
        var session = new Session();
        var result = await Runner(session).RunAsync(_ => Task.FromResult(false), default);
        Assert.Equal("OperatorRejected", result.Failure);
        Assert.True(result.RawRestorationConfirmed);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.PhysicalConfirmed);
        Assert.Equal(3, session.Commands.Count);
    }

    [Fact]
    public async Task PhysicalTimeout_BoundsIgnoringCallbackAndDoesNotResume()
    {
        var session = new Session();
        var pending = new PendingDisposalRegistry();
        var late = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await Runner(session, pending: pending, timeout: TimeSpan.FromMilliseconds(100))
            .RunAsync(_ => late.Task, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("OperatorTimeout", result.Failure);
        Assert.True(result.ManualRecoveryRequired);
        late.SetException(new IOException());
        await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, session.Commands.Count);
    }

    [Fact]
    public async Task CallbackAndDisposalExceptions_DoNotEscape()
    {
        var session = new Session { ThrowDisposal = true };
        var result = await Runner(session).RunAsync(_ => throw new IOException(), default);
        Assert.Equal("OperatorException", result.Failure);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(3, session.Commands.Count);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task CancellationBeforeOpen_AndBudgetExhaustionNeverOpen()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var session = new Session();
        Assert.Equal("Canceled", (await Runner(session).RunAsync(_ => Task.FromResult(true), cancellation.Token)).Failure);
        Assert.False(session.Opened);
        var context = new ValidationRunContext();
        while (context.RemainingTransactions > 2) { Assert.True(context.TryAllocateTransaction(out _)); }
        Assert.Equal("Exhausted", (await Runner(session, context).RunAsync(_ => Task.FromResult(true), default)).Failure);
        Assert.False(session.Opened);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task RunnerIsSingleUse()
    {
        var session = new Session();
        var runner = Runner(session);
        Assert.True((await runner.RunAsync(_ => Task.FromResult(true), default)).Success);
        Assert.Equal("Disposed", (await runner.RunAsync(_ => Task.FromResult(true), default)).Failure);
        Assert.Equal(3, session.Commands.Count);
    }

    [Theory]
    [InlineData("RZ09-05289EN4:1532:0F43:0200:0000-0300")]
    [InlineData("RZ09-05289EN4:1532:02C6:0200:0300-0000")]
    [InlineData("RZ09-05289EN4:1532:0F43:0201:0300-0000")]
    [InlineData("")]
    public async Task InvalidCliAdmission_DoesNotConstructRunner(string target)
    {
        int calls = 0;
        var output = new StringWriter();
        var command = new CoolingPadControlStateRecoveryCommand((_, _) =>
        { calls++; throw new InvalidOperationException(); }, new StringReader("YES"), output, new StringWriter());
        Assert.Equal(1, await command.RunAsync(["--confirm-target", target], default));
        Assert.Equal(1, await command.RunAsync(Admission.Concat(new[] { "--payload", "0300" }).ToArray(), default));
        Assert.Equal(1, await command.RunAsync([], default));
        Assert.Equal(0, calls);
        Assert.Equal("", output.ToString());
    }

    [Theory]
    [InlineData("YES", 0)]
    [InlineData("NO", 2)]
    [InlineData("", 2)]
    public async Task Cli_SeparatesPhysicalPromptFromJsonAndPreservesClosedFanClaims(string response, int exit)
    {
        var session = new Session();
        var output = new StringWriter();
        var prompts = new StringWriter();
        var command = new CoolingPadControlStateRecoveryCommand(Runner(session).RunAsync,
            new StringReader(response), output, prompts);
        Assert.Equal(exit, await command.RunAsync(Admission, default));
        Assert.Contains("RECOVERY_CONFIRM", prompts.ToString());
        Assert.DoesNotContain("RECOVERY_CONFIRM", output.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.False(root.GetProperty("fanCommandsSubmitted").GetBoolean());
        Assert.False(root.GetProperty("lightingCommandsSubmitted").GetBoolean());
        Assert.False(root.GetProperty("fanStateConfirmed").GetBoolean());
        Assert.False(root.GetProperty("ownershipConfirmed").GetBoolean());
        Assert.False(root.GetProperty("handbackConfirmed").GetBoolean());
        Assert.False(root.GetProperty("vendorRecoveryPerformed").GetBoolean());
        Assert.Equal(exit != 0, root.GetProperty("result").GetProperty("manualRecoveryRequired").GetBoolean());
        Assert.Equal(3, session.Commands.Count);
    }

    private static CoolingPadControlStateRecoveryRunner Runner(Session session, ValidationRunContext? context = null,
        PendingDisposalRegistry? pending = null, TimeSpan? timeout = null) => new(
        () => context ?? new ValidationRunContext(), new ActorFactory(session), TimeProvider.System,
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), timeout ?? TimeSpan.FromSeconds(180), pending);
    private sealed class ActorFactory(Session session) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            session.Opened = true;
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(new ValidationActor(session, new Battery()), null));
        }
    }
    private sealed class Battery : IBatteryCommandClient
    {
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(byte transactionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HidSessionResult<HidUnit>> SetAsync(byte transactionId, BatteryValidationSetting setting, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Session : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        internal bool Opened { get; set; }
        internal string Baseline { get; init; } = "0300";
        internal int FaultAt { get; init; }
        internal string? FaultPayload { get; init; }
        internal int CancelAt { get; init; }
        internal bool ThrowDisposal { get; init; }
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal List<TransactionIdAssignment> Transactions { get; } = [];
        internal int DisposeCount { get; private set; }
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            Transactions.Add(assignTransactionId());
            int index = Commands.Count;
            Assert.Equal(0x00, command.ClassId);
            Assert.Equal(index == 2 ? 0x04 : 0x84, command.CommandId);
            Assert.Equal("0000", Convert.ToHexString(command.Arguments.Span));
            Assert.Equal(TimeSpan.FromSeconds(3), exchangeTimeout);
            if (index == CancelAt) { throw new OperationCanceledException(); }
            if (index == FaultAt && FaultPayload is null)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.NativeIoFailure));
            }
            byte[] payload = Convert.FromHexString(index == FaultAt ? FaultPayload! : index == 1 ? Baseline : "0000");
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (ThrowDisposal) { throw new IOException(); }
            return ValueTask.CompletedTask;
        }
    }
}
