using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProductionCoolingPadPortTests
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x09)]
    [InlineData(0x2C)]
    [InlineData(0x41)]
    [InlineData(0xFF)]
    public async Task ObserveFanAsync_PreservesOpaqueByteWithoutAssigningSemantics(
        byte rawByte)
    {
        var session = new ScriptedSession(
            new HidSessionResult<ReadOnlyMemory<byte>>(
                new byte[] { 0x01, 0x05, rawByte },
                null));
        await using var port = new ProductionCoolingPadPort(
            session,
            session,
            new ValidationRunContext(),
            ExchangeTimeout);

        HidSessionResult<CoolingPadFanObservation> result =
            await port.ObserveFanAsync(CancellationToken.None);

        Assert.Null(result.Failure);
        Assert.Equal(rawByte, result.Value.OpaqueValue);
        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal((0x0D, 0x81), (command.ClassId, command.CommandId));
        Assert.Equal([0x01, 0x05, 0x00], command.Arguments.ToArray());
        Assert.Equal(3, command.ExpectedPayloadLength);
        Assert.Equal([1], session.TransactionIds);
    }

    [Fact]
    public async Task ObserveFanAsync_AllocatesDistinctTransactions()
    {
        var session = new ScriptedSession(
            Success(0x11),
            Success(0x22));
        await using var port = new ProductionCoolingPadPort(
            session,
            session,
            new ValidationRunContext(),
            ExchangeTimeout);

        await port.ObserveFanAsync(CancellationToken.None);
        await port.ObserveFanAsync(CancellationToken.None);

        Assert.Equal([1, 2], session.TransactionIds);
    }

    [Fact]
    public async Task ObserveFanAsync_ExhaustionReturnsBeforeSessionSubmission()
    {
        var context = new ValidationRunContext();
        ConsumeTransactions(context, byte.MaxValue);
        var session = new ScriptedSession();
        await using var port = new ProductionCoolingPadPort(
            session,
            session,
            context,
            ExchangeTimeout);

        HidSessionResult<CoolingPadFanObservation> result =
            await port.ObserveFanAsync(CancellationToken.None);

        Assert.Equal(HidSessionFailureKind.Exhausted, result.Failure);
        Assert.Empty(session.Commands);
        Assert.Empty(session.TransactionIds);
    }

    [Fact]
    public async Task ObserveFanAsync_LastTransactionSubmitsThenExhaustsBeforeSession()
    {
        var context = new ValidationRunContext();
        ConsumeTransactions(context, byte.MaxValue - 1);
        var session = new ScriptedSession(Success(0x2C));
        await using var port = new ProductionCoolingPadPort(
            session,
            session,
            context,
            ExchangeTimeout);

        HidSessionResult<CoolingPadFanObservation> last =
            await port.ObserveFanAsync(CancellationToken.None);
        HidSessionResult<CoolingPadFanObservation> exhausted =
            await port.ObserveFanAsync(CancellationToken.None);

        Assert.Null(last.Failure);
        Assert.Equal(HidSessionFailureKind.Exhausted, exhausted.Failure);
        Assert.Equal([byte.MaxValue], session.TransactionIds);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task ObserveFanAsync_PropagatesFailureWithoutRetry()
    {
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.GetCompletion,
            HidSessionFailureKind.Timeout,
            NativeWin32Error: null,
            TransferredByteCount: null);
        var session = new ScriptedSession(
            new HidSessionResult<ReadOnlyMemory<byte>>(
                default,
                HidSessionFailureKind.Timeout)
            {
                Diagnostic = diagnostic,
            });
        await using var port = new ProductionCoolingPadPort(
            session,
            session,
            new ValidationRunContext(),
            ExchangeTimeout);

        HidSessionResult<CoolingPadFanObservation> result =
            await port.ObserveFanAsync(CancellationToken.None);

        Assert.Equal(HidSessionFailureKind.Timeout, result.Failure);
        Assert.Equal(diagnostic, result.Diagnostic);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task ObserveFanAsync_RejectsMalformedEcho()
    {
        var session = new ScriptedSession(
            new HidSessionResult<ReadOnlyMemory<byte>>(
                new byte[] { 0x01, 0x04, 0x2C },
                null));
        await using var port = new ProductionCoolingPadPort(
            session,
            session,
            new ValidationRunContext(),
            ExchangeTimeout);

        HidSessionResult<CoolingPadFanObservation> result =
            await port.ObserveFanAsync(CancellationToken.None);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
    }

    [Fact]
    public async Task ObserveFanAsync_CanceledBeforeGateDoesNotReachSession()
    {
        var session = new ScriptedSession();
        var context = new ValidationRunContext();
        await using var port = new ProductionCoolingPadPort(
            session,
            session,
            context,
            ExchangeTimeout);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        HidSessionResult<CoolingPadFanObservation> result =
            await port.ObserveFanAsync(cancellation.Token);

        Assert.Equal(HidSessionFailureKind.Canceled, result.Failure);
        Assert.Empty(session.Commands);
        Assert.True(context.TryAllocateTransaction(out byte nextTransaction));
        Assert.Equal(1, nextTransaction);
    }

    [Fact]
    public async Task DisposeAsync_DisposesOwnedSessionOnceAndBlocksFurtherReads()
    {
        var session = new ScriptedSession();
        var port = new ProductionCoolingPadPort(
            session,
            session,
            new ValidationRunContext(),
            ExchangeTimeout);

        await port.DisposeAsync();
        await port.DisposeAsync();
        HidSessionResult<CoolingPadFanObservation> result =
            await port.ObserveFanAsync(CancellationToken.None);

        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(HidSessionFailureKind.Disposed, result.Failure);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task DormantFanExecutor_SharesPortGateAndTransactionAllocator()
    {
        var session = new ScriptedSession(
            Success(0x00),
            new HidSessionResult<ReadOnlyMemory<byte>>(
                new byte[] { 0x01, 0x05, 0x2C },
                null));
        await using var port = new ProductionCoolingPadPort(
            session,
            session,
            new ValidationRunContext(),
            ExchangeTimeout);
        CoolingPadHostControlExecutor executor =
            port.CreateDormantHostControlExecutor();

        HidSessionResult<CoolingPadFanObservation> observation =
            await port.ObserveFanAsync(CancellationToken.None);
        await executor.UpdateAsync(
            new CoolingPadFanTarget(2_200),
            CancellationToken.None);

        Assert.Null(observation.Failure);
        Assert.Equal([1, 2], session.TransactionIds);
        Assert.Equal(
            [(byte)0x81, (byte)0x01],
            session.Commands.Select(command => command.CommandId).ToArray());
    }

    private static HidSessionResult<ReadOnlyMemory<byte>> Success(byte rawByte) =>
        new(new byte[] { 0x01, 0x05, rawByte }, null);

    private static void ConsumeTransactions(ValidationRunContext context, int count)
    {
        for (int index = 0; index < count; index++)
        {
            Assert.True(context.TryAllocateTransaction(out _));
        }
    }

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

        public int DisposeCount { get; private set; }

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            TransactionIds.Add(assignTransactionId().TransactionId);
            return Task.FromResult(results.Dequeue());
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
