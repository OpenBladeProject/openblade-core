using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class DeviceModeValidationRunnerTests
{
    [Fact]
    public async Task RunAsync_AppliesConfirmsAndRestoresNormalMode()
    {
        var session = new DeviceModeSession();
        var runner = CreateRunner(session);

        DeviceModeValidationResult result = await runner.RunAsync(
            _ => Task.FromResult(true),
            default);

        Assert.True(result.Success);
        Assert.Equal("Normal", result.BaselineMode);
        Assert.Equal("Driver", result.CandidateMode);
        Assert.Equal("Normal", result.RestoredMode);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(
            [
                (0x00, 0x84, "0000"),
                (0x00, 0x04, "0300"),
                (0x00, 0x84, "0000"),
                (0x00, 0x04, "0000"),
                (0x00, 0x84, "0000"),
            ],
            session.Commands.Select(command => (
                (int)command.ClassId,
                (int)command.CommandId,
                Convert.ToHexString(command.Arguments.Span))).ToArray());
        Assert.Equal(RazerDeviceMode.Normal, session.Mode);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_AppliesNormalAndRestoresDriverMode()
    {
        var session = new DeviceModeSession(RazerDeviceMode.Driver);
        var runner = CreateRunner(session);

        DeviceModeValidationResult result = await runner.RunAsync(
            _ => Task.FromResult(true),
            default);

        Assert.True(result.Success);
        Assert.Equal("Driver", result.BaselineMode);
        Assert.Equal("Normal", result.CandidateMode);
        Assert.Equal("Driver", result.RestoredMode);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(
            [
                (0x00, 0x84, "0000"),
                (0x00, 0x04, "0000"),
                (0x00, 0x84, "0000"),
                (0x00, 0x04, "0300"),
                (0x00, 0x84, "0000"),
            ],
            session.Commands.Select(command => (
                (int)command.ClassId,
                (int)command.CommandId,
                Convert.ToHexString(command.Arguments.Span))).ToArray());
        Assert.Equal(RazerDeviceMode.Driver, session.Mode);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_RestoresWhenVisualCheckIsRejected()
    {
        var session = new DeviceModeSession();

        DeviceModeValidationResult result = await CreateRunner(session).RunAsync(
            _ => Task.FromResult(false),
            default);

        Assert.Equal(DeviceModeValidationFailure.VisualCheckRejected, result.Failure);
        Assert.True(result.Restored);
        Assert.Equal(RazerDeviceMode.Normal, session.Mode);
    }

    [Fact]
    public async Task RunAsync_RestoresWhenVisualCheckIsCanceled()
    {
        var session = new DeviceModeSession();
        using var cancellation = new CancellationTokenSource();

        DeviceModeValidationResult result = await CreateRunner(session).RunAsync(
            token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<bool>(token);
            },
            cancellation.Token);

        Assert.Equal(DeviceModeValidationFailure.Canceled, result.Failure);
        Assert.True(result.Restored);
        Assert.Equal(RazerDeviceMode.Normal, session.Mode);
    }

    [Fact]
    public async Task RunAsync_ReportsManualRecoveryWhenRestoreFails()
    {
        var session = new DeviceModeSession { FailModeSet = RazerDeviceMode.Normal };

        DeviceModeValidationResult result = await CreateRunner(session).RunAsync(
            _ => Task.FromResult(true),
            default);

        Assert.Equal(DeviceModeValidationFailure.RestoreFailed, result.Failure);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.Restored);
        Assert.Equal(RazerDeviceMode.Driver, session.Mode);
    }

    private static DeviceModeValidationRunner CreateRunner(IRazerCommandSession session) =>
        new(
            static () => new ValidationRunContext(),
            new ActorFactory(new ValidationActor(
                session,
                new UnsupportedBatteryCommands(),
                new UnsupportedTemporaryCommands())),
            TimeProvider.System,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            new PendingDisposalRegistry());

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
    }

    private sealed class DeviceModeSession(
        RazerDeviceMode initialMode = RazerDeviceMode.Normal) : IRazerCommandSession
    {
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal RazerDeviceMode Mode { get; private set; } = initialMode;
        internal RazerDeviceMode? FailModeSet { get; set; }
        internal int DisposeCount { get; private set; }

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            _ = assignTransactionId();
            Commands.Add(command);
            if ((command.ClassId, command.CommandId) == (0x00, 0x84))
            {
                byte[] response = [(byte)Mode, 0x00];
                Assert.True(command.PayloadValidator?.Invoke(response) ?? true);
                return Success(response);
            }

            Assert.Equal((0x00, 0x04), (command.ClassId, command.CommandId));
            var requested = (RazerDeviceMode)command.Arguments.Span[0];
            if (requested == FailModeSet)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(
                    default,
                    HidSessionFailureKind.NativeIoFailure));
            }

            Mode = requested;
            byte[] setResponse = command.Arguments.ToArray();
            Assert.True(command.PayloadValidator?.Invoke(setResponse) ?? true);
            return Success(setResponse);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }

        private static Task<HidSessionResult<ReadOnlyMemory<byte>>> Success(byte[] payload) =>
            Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));
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

    private sealed class UnsupportedTemporaryCommands : ITemporaryFullChargeCommandClient
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
