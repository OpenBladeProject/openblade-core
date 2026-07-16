using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class TemporaryFullChargeCommandClientTests
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);

    [Fact]
    public void TypedClientContract_HasOneQueryAndNoSetterShapedMember()
    {
        Assert.Equal(["QueryAsync"], typeof(ITemporaryFullChargeCommandClient)
            .GetMethods().Select(method => method.Name).ToArray());
        Assert.DoesNotContain(typeof(ITemporaryFullChargeCommandClient).GetMembers(), member =>
            member.Name.Contains("Set", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Apply", StringComparison.OrdinalIgnoreCase)
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
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x01 })]
    [InlineData(new byte[] { 0x00, 0x04 })]
    public async Task QueryAsync_RejectsNonClosedPayload(byte[] payload)
    {
        var client = new TemporaryFullChargeCommandClient(
            new ScriptedSession(
                new HidSessionResult<ReadOnlyMemory<byte>>(payload, null)),
            ExchangeTimeout);

        HidSessionResult<bool> result = await client.QueryAsync(1, default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(HidProtocolValidationFailure.PayloadValidator,
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
