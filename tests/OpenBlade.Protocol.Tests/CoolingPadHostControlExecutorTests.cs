using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CoolingPadHostControlExecutorTests
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task ApplyValidated2200Async_SubmitsExactPairOnce()
    {
        var session = new ScriptedSession(
            Success([0x00, 0x06, 0x00]),
            Success([0x01, 0x05, 0x2C]));
        var gate = new SemaphoreSlim(1, 1);
        bool poisoned = false;
        var executor = new CoolingPadHostControlExecutor(
            session,
            new ValidationRunContext(),
            gate,
            ExchangeTimeout,
            () => poisoned = true);

        using var cancellation = new CancellationTokenSource();
        await executor.ApplyValidated2200Async(cancellation.Token);

        Assert.False(poisoned);
        Assert.Equal([1, 2], session.TransactionIds);
        Assert.Equal(
            [
                (ClassId: (byte)0x0D, CommandId: (byte)0x10, Payload: "000600"),
                (ClassId: (byte)0x0D, CommandId: (byte)0x01, Payload: "01052C"),
            ],
            session.Commands.Select(command => (
                ClassId: command.ClassId,
                CommandId: command.CommandId,
                Payload: Convert.ToHexString(command.Arguments.Span))).ToArray());
        Assert.Equal([true, false], session.CancellationCanBeCanceled);
    }

    [Fact]
    public async Task AcquireAsync_UsesPreparationThenRequestedTargetOnce()
    {
        var session = new ScriptedSession(
            Success([0x00, 0x06, 0x00]),
            Success([0x01, 0x05, 0x0A]));
        CoolingPadHostControlExecutor executor = Create(session, static () => { });

        await executor.AcquireAsync(
            new CoolingPadFanTarget(500),
            CancellationToken.None);

        Assert.Equal(
            [
                (CommandId: (byte)0x10, Payload: "000600"),
                (CommandId: (byte)0x01, Payload: "01050A"),
            ],
            session.Commands.Select(command => (
                CommandId: command.CommandId,
                Payload: Convert.ToHexString(command.Arguments.Span))).ToArray());
        Assert.Equal([1, 2], session.TransactionIds);
    }

    [Fact]
    public async Task UpdateAsync_SubmitsOnlyTheOwnedTargetUpdate()
    {
        var session = new ScriptedSession(Success([0x01, 0x05, 0x40]));
        CoolingPadHostControlExecutor executor = Create(session, static () => { });

        await executor.UpdateAsync(
            new CoolingPadFanTarget(3_200),
            CancellationToken.None);

        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal(0x01, command.CommandId);
        Assert.Equal("010540", Convert.ToHexString(command.Arguments.Span));
        Assert.Equal([1], session.TransactionIds);
    }

    [Fact]
    public async Task ReleaseAsync_SubmitsOnlyTheTypedReleaseRequest()
    {
        var session = new ScriptedSession(Success([0x00, 0x06, 0x00]));
        CoolingPadHostControlExecutor executor = Create(session, static () => { });

        await executor.ReleaseAsync(CancellationToken.None);

        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal(0x10, command.CommandId);
        Assert.Equal("000600", Convert.ToHexString(command.Arguments.Span));
        Assert.Equal([1], session.TransactionIds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SingleCommandMismatchOrTimeoutPoisonsWithoutRetry(bool mismatch)
    {
        HidSessionResult<ReadOnlyMemory<byte>> result = mismatch
            ? Success([0x00, 0x06, 0x01])
            : new(default, HidSessionFailureKind.Timeout);
        var session = new ScriptedSession(result);
        int poisonCount = 0;
        CoolingPadHostControlExecutor executor = Create(
            session,
            () => poisonCount++);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => executor.ReleaseAsync(CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(1, poisonCount);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task ApplyValidated2200Async_PreCanceledPerformsNoSubmission()
    {
        var session = new ScriptedSession();
        var context = new ValidationRunContext();
        var executor = new CoolingPadHostControlExecutor(
            session,
            context,
            new SemaphoreSlim(1, 1),
            ExchangeTimeout,
            static () => throw new InvalidOperationException("must not poison"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ApplyValidated2200Async(cancellation.Token));

        Assert.Empty(session.Commands);
        Assert.True(context.TryAllocateTransaction(out byte first));
        Assert.Equal(1, first);
    }

    [Fact]
    public async Task ApplyValidated2200Async_ReservesBothBeforeFirstSubmission()
    {
        var context = new ValidationRunContext();
        for (int index = 0; index < byte.MaxValue - 1; index++)
        {
            Assert.True(context.TryAllocateTransaction(out _));
        }
        var session = new ScriptedSession();
        bool poisoned = false;
        var executor = new CoolingPadHostControlExecutor(
            session,
            context,
            new SemaphoreSlim(1, 1),
            ExchangeTimeout,
            () => poisoned = true);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => executor.ApplyValidated2200Async(CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(HidSessionFailureKind.Exhausted, failure.FailureKind);
        Assert.False(poisoned);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task ApplyValidated2200Async_KnownFirstRejectionDoesNotPoisonOrContinue()
    {
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.ProtocolValidation,
            HidSessionFailureKind.ProtocolFailure,
            NativeWin32Error: null,
            TransferredByteCount: null)
        {
            ProtocolValidationFailure = HidProtocolValidationFailure.Status,
        };
        var rejected = new HidSessionResult<ReadOnlyMemory<byte>>(
            default,
            HidSessionFailureKind.ProtocolFailure)
        {
            Diagnostic = diagnostic,
        };
        var session = new ScriptedSession(rejected);
        bool poisoned = false;
        var executor = Create(session, () => poisoned = true);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => executor.ApplyValidated2200Async(CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.False(poisoned);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task ApplyValidated2200Async_SecondFailurePoisonsWithoutRetry()
    {
        var session = new ScriptedSession(
            Success([0x00, 0x06, 0x00]),
            new(default, HidSessionFailureKind.Timeout));
        int poisonCount = 0;
        var executor = Create(session, () => poisonCount++);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => executor.ApplyValidated2200Async(CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(1, poisonCount);
        Assert.Equal(2, session.Commands.Count);
    }

    [Theory]
    [InlineData("000601")]
    [InlineData("01052D")]
    public async Task ApplyValidated2200Async_MismatchedAckPoisons(string finalPayload)
    {
        byte[] payload = Convert.FromHexString(finalPayload);
        var session = finalPayload.StartsWith("00", StringComparison.Ordinal)
            ? new ScriptedSession(Success(payload))
            : new ScriptedSession(
                Success([0x00, 0x06, 0x00]),
                Success(payload));
        int poisonCount = 0;
        var executor = Create(session, () => poisonCount++);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => executor.ApplyValidated2200Async(CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(1, poisonCount);
    }

    private static CoolingPadHostControlExecutor Create(
        IRazerCommandSession session,
        Action poison) => new(
            session,
            new ValidationRunContext(),
            new SemaphoreSlim(1, 1),
            ExchangeTimeout,
            poison);

    private static HidSessionResult<ReadOnlyMemory<byte>> Success(byte[] payload) =>
        new(payload, null);

    private sealed class ScriptedSession(
        params HidSessionResult<ReadOnlyMemory<byte>>[] results)
        : IRazerCommandSession
    {
        private readonly Queue<HidSessionResult<ReadOnlyMemory<byte>>> results =
            new(results);

        public RazerCommandSessionDisposition Disposition =>
            RazerCommandSessionDisposition.Healthy;

        public List<RazerCommandSpec> Commands { get; } = [];

        public List<byte> TransactionIds { get; } = [];

        public List<bool> CancellationCanBeCanceled { get; } = [];

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            TransactionIds.Add(assignTransactionId().TransactionId);
            CancellationCanBeCanceled.Add(cancellationToken.CanBeCanceled);
            return Task.FromResult(results.Dequeue());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
