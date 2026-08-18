using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadHostControlWriteTrialTests
{
    [Fact]
    public async Task Runner_SubmitsPreparationThenOneHostDemand()
    {
        var session = new SequentialSession(
            new byte[] { 0x00, 0x06, 0x00 },
            new byte[] { 0x01, 0x05, 0x2C });

        CoolingPadHostControlWriteTrialResult result =
            await CreateRunner(session).RunAsync(CancellationToken.None);

        Assert.True(result.HostControlPreparationAcknowledged);
        Assert.True(result.HostDemandAcknowledged);
        Assert.Equal(2200, result.HostDemandTargetRpm);
        Assert.True(result.OperatorPhysicalConfirmationRequired);
        Assert.True(result.SynapseRestoreRequired);
        Assert.Equal("none", result.Failure);
        Assert.Collection(
            session.Commands,
            command =>
            {
                Assert.Equal(0x10, command.CommandId);
                Assert.Equal([0x00, 0x06, 0x00], command.Arguments.ToArray());
            },
            command =>
            {
                Assert.Equal(0x01, command.CommandId);
                Assert.Equal([0x01, 0x05, 0x2C], command.Arguments.ToArray());
            });
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Runner_PreparationFailureStopsBeforeHostDemand()
    {
        var session = new SequentialSession(HidSessionFailureKind.NativeIoFailure);

        CoolingPadHostControlWriteTrialResult result =
            await CreateRunner(session).RunAsync(CancellationToken.None);

        Assert.True(result.HostControlPreparationAttempted);
        Assert.False(result.HostControlPreparationAcknowledged);
        Assert.False(result.HostDemandWriteAttempted);
        Assert.False(result.HostDemandAcknowledged);
        Assert.True(result.SynapseRestoreRequired);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task Runner_HostDemandFailureDoesNotRetryOrCleanup()
    {
        var session = new SequentialSession(
            new byte[] { 0x00, 0x06, 0x00 },
            HidSessionFailureKind.NativeIoFailure);

        CoolingPadHostControlWriteTrialResult result =
            await CreateRunner(session).RunAsync(CancellationToken.None);

        Assert.True(result.HostControlPreparationAcknowledged);
        Assert.True(result.HostDemandWriteAttempted);
        Assert.False(result.HostDemandAcknowledged);
        Assert.True(result.SynapseRestoreRequired);
        Assert.Equal(2, session.Commands.Count);
        Assert.DoesNotContain(
            session.Commands.Skip(1),
            command => command.CommandId == 0x10);
    }

    [Fact]
    public async Task Runner_ReservesBothTransactionsBeforeFirstWrite()
    {
        var context = new ValidationRunContext();
        for (int index = 0; index < byte.MaxValue - 1; index++)
        {
            Assert.True(context.TryAllocateTransaction(out _));
        }
        var session = new SequentialSession();
        var runner = new CoolingPadHostControlWriteTrialRunner(
            () => context,
            new ActorFactory(new ValidationActor(
                session,
                new UnsupportedBatteryCommands(),
                new UnsupportedTemporaryCommands())),
            TimeProvider.System,
            new PendingDisposalRegistry());

        CoolingPadHostControlWriteTrialResult result =
            await runner.RunAsync(CancellationToken.None);

        Assert.Equal("transaction-budget", result.Failure);
        Assert.False(result.HostControlPreparationAttempted);
        Assert.False(result.HostDemandWriteAttempted);
        Assert.Empty(session.Commands);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Command_RequiresExactConfirmation()
    {
        int calls = 0;
        var output = new StringWriter();
        var command = new CoolingPadHostControlWriteTrialCommand(
            _ =>
            {
                calls++;
                throw new InvalidOperationException();
            },
            output);

        int exitCode = await command.RunAsync([], CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(0, calls);
        Assert.Contains(
            CoolingPadHostControlWriteTrialCommand.Confirmation,
            output.ToString());
    }

    private static CoolingPadHostControlWriteTrialRunner CreateRunner(
        IRazerCommandSession session) => new(
        static () => new ValidationRunContext(),
        new ActorFactory(new ValidationActor(
            session,
            new UnsupportedBatteryCommands(),
            new UnsupportedTemporaryCommands())),
        TimeProvider.System,
        new PendingDisposalRegistry());

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
    }

    private sealed class SequentialSession(params object[] responses)
        : IRazerCommandSession
    {
        private int responseIndex;

        internal List<RazerCommandSpec> Commands { get; } = [];

        internal int DisposeCount { get; private set; }

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(responseIndex + 1, assignTransactionId().TransactionId);
            Commands.Add(command);
            object response = responses[responseIndex++];
            if (response is HidSessionFailureKind failure)
            {
                return Task.FromResult(
                    new HidSessionResult<ReadOnlyMemory<byte>>(default, failure));
            }

            byte[] payload = Assert.IsType<byte[]>(response);
            Assert.Equal(command.ExpectedPayloadLength, payload.Length);
            Assert.True(command.PayloadValidator?.Invoke(payload) ?? true);
            return Task.FromResult(
                new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnsupportedBatteryCommands : IBatteryCommandClient
    {
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            BatteryValidationSetting setting,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnsupportedTemporaryCommands
        : ITemporaryFullChargeCommandClient
    {
        public Task<HidSessionResult<bool>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            bool enabled,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
