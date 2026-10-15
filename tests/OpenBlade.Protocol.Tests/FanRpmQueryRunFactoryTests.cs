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

    [Fact]
    public void Rz090581ServiceFactory_UsesItsModelSpecificSharedFeatureCommandGate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rz090581Factory = Assert.IsType<FanRpmQueryRunFactory>(
            BatteryValidationFactory.CreateWindowsRz090581FanRpmForService(
                compatibilityPolicy: null));
        var rz090528Factory = Assert.IsType<FanRpmQueryRunFactory>(
            BatteryValidationFactory.CreateWindowsRz090528FanRpmQuery());

        Assert.Same(
            Rz090581FeatureCommandGate.Shared,
            rz090581Factory.SharedCommandGate);
        Assert.Null(rz090528Factory.SharedCommandGate);
        Assert.NotSame(
            Rz090528CommandSessionGate.Shared,
            rz090581Factory.SharedCommandGate);
    }

    [Fact]
    public async Task QueryAsync_HoldsSharedFeatureCommandGateThroughEachSetGetResponse()
    {
        using var sharedCommandGate = new SemaphoreSlim(1, 1);
        var session = new BlockingFanSession();
        var factory = new FanRpmQueryRunFactory(
            static () => new ValidationRunContext(),
            new ActorFactory(Actor(session)),
            TimeProvider.System,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            new PendingDisposalRegistry(),
            sharedCommandGate);
        IFanRpmQueryPort port = Assert.IsAssignableFrom<IFanRpmQueryPort>(
            (await factory.OpenAsync(default)).Value);

        Task<HidSessionResult<FanRpmReadings>> query = port.QueryAsync(default);
        await session.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(sharedCommandGate.Wait(0));

        session.ReleaseFirst.TrySetResult();
        await session.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(sharedCommandGate.Wait(0));

        session.ReleaseSecond.TrySetResult();
        HidSessionResult<FanRpmReadings> result =
            await query.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Null(result.Failure);
        Assert.Equal(new FanRpmReadings(3200, 3000), result.Value);
        Assert.True(sharedCommandGate.Wait(0));
        sharedCommandGate.Release();

        await port.DisposeAsync();
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

    private sealed class BlockingFanSession : IRazerCommandSession
    {
        private int calls;

        internal TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseSecond { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            _ = assignTransactionId();
            int call = Interlocked.Increment(ref calls);
            Task release;
            if (call == 1)
            {
                FirstStarted.TrySetResult();
                release = ReleaseFirst.Task;
            }
            else if (call == 2)
            {
                SecondStarted.TrySetResult();
                release = ReleaseSecond.Task;
            }
            else
            {
                throw new InvalidOperationException("Only two fan selectors are expected.");
            }

            await release.WaitAsync(cancellationToken);
            byte selector = command.Arguments.Span[1];
            ReadOnlyMemory<byte> payload = selector switch
            {
                0x01 => new byte[] { 0x01, 0x01, 0x20 },
                0x02 => new byte[] { 0x01, 0x02, 0x1E },
                _ => throw new InvalidOperationException(),
            };
            return new(payload, null);
        }

        public ValueTask DisposeAsync()
        {
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

        public Task<HidSessionResult<TemporaryFullChargeReading>> QueryReadingAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            bool enabled,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
