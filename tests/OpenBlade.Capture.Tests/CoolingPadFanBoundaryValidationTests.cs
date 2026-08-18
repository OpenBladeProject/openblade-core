using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadFanBoundaryValidationTests
{
    [Theory]
    [InlineData((int)CoolingPadFanBoundaryKind.DisconnectReconnect)]
    [InlineData((int)CoolingPadFanBoundaryKind.LowPowerResume)]
    public async Task Runner_ClosesReopensAndReleasesOnce(int boundaryValue)
    {
        var boundary = (CoolingPadFanBoundaryKind)boundaryValue;
        var initialSession = new ScriptedSession();
        var recoverySession = new ScriptedSession();
        var factory = new ActorFactory(
            Success(initialSession),
            Success(recoverySession));
        var presence = new PhysicalPresenceSource(
            CoolingPadPhysicalPresenceObservation.Absent);
        var stages = new List<CoolingPadFanBoundaryStage>();
        var runner = CreateRunner(factory, presence);

        CoolingPadFanBoundaryValidationResult result = await runner.RunAsync(
            boundary,
            (stage, _) =>
            {
                stages.Add(stage);
                if (stage is CoolingPadFanBoundaryStage.Disconnected
                    or CoolingPadFanBoundaryStage.BoundaryCompleted)
                {
                    Assert.Equal(1, initialSession.DisposeCount);
                }
                if (stage == CoolingPadFanBoundaryStage.Retained2200)
                {
                    Assert.Empty(recoverySession.Operations);
                }
                return Task.FromResult(true);
            },
            default);

        Assert.True(result.Success);
        Assert.Equal(boundary, result.Boundary);
        Assert.True(result.InitialApplyAttempted);
        Assert.True(result.InitialApplyAcknowledged);
        Assert.True(result.InitialPhysicalConfirmed);
        Assert.True(result.InitialHandleClosedBeforeBoundary);
        Assert.Equal(
            boundary == CoolingPadFanBoundaryKind.DisconnectReconnect
                ? true
                : null,
            result.PhysicalAbsenceObserved);
        Assert.True(result.BoundaryCompleted);
        Assert.True(result.ReopenedAfterBoundary);
        Assert.True(result.Retained2200PhysicallyConfirmedBeforeWrite);
        Assert.True(result.ReleaseAttempted);
        Assert.True(result.ReleaseAcknowledged);
        Assert.True(result.ReleasePhysicalConfirmed);
        Assert.Equal(2, result.WritePlanAttemptCount);
        Assert.Equal(2, result.AcknowledgedWritePlanCount);
        Assert.False(result.RetryAttempted);
        Assert.False(result.ActiveModeReadbackConfirmed);
        Assert.False(result.PhysicalRpmReadbackConfirmed);
        Assert.False(result.ManualSynapseRecoveryRequired);
        Assert.False(result.ManualPowerCycleMayBeRequired);
        Assert.False(result.ProductionWriteAdmitted);
        var expectedStages = new List<CoolingPadFanBoundaryStage>
        {
            CoolingPadFanBoundaryStage.Initial2200,
            CoolingPadFanBoundaryStage.BoundaryCompleted,
            CoolingPadFanBoundaryStage.Retained2200,
            CoolingPadFanBoundaryStage.Release,
        };
        if (boundary == CoolingPadFanBoundaryKind.DisconnectReconnect)
        {
            expectedStages.Insert(1, CoolingPadFanBoundaryStage.Disconnected);
        }
        Assert.Equal(expectedStages, stages);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, initialSession.DisposeCount);
        Assert.Equal(1, recoverySession.DisposeCount);
        Assert.Equal(
            [
                ((byte)0x10, "000600", (byte)1),
                ((byte)0x01, "01052C", (byte)2),
            ],
            Operations(initialSession));
        Assert.Equal(
            [((byte)0x10, "000600", (byte)1)],
            Operations(recoverySession));
    }

    [Fact]
    public async Task Runner_BoundaryRejectionStopsAfterConfirmedHandleClose()
    {
        var initialSession = new ScriptedSession();
        var recoverySession = new ScriptedSession();
        var factory = new ActorFactory(
            Success(initialSession),
            Success(recoverySession));

        CoolingPadFanBoundaryValidationResult result = await CreateRunner(
                factory,
                new PhysicalPresenceSource(CoolingPadPhysicalPresenceObservation.Absent))
            .RunAsync(
                CoolingPadFanBoundaryKind.DisconnectReconnect,
                (stage, _) => Task.FromResult(
                    stage != CoolingPadFanBoundaryStage.BoundaryCompleted),
                default);

        Assert.Equal(
            CoolingPadFanBoundaryValidationFailure.BoundaryNotConfirmed,
            result.Failure);
        Assert.True(result.InitialHandleClosedBeforeBoundary);
        Assert.False(result.ReopenedAfterBoundary);
        Assert.True(result.ManualSynapseRecoveryRequired);
        Assert.Equal(1, result.WritePlanAttemptCount);
        Assert.Equal(1, result.AcknowledgedWritePlanCount);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, initialSession.DisposeCount);
        Assert.Equal(0, recoverySession.DisposeCount);
    }

    [Fact]
    public async Task Runner_DisconnectWithoutObservedAbsenceDoesNotReopenOrWrite()
    {
        var initialSession = new ScriptedSession();
        var recoverySession = new ScriptedSession();
        var factory = new ActorFactory(
            Success(initialSession),
            Success(recoverySession));

        CoolingPadFanBoundaryValidationResult result = await CreateRunner(
                factory,
                new PhysicalPresenceSource(CoolingPadPhysicalPresenceObservation.Present))
            .RunAsync(
                CoolingPadFanBoundaryKind.DisconnectReconnect,
                static (_, _) => Task.FromResult(true),
                default);

        Assert.Equal(
            CoolingPadFanBoundaryValidationFailure.PhysicalAbsenceNotConfirmed,
            result.Failure);
        Assert.False(result.PhysicalAbsenceObserved);
        Assert.False(result.BoundaryCompleted);
        Assert.False(result.ReopenedAfterBoundary);
        Assert.Equal(1, result.WritePlanAttemptCount);
        Assert.Equal(1, factory.OpenCount);
        Assert.Empty(recoverySession.Operations);
    }

    [Fact]
    public async Task Runner_ReopenFailureDoesNotIssueRelease()
    {
        var initialSession = new ScriptedSession();
        var factory = new ActorFactory(
            Success(initialSession),
            new HidSessionResult<ValidationActor>(
                null,
                HidSessionFailureKind.NativeIoFailure));

        CoolingPadFanBoundaryValidationResult result = await CreateRunner(
                factory,
                new PhysicalPresenceSource(CoolingPadPhysicalPresenceObservation.Absent))
            .RunAsync(
                CoolingPadFanBoundaryKind.LowPowerResume,
                static (_, _) => Task.FromResult(true),
                default);

        Assert.Equal(
            CoolingPadFanBoundaryValidationFailure.ReopenFailed,
            result.Failure);
        Assert.True(result.BoundaryCompleted);
        Assert.False(result.ReopenedAfterBoundary);
        Assert.False(result.ReleaseAttempted);
        Assert.True(result.ManualSynapseRecoveryRequired);
        Assert.Equal(1, result.WritePlanAttemptCount);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, initialSession.DisposeCount);
    }

    [Fact]
    public async Task Runner_IndeterminateReleasePoisonsAndNeverRetries()
    {
        var initialSession = new ScriptedSession();
        var recoverySession = new ScriptedSession
        {
            FailureAtCall = 1,
            Failure = HidSessionFailureKind.NativeIoFailure,
        };
        var factory = new ActorFactory(
            Success(initialSession),
            Success(recoverySession));

        CoolingPadFanBoundaryValidationResult result = await CreateRunner(
                factory,
                new PhysicalPresenceSource(CoolingPadPhysicalPresenceObservation.Absent))
            .RunAsync(
                CoolingPadFanBoundaryKind.DisconnectReconnect,
                static (_, _) => Task.FromResult(true),
                default);

        Assert.Equal(
            CoolingPadFanBoundaryValidationFailure.ReleaseIndeterminate,
            result.Failure);
        Assert.True(result.ReleaseAttempted);
        Assert.False(result.ReleaseAcknowledged);
        Assert.Equal(2, result.WritePlanAttemptCount);
        Assert.Equal(1, result.AcknowledgedWritePlanCount);
        Assert.False(result.RetryAttempted);
        Assert.True(result.ManualSynapseRecoveryRequired);
        Assert.True(result.ManualPowerCycleMayBeRequired);
        Assert.Single(recoverySession.Operations);
    }

    [Fact]
    public async Task Runner_IsOneShot()
    {
        var initialSession = new ScriptedSession();
        var recoverySession = new ScriptedSession();
        var factory = new ActorFactory(
            Success(initialSession),
            Success(recoverySession));
        var runner = CreateRunner(
            factory,
            new PhysicalPresenceSource(CoolingPadPhysicalPresenceObservation.Absent));

        CoolingPadFanBoundaryValidationResult first = await runner.RunAsync(
            CoolingPadFanBoundaryKind.DisconnectReconnect,
            static (_, _) => Task.FromResult(true),
            default);
        CoolingPadFanBoundaryValidationResult second = await runner.RunAsync(
            CoolingPadFanBoundaryKind.DisconnectReconnect,
            static (_, _) => Task.FromResult(true),
            default);

        Assert.True(first.Success);
        Assert.Equal(
            CoolingPadFanBoundaryValidationFailure.AlreadyRun,
            second.Failure);
        Assert.Equal(2, factory.OpenCount);
    }

    [Theory]
    [InlineData(
        (int)CoolingPadFanBoundaryKind.DisconnectReconnect,
        CoolingPadFanBoundaryValidationCommand.DisconnectReconnectConfirmation,
        "PAD-DISCONNECT-RECONNECT-COMPLETED")]
    [InlineData(
        (int)CoolingPadFanBoundaryKind.LowPowerResume,
        CoolingPadFanBoundaryValidationCommand.LowPowerResumeConfirmation,
        "SYSTEM-SLEEP-RESUME-COMPLETED")]
    public async Task Command_RequiresExactTargetAndKeepsAdmissionClosed(
        int boundaryValue,
        string confirmation,
        string boundaryResponse)
    {
        var boundary = (CoolingPadFanBoundaryKind)boundaryValue;
        int calls = 0;
        var output = new StringWriter();
        var responses = new List<string>
        {
            "FAN-2200-CONFIRMED",
            boundaryResponse,
            "FAN-2200-STILL-ACTIVE",
            "FAN-RELEASE-CONFIRMED",
        };
        if (boundary == CoolingPadFanBoundaryKind.DisconnectReconnect)
        {
            responses.Insert(1, "PAD-DISCONNECTED-CONFIRMED");
        }
        var command = new CoolingPadFanBoundaryValidationCommand(
            async (selected, confirm, cancellationToken) =>
            {
                calls++;
                Assert.Equal(boundary, selected);
                CoolingPadFanBoundaryStage[] stages = selected
                    == CoolingPadFanBoundaryKind.DisconnectReconnect
                    ? Enum.GetValues<CoolingPadFanBoundaryStage>()
                    : Enum.GetValues<CoolingPadFanBoundaryStage>()
                        .Where(static stage =>
                            stage != CoolingPadFanBoundaryStage.Disconnected)
                        .ToArray();
                foreach (CoolingPadFanBoundaryStage stage in stages)
                {
                    Assert.True(await confirm(stage, cancellationToken));
                }
                return SuccessfulResult(boundary);
            },
            new StringReader(string.Join(Environment.NewLine, responses)),
            output);

        Assert.Equal(2, await command.RunAsync(boundary, [], default));
        Assert.Equal(0, calls);
        int exitCode = await command.RunAsync(
            boundary,
            ["--confirm-target", confirmation],
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, calls);
        string json = output.ToString()[output.ToString().IndexOf('{')..];
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty(
            "productionAdmitted").GetBoolean());
        Assert.False(document.RootElement.GetProperty("retryAllowed").GetBoolean());
        Assert.False(document.RootElement.GetProperty(
            "activeModeReadbackConfirmed").GetBoolean());
        Assert.False(document.RootElement.GetProperty(
            "physicalRpmReadbackConfirmed").GetBoolean());
        Assert.Equal(
            boundary == CoolingPadFanBoundaryKind.DisconnectReconnect,
            document.RootElement.GetProperty(
                "physicalAbsenceReadbackConfirmed").GetBoolean());
        Assert.False(document.RootElement.GetProperty(
            "lowPowerEventReadbackConfirmed").GetBoolean());
        Assert.DoesNotContain("C:\\", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serial", json, StringComparison.OrdinalIgnoreCase);
    }

    private static CoolingPadFanBoundaryValidationResult SuccessfulResult(
        CoolingPadFanBoundaryKind boundary) => new(
        boundary,
        InitialApplyAttempted: true,
        InitialApplyAcknowledged: true,
        InitialPhysicalConfirmed: true,
        InitialHandleClosedBeforeBoundary: true,
        PhysicalAbsenceObserved:
            boundary == CoolingPadFanBoundaryKind.DisconnectReconnect ? true : null,
        BoundaryCompleted: true,
        ReopenedAfterBoundary: true,
        Retained2200PhysicallyConfirmedBeforeWrite: true,
        ReleaseAttempted: true,
        ReleaseAcknowledged: true,
        ReleasePhysicalConfirmed: true,
        WritePlanAttemptCount: 2,
        AcknowledgedWritePlanCount: 2,
        RetryAttempted: false,
        ActiveModeReadbackConfirmed: false,
        PhysicalRpmReadbackConfirmed: false,
        ManualSynapseRecoveryRequired: false,
        ManualPowerCycleMayBeRequired: false,
        ProductionWriteAdmitted: false,
        CoolingPadFanBoundaryValidationFailure.None);

    private static CoolingPadFanBoundaryValidationRunner CreateRunner(
        IValidationActorFactory actorFactory,
        ICoolingPadPhysicalPresenceSource physicalPresenceSource) => new(
        static () => new ValidationRunContext(),
        actorFactory,
        physicalPresenceSource,
        TimeProvider.System,
        new PendingDisposalRegistry());

    private static HidSessionResult<ValidationActor> Success(
        ScriptedSession session) => new(
        new ValidationActor(
            session,
            new UnsupportedBatteryCommands(),
            new UnsupportedTemporaryCommands()),
        null);

    private static (byte CommandId, string Arguments, byte TransactionId)[] Operations(
        ScriptedSession session) => session.Operations
        .Select(operation => (
            operation.Command.CommandId,
            Convert.ToHexString(operation.Command.Arguments.Span),
            operation.TransactionId))
        .ToArray();

    private sealed class ActorFactory(
        params HidSessionResult<ValidationActor>[] results) : IValidationActorFactory
    {
        private readonly Queue<HidSessionResult<ValidationActor>> results =
            new(results);

        internal int OpenCount { get; private set; }

        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context)
        {
            OpenCount++;
            return ValueTask.FromResult(results.Dequeue());
        }
    }

    private sealed record Operation(RazerCommandSpec Command, byte TransactionId);

    private sealed class PhysicalPresenceSource(
        CoolingPadPhysicalPresenceObservation observation)
        : ICoolingPadPhysicalPresenceSource
    {
        public CoolingPadPhysicalPresenceObservation Observe() => observation;
    }

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
