using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class FanRpmQueryRunFactoryTests
{
    [Fact]
    public void PublicPort_ExposesOnlyOneClosedTypedQuery()
    {
        string[] methods = typeof(IFanRpmQueryPort).GetMethods()
            .Select(method => method.Name)
            .Order()
            .ToArray();

        Assert.Equal(["QueryAsync"], methods);
        Assert.Contains(typeof(IAsyncDisposable), typeof(IFanRpmQueryPort).GetInterfaces());
        Assert.Empty(typeof(IFanRpmQueryPort).GetProperties());
        Assert.DoesNotContain(typeof(IFanRpmQueryPort).GetMembers(), member =>
            member.Name.Contains("Set", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Apply", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Command", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task QueryAsync_QueriesBothFixedSelectorsOnceInOneSessionAndReturnsMax()
    {
        var session = new FanSession();
        var actorFactory = new ActorFactory(Actor(session));
        var factory = new FanRpmQueryRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            TimeProvider.System,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            new PendingDisposalRegistry());

        IFanRpmQueryPort port = Assert.IsAssignableFrom<IFanRpmQueryPort>(
            (await factory.OpenAsync(default)).Value);
        HidSessionResult<FanRpmReadings> first = await port.QueryAsync(default);
        HidSessionResult<FanRpmReadings> second = await port.QueryAsync(default);

        Assert.Null(first.Failure);
        Assert.Equal(new FanRpmReadings(3200, 3000), first.Value);
        Assert.Equal(3200, first.Value!.MaxRpm);
        Assert.Equal(HidSessionFailureKind.Disposed, second.Failure);
        Assert.Equal(1, actorFactory.OpenCount);
        Assert.Equal([1, 2], session.TransactionIds);
        Assert.All(session.Commands, command =>
        {
            Assert.Equal(0x0D, command.ClassId);
            Assert.Equal(0x88, command.CommandId);
            Assert.Equal(3, command.ExpectedPayloadLength);
            Assert.NotNull(command.PayloadValidator);
        });
        Assert.Equal(
            [new byte[] { 0x01, 0x01, 0x00 }, new byte[] { 0x01, 0x02, 0x00 }],
            session.Arguments);
        Assert.Equal(1, session.DisposeCount);

        await port.DisposeAsync();
        Assert.Equal(1, session.DisposeCount);
    }

    private static ValidationActor Actor(IRazerCommandSession session) =>
        new(session, new BatteryCommands(), new TemporaryCommands());

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        internal int OpenCount { get; private set; }

        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context)
        {
            OpenCount++;
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
        }
    }

    private sealed class FanSession : IRazerCommandSession
    {
        internal List<byte> TransactionIds { get; } = [];
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal List<byte[]> Arguments { get; } = [];
        internal int DisposeCount { get; private set; }

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            TransactionIdAssignment assignment = assignTransactionId();
            TransactionIds.Add(assignment.TransactionId);
            Commands.Add(command);
            Arguments.Add(command.Arguments.ToArray());
            byte selector = command.Arguments.Span[1];
            ReadOnlyMemory<byte> payload = selector switch
            {
                0x01 => new byte[] { 0x01, 0x01, 0x20 },
                0x02 => new byte[] { 0x01, 0x02, 0x1E },
                _ => throw new InvalidOperationException(),
            };
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BatteryCommands : IBatteryCommandClient
    {
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            BatteryValidationSetting setting,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TemporaryCommands : ITemporaryFullChargeCommandClient
    {
        public Task<HidSessionResult<bool>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
