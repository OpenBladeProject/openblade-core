using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CoolingPadLightingExecutorTests
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task SubmitFrameAsync_UsesExactSetOnlyPayloadOnce()
    {
        var session = new ScriptedSession();
        session.SubmitResults.Enqueue(Success([]));
        await using ProductionCoolingPadPort port = CreatePort(session);
        ICoolingPadLightingExecutor executor = port.CreateDormantLightingExecutor();
        var frame = new CoolingPadLightingFrame(
            Enumerable.Repeat(
                new RgbColor(0x12, 0x34, 0x56),
                CoolingPadLightingFrame.SlotCount).ToArray());

        CoolingPadLightingSinkResult result =
            await executor.SubmitFrameAsync(frame, default);

        Assert.True(result.Success);
        RecordedOperation operation = Assert.Single(session.Operations);
        Assert.Equal(SubmissionKind.SetOnly, operation.Kind);
        Assert.Equal((0x0F, 0x03), (operation.Command.ClassId, operation.Command.CommandId));
        Assert.Equal(1, operation.TransactionId);
        byte[] payload = operation.Command.Arguments.ToArray();
        Assert.Equal(CoolingPadLightingProtocol.FramePayloadLength, payload.Length);
        Assert.Equal([0x00, 0x00, 0x00, 0x00, 0x11], payload[..5]);
        Assert.All(
            Enumerable.Range(0, CoolingPadLightingFrame.SlotCount),
            slot => Assert.Equal(
                [0x12, 0x34, 0x56],
                payload[(5 + (slot * 3))..(8 + (slot * 3))]));
    }

    [Fact]
    public async Task BrightnessQueryAndSet_UseExactRequestResponseContracts()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x7F]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x7F]));
        await using ProductionCoolingPadPort port = CreatePort(session);
        ICoolingPadLightingExecutor executor = port.CreateDormantLightingExecutor();

        HidSessionResult<CoolingPadBrightnessReading> queried =
            await executor.QueryBrightnessAsync(default);
        CoolingPadLightingSinkResult set =
            await executor.SetBrightnessAsync(50, default);

        Assert.Null(queried.Failure);
        Assert.Equal(0x7F, queried.Value.Raw);
        Assert.Equal(50, queried.Value.Percent);
        Assert.True(set.Success);
        Assert.Equal(
            [
                (SubmissionKind.RequestResponse, (byte)0x0F, (byte)0x84, "010500", (byte)1),
                (SubmissionKind.RequestResponse, (byte)0x0F, (byte)0x04, "01007F", (byte)2),
            ],
            session.Operations.Select(operation => (
                operation.Kind,
                operation.Command.ClassId,
                operation.Command.CommandId,
                Convert.ToHexString(operation.Command.Arguments.Span),
                operation.TransactionId)).ToArray());
    }

    [Fact]
    public async Task RawBrightnessRestore_PreservesExactQueriedByte()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x80]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x80]));
        await using ProductionCoolingPadPort port = CreatePort(session);
        ICoolingPadLightingExecutor executor = port.CreateDormantLightingExecutor();

        HidSessionResult<CoolingPadBrightnessReading> queried =
            await executor.QueryBrightnessAsync(default);
        CoolingPadLightingSinkResult restored =
            await executor.SetBrightnessRawAsync(queried.Value.Raw, default);

        Assert.Null(queried.Failure);
        Assert.Equal(0x80, queried.Value.Raw);
        Assert.True(restored.Success);
        Assert.Equal(
            ["010500", "010080"],
            session.Operations.Select(operation =>
                Convert.ToHexString(operation.Command.Arguments.Span)).ToArray());
    }

    [Fact]
    public async Task SetBrightnessAsync_AckMismatchPoisonsWithoutRetry()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x80]));
        await using ProductionCoolingPadPort port = CreatePort(session);
        ICoolingPadLightingExecutor executor = port.CreateDormantLightingExecutor();

        CoolingPadLightingSinkResult mismatch =
            await executor.SetBrightnessAsync(50, default);
        HidSessionResult<CoolingPadBrightnessReading> poisoned =
            await executor.QueryBrightnessAsync(default);
        HidSessionResult<CoolingPadFanObservation> fan =
            await port.ObserveFanAsync(default);

        Assert.Equal(CoolingPadLightingSinkFailure.Indeterminate, mismatch.Failure);
        Assert.Equal(HidSessionFailureKind.Poisoned, poisoned.Failure);
        Assert.Equal(HidSessionFailureKind.Poisoned, fan.Failure);
        Assert.Single(session.Operations);
    }

    [Fact]
    public async Task QueryBrightnessAsync_MalformedPayloadFailsWithoutPoisoningWrites()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x7F]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x7F]));
        await using ProductionCoolingPadPort port = CreatePort(session);
        ICoolingPadLightingExecutor executor = port.CreateDormantLightingExecutor();

        HidSessionResult<CoolingPadBrightnessReading> malformed =
            await executor.QueryBrightnessAsync(default);
        CoolingPadLightingSinkResult set =
            await executor.SetBrightnessAsync(50, default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, malformed.Failure);
        Assert.True(set.Success);
        Assert.Equal([1, 2], session.Operations.Select(operation => operation.TransactionId));
    }

    [Fact]
    public async Task SubmitFrameAsync_MissingSetOnlySupportIsKnownNotApplied()
    {
        var context = new ValidationRunContext();
        var session = new ExecuteOnlySession();
        await using var port = new ProductionCoolingPadPort(
            session,
            session,
            context,
            ExchangeTimeout);
        ICoolingPadLightingExecutor executor = port.CreateDormantLightingExecutor();

        CoolingPadLightingSinkResult result = await executor.SubmitFrameAsync(
            SolidFrame(new RgbColor(1, 2, 3)),
            default);

        Assert.Equal(CoolingPadLightingSinkFailure.KnownNotApplied, result.Failure);
        Assert.Empty(session.Commands);
        Assert.True(context.TryAllocateTransaction(out byte first));
        Assert.Equal(1, first);
    }

    [Fact]
    public async Task SubmitFrameAsync_PreCanceledDoesNotReserveOrSubmit()
    {
        var context = new ValidationRunContext();
        var session = new ScriptedSession();
        await using ProductionCoolingPadPort port = CreatePort(session, context);
        ICoolingPadLightingExecutor executor = port.CreateDormantLightingExecutor();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.SubmitFrameAsync(
                SolidFrame(new RgbColor(1, 2, 3)),
                cancellation.Token));

        Assert.Empty(session.Operations);
        Assert.True(context.TryAllocateTransaction(out byte first));
        Assert.Equal(1, first);
    }

    [Fact]
    public async Task SubmitFrameAsync_SubmittedCancellationPoisonsWithoutRetry()
    {
        var session = new ScriptedSession();
        session.SubmitResults.Enqueue(new(default, HidSessionFailureKind.Canceled)
        {
            Diagnostic = new HidSessionFailureDiagnostic(
                HidSessionFailurePhase.SetCompletion,
                HidSessionFailureKind.Canceled,
                NativeWin32Error: null,
                TransferredByteCount: null),
        });
        await using ProductionCoolingPadPort port = CreatePort(session);
        ICoolingPadLightingExecutor executor = port.CreateDormantLightingExecutor();

        CoolingPadLightingSinkResult result = await executor.SubmitFrameAsync(
            SolidFrame(new RgbColor(1, 2, 3)),
            default);
        CoolingPadLightingSinkResult blocked =
            await executor.SetBrightnessAsync(50, default);

        Assert.Equal(CoolingPadLightingSinkFailure.Indeterminate, result.Failure);
        Assert.Equal(CoolingPadLightingSinkFailure.KnownNotApplied, blocked.Failure);
        Assert.Single(session.Operations);
    }

    [Fact]
    public async Task SubmitFrameAsync_SetSubmitFailurePoisonsWithoutRetry()
    {
        var session = new ScriptedSession();
        session.SubmitResults.Enqueue(new(default, HidSessionFailureKind.NativeIoFailure)
        {
            Diagnostic = new HidSessionFailureDiagnostic(
                HidSessionFailurePhase.SetSubmit,
                HidSessionFailureKind.NativeIoFailure,
                NativeWin32Error: 31,
                TransferredByteCount: null),
        });
        await using ProductionCoolingPadPort port = CreatePort(session);
        ICoolingPadLightingExecutor executor = port.CreateDormantLightingExecutor();

        CoolingPadLightingSinkResult result = await executor.SubmitFrameAsync(
            SolidFrame(new RgbColor(1, 2, 3)),
            default);
        CoolingPadLightingSinkResult blocked =
            await executor.SetBrightnessAsync(50, default);

        Assert.Equal(CoolingPadLightingSinkFailure.Indeterminate, result.Failure);
        Assert.Equal(CoolingPadLightingSinkFailure.KnownNotApplied, blocked.Failure);
        Assert.Single(session.Operations);
    }

    [Fact]
    public async Task Operations_SerializeThroughProductionPortGate()
    {
        var session = new ScriptedSession(blockFirstExecute: true);
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x7F]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x7F]));
        await using ProductionCoolingPadPort port = CreatePort(session);
        ICoolingPadLightingExecutor executor = port.CreateDormantLightingExecutor();

        Task<HidSessionResult<CoolingPadBrightnessReading>> query =
            executor.QueryBrightnessAsync(default);
        await session.FirstExecuteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<CoolingPadLightingSinkResult> set =
            executor.SetBrightnessAsync(50, default);
        await Task.Yield();

        Assert.Single(session.Operations);
        session.ReleaseFirstExecute();
        Assert.Null((await query).Failure);
        Assert.True((await set).Success);
        Assert.Equal([1, 2], session.Operations.Select(operation => operation.TransactionId));
    }

    [Fact]
    public async Task LightingAndFanObservation_SerializeThroughSamePortGate()
    {
        var session = new ScriptedSession(blockFirstExecute: true);
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x7F]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x2C]));
        await using ProductionCoolingPadPort port = CreatePort(session);
        ICoolingPadLightingExecutor executor = port.CreateDormantLightingExecutor();

        Task<HidSessionResult<CoolingPadBrightnessReading>> lighting =
            executor.QueryBrightnessAsync(default);
        await session.FirstExecuteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<HidSessionResult<CoolingPadFanObservation>> fan =
            port.ObserveFanAsync(default);
        await Task.Yield();

        Assert.Single(session.Operations);
        session.ReleaseFirstExecute();
        Assert.Null((await lighting).Failure);
        Assert.Null((await fan).Failure);
        Assert.Equal([1, 2], session.Operations.Select(operation => operation.TransactionId));
    }

    [Fact]
    public async Task DisposeAsync_WaitsForInFlightLightingAndBlocksLaterSubmission()
    {
        var session = new ScriptedSession(blockFirstExecute: true);
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x7F]));
        var port = CreatePort(session);
        ICoolingPadLightingExecutor executor = port.CreateDormantLightingExecutor();

        Task<CoolingPadLightingSinkResult> setting =
            executor.SetBrightnessAsync(50, default);
        await session.FirstExecuteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task disposing = port.DisposeAsync().AsTask();
        await Task.Yield();
        Assert.False(disposing.IsCompleted);

        session.ReleaseFirstExecute();
        Assert.True((await setting).Success);
        await disposing;
        CoolingPadLightingSinkResult blocked = await executor.SetBrightnessAsync(50, default);

        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(CoolingPadLightingSinkFailure.KnownNotApplied, blocked.Failure);
        Assert.Single(session.Operations);
    }

    [Fact]
    public async Task TransactionExhaustionStopsBeforeLightingSubmission()
    {
        var context = new ValidationRunContext();
        for (int index = 0; index < byte.MaxValue; index++)
        {
            Assert.True(context.TryAllocateTransaction(out _));
        }
        var session = new ScriptedSession();
        await using ProductionCoolingPadPort port = CreatePort(session, context);
        ICoolingPadLightingExecutor executor = port.CreateDormantLightingExecutor();

        CoolingPadLightingSinkResult result = await executor.SubmitFrameAsync(
            SolidFrame(new RgbColor(1, 2, 3)),
            default);

        Assert.Equal(CoolingPadLightingSinkFailure.KnownNotApplied, result.Failure);
        Assert.Empty(session.Operations);
    }

    [Fact]
    public async Task Construction_PerformsNoSessionIo()
    {
        var session = new ScriptedSession();
        await using ProductionCoolingPadPort port = CreatePort(session);

        ICoolingPadLightingExecutor executor = port.CreateDormantLightingExecutor();

        Assert.NotNull(executor);
        Assert.Empty(session.Operations);
    }

    private static ProductionCoolingPadPort CreatePort(
        ScriptedSession session,
        ValidationRunContext? context = null) => new(
            session,
            session,
            context ?? new ValidationRunContext(),
            ExchangeTimeout);

    private static CoolingPadLightingFrame SolidFrame(RgbColor color) => new(
        Enumerable.Repeat(color, CoolingPadLightingFrame.SlotCount).ToArray());

    private static HidSessionResult<ReadOnlyMemory<byte>> Success(byte[] payload) =>
        new(payload, null);

    private enum SubmissionKind
    {
        RequestResponse,
        SetOnly,
    }

    private readonly record struct RecordedOperation(
        SubmissionKind Kind,
        RazerCommandSpec Command,
        byte TransactionId);

    private sealed class ScriptedSession(bool blockFirstExecute = false)
        : IRazerCommandSession, IRazerSetFeatureCommandSession
    {
        private int executeCalls;
        private readonly TaskCompletionSource releaseExecute =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RazerCommandSessionDisposition Disposition =>
            RazerCommandSessionDisposition.Healthy;

        public Queue<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteResults { get; } = [];

        public Queue<HidSessionResult<ReadOnlyMemory<byte>>> SubmitResults { get; } = [];

        public List<RecordedOperation> Operations { get; } = [];

        public TaskCompletionSource FirstExecuteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref executeCalls);
            Operations.Add(new(
                SubmissionKind.RequestResponse,
                command,
                assignTransactionId().TransactionId));
            FirstExecuteStarted.TrySetResult();
            if (blockFirstExecute && call == 1)
            {
                await releaseExecute.Task.WaitAsync(cancellationToken);
            }
            return ExecuteResults.Dequeue();
        }

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            Operations.Add(new(
                SubmissionKind.SetOnly,
                command,
                assignTransactionId().TransactionId));
            return Task.FromResult(SubmitResults.Dequeue());
        }

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitBatchAsync(
            IReadOnlyList<RazerCommandSpec> commands,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Lighting frames use one SET-only report.");

        public void ReleaseFirstExecute() => releaseExecute.TrySetResult();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ExecuteOnlySession : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition =>
            RazerCommandSessionDisposition.Healthy;

        public List<RazerCommandSpec> Commands { get; } = [];

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(Success([0x01, 0x05, 0x7F]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
