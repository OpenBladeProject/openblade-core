using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadControlStateRoundTripTests
{
    [Fact]
    public async Task Success_UsesFiveFixedCommandsAndDistinctTransactions()
    {
        var session = new Session();
        var result = await Runner(session).RunAsync(default);
        Assert.True(result.Success);
        Assert.Equal("0000", result.RawBaseline);
        Assert.Equal("0300", result.RawCandidate);
        Assert.Equal("0000", result.RawRestored);
        Assert.True(result.ApplyAttempted && result.ApplyAcknowledged && result.CandidateConfirmed);
        Assert.True(result.RestoreAttempted && result.RestoreAcknowledged && result.Restored);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(new[] { "00:84:0000", "00:04:0300", "00:84:0000", "00:04:0000", "00:84:0000" },
            session.Commands.Select(c => $"{c.ClassId:X2}:{c.CommandId:X2}:{Convert.ToHexString(c.Arguments.Span)}"));
        Assert.Equal(5, session.Transactions.Distinct().Count());
        Assert.Equal(1, session.DisposeCount);
    }

    [Theory]
    [InlineData("0300", "BaselineNot0000")]
    [InlineData("0100", "UnexpectedPayload")]
    [InlineData("0001", "UnexpectedPayload")]
    [InlineData("00", "UnexpectedPayload")]
    public async Task BaselineWrongOrUnknown_NeverWrites(string raw, string failure)
    {
        var session = new Session { OverrideAt = 1, OverridePayload = raw };
        var result = await Runner(session).RunAsync(default);
        Assert.Equal(failure, result.Failure);
        Assert.Equal("baselineRead", result.FailurePhase);
        Assert.Single(session.Commands);
        Assert.False(result.ApplyAttempted || result.ManualRecoveryRequired);
    }

    [Theory]
    [InlineData(1, "baselineRead", false)]
    [InlineData(2, "apply", true)]
    [InlineData(3, "candidateRead", true)]
    [InlineData(4, "restore", true)]
    [InlineData(5, "restorationRead", true)]
    public async Task ExchangeFailure_StopsAllFurtherSubmissions(int index, string phase, bool manual)
    {
        var session = new Session { FailureAt = index };
        var result = await Runner(session).RunAsync(default);
        Assert.Equal("NativeIoFailure", result.Failure);
        Assert.Equal(phase, result.FailurePhase);
        Assert.Equal(index, session.Commands.Count);
        Assert.Equal(manual, result.ManualRecoveryRequired);
        Assert.False(result.Restored);
        Assert.Equal(index >= 4, result.RestoreAttempted);
    }

    [Theory]
    [InlineData(2, "0000", "UnexpectedPayload")]
    [InlineData(2, "0301", "UnexpectedPayload")]
    [InlineData(3, "0000", "CandidateMismatch")]
    [InlineData(3, "0100", "UnexpectedPayload")]
    [InlineData(4, "0300", "UnexpectedPayload")]
    [InlineData(5, "0300", "RestorationMismatch")]
    [InlineData(5, "0001", "UnexpectedPayload")]
    public async Task UnexpectedAcknowledgmentOrReadback_StopsWithoutAnotherWrite(int index, string raw, string failure)
    {
        var session = new Session { OverrideAt = index, OverridePayload = raw };
        var result = await Runner(session).RunAsync(default);
        Assert.Equal(failure, result.Failure);
        Assert.Equal(index, session.Commands.Count);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.Restored);
        Assert.Equal(index >= 4, result.RestoreAttempted);
    }

    [Fact]
    public async Task CancellationBeforeOpening_SubmitsNothing()
    {
        var session = new Session();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await Runner(session).RunAsync(cancellation.Token);
        Assert.Equal("Canceled", result.Failure);
        Assert.Empty(session.Commands);
        Assert.False(result.ApplyAttempted || result.ManualRecoveryRequired);
        Assert.Equal(0, session.DisposeCount);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public async Task CancellationDuringExchange_StopsAndNeverRestoresAmbiguity(int index, bool manual)
    {
        var session = new Session { ThrowCancellationAt = index };
        var result = await Runner(session).RunAsync(default);
        Assert.Equal("Canceled", result.Failure);
        Assert.Equal(index, session.Commands.Count);
        Assert.Equal(manual, result.ManualRecoveryRequired);
        Assert.False(result.Restored);
    }

    [Fact]
    public async Task CancellationAfterConfirmedCandidate_CompletesOneBoundedRestoreAndReadback()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new Session { AfterResponse = index => { if (index == 3) { cancellation.Cancel(); } } };
        var result = await Runner(session).RunAsync(cancellation.Token);
        Assert.Equal("Canceled", result.Failure);
        Assert.True(result.CandidateConfirmed && result.Restored);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(5, session.Commands.Count);
        Assert.All(session.Tokens.Skip(3), token => Assert.False(token.IsCancellationRequested));
    }

    [Fact]
    public async Task CancellationAfterApplyAcknowledgment_StopsBeforeCandidateRead()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new Session { AfterResponse = index => { if (index == 2) { cancellation.Cancel(); } } };
        var result = await Runner(session).RunAsync(cancellation.Token);
        Assert.Equal("Canceled", result.Failure);
        Assert.Equal(2, session.Commands.Count);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.RestoreAttempted);
    }

    [Fact]
    public async Task ExceptionAndDisposalException_DoNotEscapeOrClaimRestoration()
    {
        var session = new Session { ThrowAt = 4, ThrowDisposal = true };
        var result = await Runner(session).RunAsync(default);
        Assert.Equal("restore", result.FailurePhase);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.Restored);
        Assert.Equal(4, session.Commands.Count);
    }

    [Fact]
    public async Task Runner_IsSingleUse()
    {
        var session = new Session();
        var runner = Runner(session);
        Assert.True((await runner.RunAsync(default)).Success);
        Assert.Equal("Disposed", (await runner.RunAsync(default)).Failure);
        Assert.Equal(5, session.Commands.Count);
    }

    [Fact]
    public async Task InsufficientTransactionBudget_DoesNotOpenOrSubmit()
    {
        var context = new ValidationRunContext();
        while (context.RemainingTransactions > 4)
        {
            Assert.True(context.TryAllocateTransaction(out _));
        }
        var session = new Session();
        var factory = new ActorFactory(session);
        var runner = new CoolingPadControlStateRoundTripRunner(() => context, factory,
            TimeProvider.System, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        var result = await runner.RunAsync(default);
        Assert.Equal("Exhausted", result.Failure);
        Assert.False(factory.Opened);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task RestoreTimeout_DoesNotRetryOrReadAgain()
    {
        var session = new Session { FailureAt = 4, FailureKind = HidSessionFailureKind.Timeout };
        var result = await Runner(session).RunAsync(default);
        Assert.Equal("Timeout", result.Failure);
        Assert.Equal("restore", result.FailurePhase);
        Assert.True(result.RestoreAttempted && result.ManualRecoveryRequired);
        Assert.False(result.RestoreAcknowledged || result.Restored);
        Assert.Equal(4, session.Commands.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("RZ09-05289EN4:1532:0F43:0200:0300-0000-0300")]
    [InlineData("RZ09-05289EN4:1532:0F43:0200:0000-0300-0000:05")]
    public async Task Confirmation_RejectsMissingAlternateOrExtendedIntent(string confirmation)
    {
        bool called = false;
        var command = new CoolingPadControlStateRoundTripCommand(_ =>
        { called = true; throw new InvalidOperationException(); }, new StringWriter());
        Assert.Equal(1, await command.RunAsync(["--confirm-target", confirmation], default));
        Assert.False(called);
        Assert.Equal(1, await command.RunAsync(["--confirm-target", CoolingPadControlStateRoundTripCommand.ExactTargetConfirmation, "--report-id", "05"], default));
        Assert.False(called);
    }

    [Fact]
    public async Task Output_ExposesRawEvidenceAndKeepsAllSemanticGatesClosed()
    {
        var output = new StringWriter();
        var command = new CoolingPadControlStateRoundTripCommand(Runner(new Session()).RunAsync, output);
        Assert.Equal(0, await command.RunAsync(["--confirm-target", CoolingPadControlStateRoundTripCommand.ExactTargetConfirmation], default));
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        foreach (string gate in new[] { "productionAdmitted", "fanStateConfirmed", "ownershipConfirmed", "handbackConfirmed", "semanticMappingConfirmed", "hardwareTrialPreviouslyValidated" })
        {
            Assert.False(root.GetProperty(gate).GetBoolean());
        }

        Assert.Equal("RZ09-05289EN4", root.GetProperty("device").GetProperty("sku").GetString());
        Assert.Equal("0000", root.GetProperty("result").GetProperty("rawRestored").GetString());
        Assert.DoesNotContain("Driver", output.ToString());
        Assert.DoesNotContain("bios", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static CoolingPadControlStateRoundTripRunner Runner(Session session) => new(
        () => new ValidationRunContext(), new ActorFactory(session), TimeProvider.System,
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), new PendingDisposalRegistry());

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
    private sealed class Session : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal List<TransactionIdAssignment> Transactions { get; } = [];
        internal List<CancellationToken> Tokens { get; } = [];
        internal int FailureAt { get; init; }
        internal HidSessionFailureKind FailureKind { get; init; } = HidSessionFailureKind.NativeIoFailure;
        internal int OverrideAt { get; init; }
        internal string OverridePayload { get; init; } = "";
        internal int ThrowAt { get; init; }
        internal int ThrowCancellationAt { get; init; }
        internal bool ThrowDisposal { get; init; }
        internal Action<int>? AfterResponse { get; init; }
        internal int DisposeCount { get; private set; }
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            Transactions.Add(assignTransactionId());
            Tokens.Add(cancellationToken);
            int index = Commands.Count;
            Assert.Equal(TimeSpan.FromSeconds(3), exchangeTimeout);
            Assert.Equal(2, command.ExpectedPayloadLength);
            if (index == ThrowCancellationAt)
            {
                throw new OperationCanceledException();
            }

            if (index == ThrowAt)
            {
                throw new IOException();
            }

            if (index == FailureAt)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, FailureKind));
            }

            string raw = index == OverrideAt ? OverridePayload : index is 2 or 3 ? "0300" : "0000";
            AfterResponse?.Invoke(index);
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(Convert.FromHexString(raw), null));
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
