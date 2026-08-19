using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadFanCadenceValidationTests
{
    [Fact]
    public async Task Runner_SubmitsSevenOneHertzUpdatesAndReleasesOnce()
    {
        var session = new ScriptedSession();
        var delays = new List<TimeSpan>();
        var stages = new List<CoolingPadFanCadenceValidationStage>();

        CoolingPadFanCadenceValidationResult result = await CreateRunner(
                session,
                (delay, _) =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                })
            .RunAsync(
                (stage, _) =>
                {
                    stages.Add(stage);
                    return Task.FromResult(true);
                },
                default);

        Assert.True(result.Success);
        Assert.Equal(7, result.RequestedUpdateCount);
        Assert.Equal(7, result.AcknowledgedUpdateCount);
        Assert.All(delays, delay => Assert.Equal(TimeSpan.FromSeconds(1), delay));
        Assert.Equal(
            [
                CoolingPadFanCadenceValidationStage.BaselineAuto,
                CoolingPadFanCadenceValidationStage.CadenceObserved,
                CoolingPadFanCadenceValidationStage.Release,
            ],
            stages);
        Assert.Equal(
            [
                ((byte)0x10, "000600", (byte)1),
                ((byte)0x01, "01050A", (byte)2),
                ((byte)0x01, "010514", (byte)3),
                ((byte)0x01, "01051E", (byte)4),
                ((byte)0x01, "01052C", (byte)5),
                ((byte)0x01, "010540", (byte)6),
                ((byte)0x01, "01052C", (byte)7),
                ((byte)0x01, "01050A", (byte)8),
                ((byte)0x01, "01052C", (byte)9),
                ((byte)0x10, "000600", (byte)10),
            ],
            session.Operations.Select(operation => (
                operation.Command.CommandId,
                Convert.ToHexString(operation.Command.Arguments.Span),
                operation.TransactionId)).ToArray());
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Runner_PhysicalCadenceRejectionStillReleasesOnce()
    {
        var session = new ScriptedSession();

        CoolingPadFanCadenceValidationResult result = await CreateRunner(session)
            .RunAsync(
                (stage, _) => Task.FromResult(
                    stage != CoolingPadFanCadenceValidationStage.CadenceObserved),
                default);

        Assert.Equal(
            CoolingPadFanCadenceValidationFailure.CadenceNotConfirmed,
            result.Failure);
        Assert.True(result.ReleaseAcknowledged);
        Assert.True(result.ReleasePhysicallyConfirmed);
        Assert.False(result.ManualSynapseRecoveryRequired);
        Assert.Equal(10, session.Operations.Count);
    }

    [Fact]
    public async Task Runner_IndeterminateUpdateDoesNotSubmitReleaseOrRetry()
    {
        var session = new ScriptedSession
        {
            FailureAtCall = 5,
            Failure = HidSessionFailureKind.NativeIoFailure,
        };

        CoolingPadFanCadenceValidationResult result = await CreateRunner(session)
            .RunAsync((_, _) => Task.FromResult(true), default);

        Assert.Equal(
            CoolingPadFanCadenceValidationFailure.UpdateIndeterminate,
            result.Failure);
        Assert.False(result.ReleaseAttempted);
        Assert.True(result.ManualSynapseRecoveryRequired);
        Assert.True(result.ManualPowerCycleMayBeRequired);
        Assert.Equal(5, session.Operations.Count);
    }

    [Fact]
    public async Task Runner_DefinitiveUpdateFailureSubmitsOneRelease()
    {
        var session = new ScriptedSession
        {
            FailureAtCall = 5,
            Failure = HidSessionFailureKind.ConflictBlocked,
        };

        CoolingPadFanCadenceValidationResult result = await CreateRunner(session)
            .RunAsync((_, _) => Task.FromResult(true), default);

        Assert.Equal(
            CoolingPadFanCadenceValidationFailure.UpdateKnownNotApplied,
            result.Failure);
        Assert.True(result.ReleaseAttempted);
        Assert.True(result.ReleaseAcknowledged);
        Assert.True(result.ReleasePhysicallyConfirmed);
        Assert.False(result.ManualSynapseRecoveryRequired);
        Assert.Equal(6, session.Operations.Count);
        Assert.Equal(0x10, session.Operations[^1].Command.CommandId);
    }

    [Fact]
    public async Task Command_RequiresExactTargetAndKeepsAdmissionClosed()
    {
        int calls = 0;
        var output = new StringWriter();
        var command = new CoolingPadFanCadenceValidationCommand(
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new CoolingPadFanCadenceValidationResult(
                    BaselineAutoPhysicallyConfirmed: true,
                    AcquireAcknowledged: true,
                    RequestedUpdateCount: 7,
                    AcknowledgedUpdateCount: 7,
                    OneHertzCadencePhysicallyConfirmed: true,
                    ReleaseAttempted: true,
                    ReleaseAcknowledged: true,
                    ReleasePhysicallyConfirmed: true,
                    ManualSynapseRecoveryRequired: false,
                    ManualPowerCycleMayBeRequired: false,
                    CoolingPadFanCadenceValidationFailure.None));
            },
            new StringReader(string.Empty),
            output);

        Assert.Equal(2, await command.RunAsync([], default));
        Assert.Equal(0, calls);
        int exitCode = await command.RunAsync(
            ["--confirm-target", CoolingPadFanCadenceValidationCommand.Confirmation],
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, calls);
        string json = output.ToString()[output.ToString().IndexOf('{')..];
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(1_000, document.RootElement.GetProperty(
            "intervalMilliseconds").GetInt32());
        Assert.Equal(7, document.RootElement.GetProperty(
            "targetRpmSequence").GetArrayLength());
        Assert.False(document.RootElement.GetProperty(
            "productionAdmitted").GetBoolean());
        Assert.False(document.RootElement.GetProperty(
            "activeModeReadbackConfirmed").GetBoolean());
        Assert.False(document.RootElement.GetProperty(
            "physicalRpmReadbackConfirmed").GetBoolean());
        Assert.False(document.RootElement.GetProperty("retryAllowed").GetBoolean());
    }

    private static CoolingPadFanCadenceValidationRunner CreateRunner(
        ScriptedSession session,
        Func<TimeSpan, CancellationToken, Task>? delay = null) => new(
        static () => new ValidationRunContext(),
        new ActorFactory(new ValidationActor(
            session,
            new UnsupportedBatteryCommands(),
            new UnsupportedTemporaryCommands())),
        delay ?? (static (_, _) => Task.CompletedTask),
        TimeProvider.System,
        new PendingDisposalRegistry());

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context) => ValueTask.FromResult(
            new HidSessionResult<ValidationActor>(actor, null));
    }

    private sealed record Operation(RazerCommandSpec Command, byte TransactionId);

    private sealed class ScriptedSession : IRazerCommandSession
    {
        internal int? FailureAtCall { get; init; }
        internal HidSessionFailureKind Failure { get; init; }
        internal List<Operation> Operations { get; } = [];
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
            byte transaction = assignTransactionId().TransactionId;
            Operations.Add(new(command, transaction));
            if (FailureAtCall == Operations.Count)
            {
                return Task.FromResult(
                    new HidSessionResult<ReadOnlyMemory<byte>>(default, Failure));
            }

            byte[] response = command.Arguments.ToArray();
            Assert.True(command.PayloadValidator?.Invoke(response) ?? true);
            return Task.FromResult(
                new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
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
