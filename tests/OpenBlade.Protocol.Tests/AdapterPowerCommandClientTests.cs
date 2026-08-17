using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class AdapterPowerCommandClientTests
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData("0211", AdapterPowerState.UsbCPower45W)]
    [InlineData("0311", AdapterPowerState.UsbCPower60W)]
    [InlineData("0411", AdapterPowerState.UsbCPower65W)]
    [InlineData("0511", AdapterPowerState.UsbCPower80W)]
    [InlineData("0611", AdapterPowerState.UsbCPower90W)]
    [InlineData("0711", AdapterPowerState.UsbCPower100W)]
    [InlineData("1111", AdapterPowerState.FullPower280W)]
    [InlineData("1211", AdapterPowerState.FullPower330W)]
    [InlineData("FFFF", AdapterPowerState.Unknown)]
    public async Task QueryAsync_UsesExactCommandAndReturnsTypedState(
        string responseHex,
        AdapterPowerState expected)
    {
        var session = new ScriptedSession(
            new HidSessionResult<ReadOnlyMemory<byte>>(
                Convert.FromHexString(responseHex),
                null));
        var client = new AdapterPowerCommandClient(session, ExchangeTimeout);

        HidSessionResult<AdapterPowerState> result =
            await client.QueryAsync(0x31, default);

        Assert.Null(result.Failure);
        Assert.Equal(expected, result.Value);
        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal((0x07, 0x8C), (command.ClassId, command.CommandId));
        Assert.Equal([0x00, 0x00], command.Arguments.ToArray());
        Assert.Equal(2, command.ExpectedPayloadLength);
        Assert.True(command.PayloadValidator!(Convert.FromHexString(responseHex)));
        Assert.Equal(0x31, session.TransactionIds.Single());
    }

    [Fact]
    public async Task QueryAsync_MalformedReplyFailsClosed()
    {
        var client = new AdapterPowerCommandClient(
            new ScriptedSession(new HidSessionResult<ReadOnlyMemory<byte>>(
                new byte[] { 0x07 },
                null)),
            ExchangeTimeout);

        HidSessionResult<AdapterPowerState> result =
            await client.QueryAsync(1, default);

        Assert.Equal(AdapterPowerState.Unknown, result.Value);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(
            HidProtocolValidationFailure.PayloadValidator,
            result.Diagnostic?.ProtocolValidationFailure);
    }

    [Fact]
    public async Task QueryAsync_PropagatesTransportFailureAndDiagnostic()
    {
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.GetCompletion,
            HidSessionFailureKind.Timeout,
            NativeWin32Error: null,
            TransferredByteCount: null);
        var client = new AdapterPowerCommandClient(
            new ScriptedSession(
                new HidSessionResult<ReadOnlyMemory<byte>>(
                    default,
                    HidSessionFailureKind.Timeout)
                {
                    Diagnostic = diagnostic,
                }),
            ExchangeTimeout);

        HidSessionResult<AdapterPowerState> result =
            await client.QueryAsync(1, default);

        Assert.Equal(AdapterPowerState.Unknown, result.Value);
        Assert.Equal(HidSessionFailureKind.Timeout, result.Failure);
        Assert.Equal(diagnostic, result.Diagnostic);
    }

    [Fact]
    public async Task QueryAsync_RejectsZeroTransactionBeforeTransport()
    {
        var session = new ScriptedSession();
        var client = new AdapterPowerCommandClient(session, ExchangeTimeout);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.QueryAsync(0, default));
        Assert.Empty(session.Commands);
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
