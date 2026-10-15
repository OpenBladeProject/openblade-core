using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class TemporaryFullChargeCommandClientTests
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);

    [Fact]
    public void TypedClientContract_ExposesOnlyClosedQueryAndSetter()
    {
        Assert.Equal(["QueryAsync", "SetAsync"], typeof(ITemporaryFullChargeCommandClient)
            .GetMethods().Select(method => method.Name).Order().ToArray());
        Assert.DoesNotContain(typeof(ITemporaryFullChargeCommandClient).GetMembers(), member =>
            member.Name.Contains("Apply", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Catalog", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0x00, false)]
    [InlineData(0x04, true)]
    public async Task QueryAsync_UsesExactCommandAndReturnsTypedStatus(byte response, bool expected)
    {
        var session = new ScriptedSession(
            new HidSessionResult<ReadOnlyMemory<byte>>(new byte[] { response }, null));
        var client = new TemporaryFullChargeCommandClient(session, ExchangeTimeout);

        HidSessionResult<bool> result = await client.QueryAsync(0x41, default);

        Assert.Null(result.Failure);
        Assert.Equal(expected, result.Value);
        Call call = Assert.Single(session.Calls);
        Assert.Equal(0x07, call.Command.ClassId);
        Assert.Equal(0x8F, call.Command.CommandId);
        Assert.Equal([0x00], call.Command.Arguments.ToArray());
        Assert.Equal(1, call.Command.ExpectedPayloadLength);
        Assert.Equal(0x41, call.AssignTransactionId().TransactionId);
        Assert.Equal(ExchangeTimeout, call.ExchangeTimeout);
    }

    [Theory]
    [InlineData(new byte[] { }, "(empty)")]
    [InlineData(new byte[] { 0x01 }, "01")]
    [InlineData(new byte[] { 0x05 }, "05")]
    [InlineData(new byte[] { 0x00, 0x04 }, "00 04")]
    public async Task QueryAsync_RejectsNonClosedPayload(byte[] payload, string expectedHex)
    {
        var client = new TemporaryFullChargeCommandClient(
            new ScriptedSession(
                new HidSessionResult<ReadOnlyMemory<byte>>(payload, null)),
            ExchangeTimeout);

        HidSessionResult<bool> result = await client.QueryAsync(1, default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(HidProtocolValidationFailure.PayloadValidator,
            result.Diagnostic?.ProtocolValidationFailure);
        Assert.Equal(expectedHex, result.Diagnostic?.RejectedPayloadHex);
    }

    [Theory]
    [InlineData(0x08, false, 0x08)]
    [InlineData(0x0C, true, 0x08)]
    [InlineData(0x01, false, 0x01)]
    [InlineData(0x02, false, 0x02)]
    public async Task QueryAsync_PowerModeControlBitmaskDecodesOnlyWithModelScopedEvidence(
        byte response,
        bool expectedActive,
        byte expectedOtherSettings)
    {
        var scoped = new ScriptedSession(
            new HidSessionResult<ReadOnlyMemory<byte>>(new byte[] { response }, null),
            new HidSessionResult<ReadOnlyMemory<byte>>(new byte[] { response }, null));
        var scopedClient = new TemporaryFullChargeCommandClient(
            scoped,
            ExchangeTimeout,
            decodesPowerModeControlBitmask: true);

        HidSessionResult<bool> accepted = await scopedClient.QueryAsync(1, default);
        HidSessionResult<TemporaryFullChargeReading> reading =
            await scopedClient.QueryReadingAsync(2, default);

        Assert.Null(accepted.Failure);
        Assert.Equal(expectedActive, accepted.Value);
        Assert.Null(reading.Failure);
        Assert.Equal(
            new TemporaryFullChargeReading(expectedActive, expectedOtherSettings),
            reading.Value);
        Assert.True(scoped.Calls[0].Command.PayloadValidator!(new byte[] { response }));

        var strictSession = new ScriptedSession(
            new HidSessionResult<ReadOnlyMemory<byte>>(new byte[] { response }, null));
        var strict = new TemporaryFullChargeCommandClient(strictSession, ExchangeTimeout);
        HidSessionResult<bool> rejected = await strict.QueryAsync(1, default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, rejected.Failure);
        Assert.Equal($"{response:X2}", rejected.Diagnostic?.RejectedPayloadHex);
        Assert.False(Assert.Single(strictSession.Calls).Command.PayloadValidator!(
            new byte[] { response }));
    }

    [Theory]
    [InlineData(new byte[] { 0x10 }, "10")]
    [InlineData(new byte[] { 0x80 }, "80")]
    [InlineData(new byte[] { 0xFF }, "FF")]
    [InlineData(new byte[] { 0x08, 0x00 }, "08 00")]
    public async Task QueryAsync_PowerModeControlBitmaskStillRejectsUndefinedBits(
        byte[] payload,
        string expectedHex)
    {
        var client = new TemporaryFullChargeCommandClient(
            new ScriptedSession(
                new HidSessionResult<ReadOnlyMemory<byte>>(payload, null)),
            ExchangeTimeout,
            decodesPowerModeControlBitmask: true);

        HidSessionResult<bool> result = await client.QueryAsync(1, default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(expectedHex, result.Diagnostic?.RejectedPayloadHex);
    }

    [Theory]
    [InlineData(false, 0x00)]
    [InlineData(true, 0x04)]
    public async Task SetAsync_UsesExactClosedCommandAndEcho(bool enabled, byte payload)
    {
        var session = new ScriptedSession(
            new HidSessionResult<ReadOnlyMemory<byte>>(new byte[] { payload }, null));
        var client = new TemporaryFullChargeCommandClient(session, ExchangeTimeout);

        HidSessionResult<HidUnit> result = await client.SetAsync(0x42, enabled, default);

        Assert.Null(result.Failure);
        Call call = Assert.Single(session.Calls);
        Assert.Equal(0x07, call.Command.ClassId);
        Assert.Equal(0x0F, call.Command.CommandId);
        Assert.Equal([payload], call.Command.Arguments.ToArray());
        Assert.Equal(1, call.Command.ExpectedPayloadLength);
        Assert.Equal(0x42, call.AssignTransactionId().TransactionId);
        Assert.Equal(ExchangeTimeout, call.ExchangeTimeout);
    }

    [Fact]
    public async Task SetAsync_RejectsNonEchoedAcknowledgement()
    {
        var client = new TemporaryFullChargeCommandClient(
            new ScriptedSession(
                new HidSessionResult<ReadOnlyMemory<byte>>(new byte[] { 0x00 }, null)),
            ExchangeTimeout);

        HidSessionResult<HidUnit> result = await client.SetAsync(1, enabled: true, default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(
            HidProtocolValidationFailure.PayloadValidator,
            result.Diagnostic?.ProtocolValidationFailure);
    }

    private sealed class ScriptedSession(params HidSessionResult<ReadOnlyMemory<byte>>[] results)
        : IRazerCommandSession
    {
        private readonly Queue<HidSessionResult<ReadOnlyMemory<byte>>> results = new(results);
        internal List<Call> Calls { get; } = [];
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(new(command, assignTransactionId, exchangeTimeout));
            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed record Call(
        RazerCommandSpec Command,
        Func<TransactionIdAssignment> AssignTransactionId,
        TimeSpan ExchangeTimeout);
}
