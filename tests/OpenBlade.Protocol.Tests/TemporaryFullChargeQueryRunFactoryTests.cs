using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class TemporaryFullChargeQueryRunFactoryTests
{
    [Fact]
    public void PublicPort_ExposesExactlyOneTypedQueryAndDisposal()
    {
        string[] methods = typeof(ITemporaryFullChargeQueryPort).GetMethods()
            .Select(method => method.Name).Order().ToArray();

        Assert.Equal(["QueryAsync"], methods);
        Assert.Contains(typeof(IAsyncDisposable), typeof(ITemporaryFullChargeQueryPort).GetInterfaces());
        Assert.Empty(typeof(ITemporaryFullChargeQueryPort).GetProperties());
        Assert.DoesNotContain(typeof(ITemporaryFullChargeQueryPort).GetMembers(), member =>
            member.Name.Contains("Set", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Apply", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Catalog", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OpenAsync_ReservesIdBeforeOpeningAndQueryRunsExactlyOnce()
    {
        var context = new ValidationRunContext();
        var commands = new TemporaryCommands(new(false, null));
        var actorFactory = new ActorFactory(Actor(commands));
        var factory = new TemporaryFullChargeQueryRunFactory(
            () => context, actorFactory, TimeProvider.System, TimeSpan.FromSeconds(5),
            new PendingDisposalRegistry());

        ITemporaryFullChargeQueryPort port = (await factory.OpenAsync(default)).Value!;
        HidSessionResult<bool> first = await port.QueryAsync(default);
        HidSessionResult<bool> second = await port.QueryAsync(default);

        Assert.Null(first.Failure);
        Assert.False(first.Value);
        Assert.Equal(HidSessionFailureKind.Disposed, second.Failure);
        Assert.Equal([1], commands.TransactionIds);
        Assert.Equal(1, actorFactory.OpenCount);
    }

    private static ValidationActor Actor(ITemporaryFullChargeCommandClient temporary) =>
        new(new Session(), new BatteryCommands(), temporary);

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        internal int OpenCount { get; private set; }
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            OpenCount++;
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
        }
    }

    private sealed class TemporaryCommands(HidSessionResult<bool> result)
        : ITemporaryFullChargeCommandClient
    {
        internal List<byte> TransactionIds { get; } = [];
        public Task<HidSessionResult<bool>> QueryAsync(byte transactionId, CancellationToken token)
        {
            TransactionIds.Add(transactionId);
            return Task.FromResult(result);
        }
    }

    private sealed class BatteryCommands : IBatteryCommandClient
    {
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(byte id, CancellationToken token) => throw new InvalidOperationException();
        public Task<HidSessionResult<HidUnit>> SetAsync(byte id, BatteryValidationSetting setting, CancellationToken token) => throw new InvalidOperationException();
    }

    private sealed class Session : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition { get; private set; } = RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command, Func<TransactionIdAssignment> assign, TimeSpan timeout, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposition = RazerCommandSessionDisposition.FullyDisposed; return ValueTask.CompletedTask; }
    }
}
