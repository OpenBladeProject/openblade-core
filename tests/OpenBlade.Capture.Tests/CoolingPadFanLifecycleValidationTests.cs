using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadFanLifecycleValidationTests
{
    [Fact]
    public async Task Runner_ExecutesExactRangeUpdateAndReleaseSequenceOnce()
    {
        var session = new ScriptedSession();
        var stages = new List<CoolingPadFanValidationStage>();

        CoolingPadFanLifecycleValidationResult result = await CreateRunner(session)
            .RunAsync(
                (stage, _) =>
                {
                    stages.Add(stage);
                    return Task.FromResult(true);
                },
                default);

        Assert.True(result.Success);
        Assert.False(result.ManualSynapseRecoveryRequired);
        Assert.False(result.ManualPowerCycleMayBeRequired);
        Assert.Equal(
            [
                CoolingPadFanValidationStage.Minimum500,
                CoolingPadFanValidationStage.Maximum3200,
                CoolingPadFanValidationStage.Update2200,
                CoolingPadFanValidationStage.Release,
            ],
            stages);
        Assert.Equal(
            [
                ((byte)0x10, "000600", (byte)1),
                ((byte)0x01, "01050A", (byte)2),
                ((byte)0x01, "010540", (byte)3),
                ((byte)0x01, "01052C", (byte)4),
                ((byte)0x10, "000600", (byte)5),
            ],
            session.Operations.Select(operation => (
                operation.Command.CommandId,
                Convert.ToHexString(operation.Command.Arguments.Span),
                operation.TransactionId)).ToArray());
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Runner_PhysicalRejectionStopsBeforeLaterWrites()
    {
        var session = new ScriptedSession();

        CoolingPadFanLifecycleValidationResult result = await CreateRunner(session)
            .RunAsync(
                (stage, _) => Task.FromResult(
                    stage != CoolingPadFanValidationStage.Maximum3200),
                default);

        Assert.Equal(
            CoolingPadFanLifecycleValidationFailure.MaximumPhysicalNotConfirmed,
            result.Failure);
        Assert.True(result.MinimumPhysicalConfirmed);
        Assert.True(result.MaximumUpdateAcknowledged);
        Assert.False(result.Update2200Acknowledged);
        Assert.True(result.ManualSynapseRecoveryRequired);
        Assert.Equal(3, session.Operations.Count);
    }

    [Fact]
    public async Task Runner_IndeterminateUpdatePoisonsAndNeverRetries()
    {
        var session = new ScriptedSession
        {
            FailureAtCall = 3,
            Failure = HidSessionFailureKind.NativeIoFailure,
        };

        CoolingPadFanLifecycleValidationResult result = await CreateRunner(session)
            .RunAsync((_, _) => Task.FromResult(true), default);

        Assert.Equal(
            CoolingPadFanLifecycleValidationFailure.MaximumUpdateIndeterminate,
            result.Failure);
        Assert.True(result.ManualSynapseRecoveryRequired);
        Assert.True(result.ManualPowerCycleMayBeRequired);
        Assert.Equal(3, session.Operations.Count);
    }

    [Fact]
    public async Task Runner_ReleasePhysicalRejectionRequiresManualSynapseRecovery()
    {
        var session = new ScriptedSession();

        CoolingPadFanLifecycleValidationResult result = await CreateRunner(session)
            .RunAsync(
                (stage, _) => Task.FromResult(
                    stage != CoolingPadFanValidationStage.Release),
                default);

        Assert.Equal(
            CoolingPadFanLifecycleValidationFailure.ReleasePhysicalNotConfirmed,
            result.Failure);
        Assert.True(result.ReleaseAcknowledged);
        Assert.False(result.ReleasePhysicalConfirmed);
        Assert.True(result.ManualSynapseRecoveryRequired);
        Assert.Equal(5, session.Operations.Count);
    }

    [Fact]
    public async Task Command_RequiresExactTargetAndKeepsAdmissionClosed()
    {
        int calls = 0;
        var output = new StringWriter();
        var command = new CoolingPadFanLifecycleValidationCommand(
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new CoolingPadFanLifecycleValidationResult(
                    MinimumApplyAcknowledged: true,
                    MinimumPhysicalConfirmed: true,
                    MaximumUpdateAcknowledged: true,
                    MaximumPhysicalConfirmed: true,
                    Update2200Acknowledged: true,
                    Update2200PhysicalConfirmed: true,
                    ReleaseAcknowledged: true,
                    ReleasePhysicalConfirmed: true,
                    ManualSynapseRecoveryRequired: false,
                    ManualPowerCycleMayBeRequired: false,
                    CoolingPadFanLifecycleValidationFailure.None));
            },
            new StringReader(string.Empty),
            output);

        Assert.Equal(2, await command.RunAsync([], default));
        Assert.Equal(0, calls);
        int exitCode = await command.RunAsync(
            ["--confirm-target", CoolingPadFanLifecycleValidationCommand.Confirmation],
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, calls);
        string json = output.ToString()[output.ToString().IndexOf('{')..];
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("productionAdmitted").GetBoolean());
        Assert.False(document.RootElement.GetProperty(
            "activeModeReadbackConfirmed").GetBoolean());
        Assert.False(document.RootElement.GetProperty(
            "physicalRpmReadbackConfirmed").GetBoolean());
        Assert.False(document.RootElement.GetProperty("retryAllowed").GetBoolean());
    }

    private static CoolingPadFanLifecycleValidationRunner CreateRunner(
        ScriptedSession session) => new(
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
