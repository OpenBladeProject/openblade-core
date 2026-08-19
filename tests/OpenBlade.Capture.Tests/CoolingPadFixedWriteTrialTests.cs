using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadFixedWriteTrialTests
{
    [Fact]
    public async Task Runner_SubmitsOneFixedWriteAndNeverSubmitsAutoCleanup()
    {
        var session = new SequentialSession(
            new byte[] { 0x01, 0x05, 0x00 },
            new byte[] { 0x01, 0x02, 0x2C },
            new byte[] { 0x01, 0x05, 0x2C });
        var runner = CreateRunner(session);

        CoolingPadFixedWriteTrialResult result =
            await runner.RunAsync(CancellationToken.None);

        Assert.True(result.BaselineRead);
        Assert.Equal((byte)0, result.BaselineOpaqueValue);
        Assert.True(result.WriteAttempted);
        Assert.True(result.WriteAcknowledged);
        Assert.True(result.PostWriteRead);
        Assert.Equal((byte)0x2C, result.PostWriteOpaqueValue);
        Assert.False(result.ActiveStateReadbackConfirmed);
        Assert.True(result.SynapseRestoreRequired);
        Assert.Equal("none", result.Failure);
        Assert.Equal(3, session.Commands.Count);
        RazerCommandSpec write = session.Commands[1];
        Assert.Equal(0x0D, write.ClassId);
        Assert.Equal(0x10, write.CommandId);
        Assert.Equal([0x01, 0x02, 0x2C], write.Arguments.ToArray());
        Assert.DoesNotContain(
            session.Commands,
            command => command.CommandId == 0x10
                && command.Arguments.Span.SequenceEqual(
                    new byte[] { 0x00, 0x06, 0x00 }));
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Runner_WriteFailureDoesNotRetryOrSendCleanup()
    {
        var session = new SequentialSession(
            new byte[] { 0x01, 0x05, 0x00 },
            HidSessionFailureKind.NativeIoFailure);

        CoolingPadFixedWriteTrialResult result =
            await CreateRunner(session).RunAsync(CancellationToken.None);

        Assert.True(result.WriteAttempted);
        Assert.False(result.WriteAcknowledged);
        Assert.False(result.ActiveStateReadbackConfirmed);
        Assert.True(result.SynapseRestoreRequired);
        Assert.Equal(2, session.Commands.Count);
        Assert.Equal(0x10, session.Commands[1].CommandId);
        Assert.Equal(1, session.DisposeCount);
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x20)]
    [InlineData(0x2C)]
    [InlineData(0xFF)]
    public async Task Runner_NonzeroBaselineStopsBeforeWrite(
        byte baselineRaw)
    {
        var session = new SequentialSession(
            new byte[] { 0x01, 0x05, baselineRaw });

        CoolingPadFixedWriteTrialResult result =
            await CreateRunner(session).RunAsync(CancellationToken.None);

        Assert.True(result.BaselineRead);
        Assert.Equal(baselineRaw, result.BaselineOpaqueValue);
        Assert.False(result.WriteAttempted);
        Assert.False(result.WriteAcknowledged);
        Assert.False(result.SynapseRestoreRequired);
        Assert.Equal("baseline-not-opaque-zero", result.Failure);
        Assert.Single(session.Commands);
        Assert.Equal(0x81, session.Commands[0].CommandId);
        Assert.Equal(1, session.DisposeCount);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x1F)]
    [InlineData(0x2C)]
    [InlineData(0xFF)]
    public async Task Runner_PreservesPostWriteOpaqueValueWithoutClaimingState(
        byte postRaw)
    {
        var session = new SequentialSession(
            new byte[] { 0x01, 0x05, 0x00 },
            new byte[] { 0x01, 0x02, 0x2C },
            new byte[] { 0x01, 0x05, postRaw });

        CoolingPadFixedWriteTrialResult result =
            await CreateRunner(session).RunAsync(CancellationToken.None);

        Assert.True(result.WriteAcknowledged);
        Assert.True(result.PostWriteRead);
        Assert.Equal(postRaw, result.PostWriteOpaqueValue);
        Assert.False(result.ActiveStateReadbackConfirmed);
        Assert.Equal("none", result.Failure);
        Assert.True(result.SynapseRestoreRequired);
        Assert.Equal(3, session.Commands.Count);
    }

    [Fact]
    public async Task Command_RequiresExactConfirmation()
    {
        int calls = 0;
        var output = new StringWriter();
        var command = new CoolingPadFixedWriteTrialCommand(
            _ =>
            {
                calls++;
                throw new InvalidOperationException();
            },
            output);

        int exitCode = await command.RunAsync([], CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(0, calls);
        Assert.Contains(CoolingPadFixedWriteTrialCommand.Confirmation, output.ToString());
    }

    [Fact]
    public async Task Command_ReturnsSuccessForAcknowledgedWriteAndOpaquePostRead()
    {
        var output = new StringWriter();
        var result = new CoolingPadFixedWriteTrialResult(
            BaselineRead: true,
            BaselineOpaqueValue: 0,
            WriteAttempted: true,
            WriteAcknowledged: true,
            PostWriteRead: true,
            PostWriteOpaqueValue: 0,
            ActiveStateReadbackConfirmed: false,
            SynapseRestoreRequired: true,
            Failure: "none");
        var command = new CoolingPadFixedWriteTrialCommand(
            _ => Task.FromResult(result),
            output);

        int exitCode = await command.RunAsync(
            ["--confirm-target", CoolingPadFixedWriteTrialCommand.Confirmation],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("restore the cooling pad to Auto", output.ToString());
    }

    [Fact]
    public void FixedCommand_HasExactCapturedPayloadAndEchoValidation()
    {
        RazerCommandSpec command =
            CoolingPadFixedWriteTrialRunner.CreateFixed2200Command();

        Assert.Equal(0x0D, command.ClassId);
        Assert.Equal(0x10, command.CommandId);
        Assert.Equal([0x01, 0x02, 0x2C], command.Arguments.ToArray());
        Assert.True(command.PayloadValidator!(new byte[] { 0x01, 0x02, 0x2C }));
        Assert.False(command.PayloadValidator!(new byte[] { 0x00, 0x06, 0x00 }));
    }

    private static CoolingPadFixedWriteTrialRunner CreateRunner(
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
