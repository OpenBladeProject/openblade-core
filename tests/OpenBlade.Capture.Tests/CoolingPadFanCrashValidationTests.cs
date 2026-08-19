using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadFanCrashValidationTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.CoolingPadFanCrashValidationTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Runner_ArmsClosesAndReleasesThroughFreshActorOnce()
    {
        var initialSession = new ScriptedSession();
        var recoverySession = new ScriptedSession();
        var factory = new ActorFactory(
            Success(initialSession),
            Success(recoverySession));
        var runner = CreateRunner(factory);

        CoolingPadFanCrashArmResult arm = await runner.ArmAsync(
            static _ => Task.FromResult(true),
            default);
        CoolingPadFanCrashRecoveryResult recovery = await runner.RecoverAsync(
            static _ => Task.FromResult(true),
            static _ => Task.FromResult(true),
            default);

        Assert.True(arm.Success);
        Assert.True(arm.HandleClosedBeforeExit);
        Assert.True(recovery.Success);
        Assert.False(recovery.RetryAttempted);
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
    public async Task Runner_IndeterminateReleaseNeverRetries()
    {
        var initialSession = new ScriptedSession();
        var recoverySession = new ScriptedSession
        {
            FailureAtCall = 1,
            Failure = HidSessionFailureKind.NativeIoFailure,
        };
        var runner = CreateRunner(new ActorFactory(
            Success(initialSession),
            Success(recoverySession)));
        Assert.True((await runner.ArmAsync(
            static _ => Task.FromResult(true), default)).Success);

        CoolingPadFanCrashRecoveryResult recovery = await runner.RecoverAsync(
            static _ => Task.FromResult(true),
            static _ => Task.FromResult(true),
            default);

        Assert.Equal(CoolingPadFanCrashFailure.ReleaseIndeterminate, recovery.Failure);
        Assert.True(recovery.ReleaseAttempted);
        Assert.False(recovery.ReleaseAcknowledged);
        Assert.False(recovery.RetryAttempted);
        Assert.True(recovery.ManualSynapseRecoveryRequired);
        Assert.True(recovery.ManualPowerCycleMayBeRequired);
        Assert.Single(recoverySession.Operations);
    }

    [Fact]
    public async Task MarkerStore_CreatesAndCompletesExactMarker()
    {
        Directory.CreateDirectory(temporaryDirectory);
        string path = Path.Combine(temporaryDirectory, "marker.json");
        var store = new CoolingPadFanCrashMarkerStore(path);
        CoolingPadFanCrashMarker marker = Marker();

        await store.CreateArmedAsync(marker, default);
        Assert.Equal(marker, await store.ReadAsync(default));
        CoolingPadFanCrashMarker completed = marker with
        {
            State = CoolingPadFanCrashMarkerState.UnexpectedExitReleaseCompleted,
            ReleaseAttempted = true,
            ReleasePhysicallyConfirmed = true,
        };
        await store.WriteAsync(completed, default);

        Assert.Equal(completed, await store.ReadAsync(default));
        Assert.Empty(Directory.GetFiles(temporaryDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Command_CtrlCReleasesOnceAndClosesMarkerWithoutCrashClaim()
    {
        var store = new FakeStore();
        var output = new StringWriter();
        int armCalls = 0;
        int recoveryCalls = 0;
        Action? cancel = null;
        var command = Command(
            output,
            store,
            onArm: () => armCalls++,
            onRecover: () => recoveryCalls++,
            responses:
            [
                CoolingPadFanCrashValidationCommand.ArmConfirmation,
                CoolingPadFanCrashValidationCommand.Fixed2200Confirmation,
                CoolingPadFanCrashValidationCommand.Retained2200Confirmation,
                CoolingPadFanCrashValidationCommand.ReleaseConfirmation,
            ],
            wait: token =>
            {
                cancel!();
                return Task.FromCanceled(token);
            },
            register: action =>
            {
                cancel = action;
                return NoopDisposable.Instance;
            });

        int exitCode = await command.RunArmAsync(ArmArguments(), default);

        Assert.Equal(CoolingPadFanCrashValidationCommand.CanceledExitCode, exitCode);
        Assert.Equal(1, armCalls);
        Assert.Equal(1, recoveryCalls);
        Assert.Equal(
            CoolingPadFanCrashMarkerState.GracefulReleaseCompleted,
            store.Marker!.State);
        using JsonDocument result = LastJson(output);
        Assert.False(result.RootElement.GetProperty("unexpectedExitObserved").GetBoolean());
        Assert.False(result.RootElement.GetProperty("crashRecoveryValidated").GetBoolean());
        Assert.False(result.RootElement.GetProperty("retryAttempted").GetBoolean());
        Assert.False(result.RootElement.GetProperty("productionAdmitted").GetBoolean());
    }

    [Fact]
    public async Task Command_DeadExactOriginReleasesOnceAndCompletesMarker()
    {
        var store = new FakeStore { Marker = Marker() };
        var output = new StringWriter();
        int recoveryCalls = 0;
        var command = Command(
            output,
            store,
            onRecover: () => recoveryCalls++,
            originAlive: false,
            responses:
            [
                CoolingPadFanCrashValidationCommand.RecoveryConfirmation,
                CoolingPadFanCrashValidationCommand.Retained2200Confirmation,
                CoolingPadFanCrashValidationCommand.ReleaseConfirmation,
            ]);

        int exitCode = await command.RunRecoveryAsync(
            RecoveryArguments(),
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, recoveryCalls);
        Assert.Equal(
            CoolingPadFanCrashMarkerState.UnexpectedExitReleaseCompleted,
            store.Marker!.State);
        using JsonDocument result = LastJson(output);
        Assert.True(result.RootElement.GetProperty("unexpectedExitObserved").GetBoolean());
        Assert.True(result.RootElement.GetProperty("crashRecoveryValidated").GetBoolean());
        Assert.False(result.RootElement.GetProperty("activeModeReadbackConfirmed").GetBoolean());
        Assert.False(result.RootElement.GetProperty("physicalRpmReadbackConfirmed").GetBoolean());
        Assert.False(result.RootElement.GetProperty("retryAttempted").GetBoolean());
    }

    [Fact]
    public async Task Command_LiveOriginBlocksBeforeRelease()
    {
        var store = new FakeStore { Marker = Marker() };
        int recoveryCalls = 0;
        var command = Command(
            new StringWriter(),
            store,
            onRecover: () => recoveryCalls++,
            originAlive: true);

        int exitCode = await command.RunRecoveryAsync(
            RecoveryArguments(),
            default);

        Assert.Equal(
            CoolingPadFanCrashValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.Equal(0, recoveryCalls);
        Assert.Equal(CoolingPadFanCrashMarkerState.Armed, store.Marker!.State);
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static CoolingPadFanCrashValidationRunner CreateRunner(
        IValidationActorFactory actorFactory) => new(
        static () => new ValidationRunContext(),
        actorFactory,
        TimeProvider.System,
        new PendingDisposalRegistry());

    private static CoolingPadFanCrashValidationCommand Command(
        StringWriter output,
        FakeStore store,
        Action? onArm = null,
        Action? onRecover = null,
        IReadOnlyList<string>? responses = null,
        bool originAlive = false,
        Func<CancellationToken, Task>? wait = null,
        Func<Action, IDisposable>? register = null) => new(
        async (confirm, cancellationToken) =>
        {
            onArm?.Invoke();
            bool physical = await confirm(cancellationToken);
            return new CoolingPadFanCrashArmResult(
                true, true, physical, true, !physical, false,
                physical
                    ? CoolingPadFanCrashFailure.None
                    : CoolingPadFanCrashFailure.ApplyPhysicalNotConfirmed);
        },
        async (confirmRetained, confirmRelease, cancellationToken) =>
        {
            onRecover?.Invoke();
            bool retained = await confirmRetained(cancellationToken);
            bool released = retained && await confirmRelease(cancellationToken);
            return new CoolingPadFanCrashRecoveryResult(
                retained,
                ReleaseAttempted: retained,
                ReleaseAcknowledged: retained,
                ReleasePhysicalConfirmed: released,
                HandleClosed: true,
                RetryAttempted: false,
                ManualSynapseRecoveryRequired: !released,
                ManualPowerCycleMayBeRequired: false,
                released
                    ? CoolingPadFanCrashFailure.None
                    : CoolingPadFanCrashFailure.ReleasePhysicalNotConfirmed);
        },
        new QueueReader(new Queue<string>(responses ??
        [
            CoolingPadFanCrashValidationCommand.ArmConfirmation,
        ])),
        output,
        _ => store,
        ProcessIdentity,
        (_, _) => originAlive,
        wait ?? (_ => Task.CompletedTask),
        register ?? (_ => NoopDisposable.Instance));

    private static IReadOnlyList<string> ArmArguments() =>
    [
        "--confirm-target",
        CoolingPadFanCrashValidationCommand.ArmConfirmation,
        "--marker",
        "private-marker.json",
    ];

    private static IReadOnlyList<string> RecoveryArguments() =>
    [
        "--confirm-target",
        CoolingPadFanCrashValidationCommand.RecoveryConfirmation,
        "--marker",
        "private-marker.json",
    ];

    private static CaptureProcessIdentity ProcessIdentity() => new(
        123,
        456,
        "0.5.0+test",
        new string('A', 64));

    private static CoolingPadFanCrashMarker Marker() => new(
        1,
        CoolingPadFanCrashValidationCommand.Target,
        "0.5.0+test",
        new string('A', 64),
        123,
        456,
        CoolingPadFanCrashMarkerState.Armed,
        Fixed2200PhysicallyConfirmed: true,
        HandleClosedBeforeExit: true,
        ReleaseAttempted: false,
        ReleasePhysicallyConfirmed: false,
        ManualRecoveryRequired: false);

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

    private static JsonDocument LastJson(StringWriter output)
    {
        string text = output.ToString();
        return JsonDocument.Parse(text[text.IndexOf('{', StringComparison.Ordinal)..]);
    }

    private sealed class FakeStore : ICoolingPadFanCrashMarkerStore
    {
        internal CoolingPadFanCrashMarker? Marker { get; set; }

        public Task CreateArmedAsync(
            CoolingPadFanCrashMarker marker,
            CancellationToken cancellationToken)
        {
            if (Marker is not null)
            {
                throw new IOException("Marker exists.");
            }
            Marker = marker;
            return Task.CompletedTask;
        }

        public Task<CoolingPadFanCrashMarker?> ReadAsync(
            CancellationToken cancellationToken) => Task.FromResult(Marker);

        public Task WriteAsync(
            CoolingPadFanCrashMarker marker,
            CancellationToken cancellationToken)
        {
            Marker = marker;
            return Task.CompletedTask;
        }
    }

    private sealed class ActorFactory(
        params HidSessionResult<ValidationActor>[] results)
        : IValidationActorFactory
    {
        private readonly Queue<HidSessionResult<ValidationActor>> results = new(results);

        internal int OpenCount { get; private set; }

        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context)
        {
            OpenCount++;
            return ValueTask.FromResult(results.Dequeue());
        }
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
            Operations.Add(new Operation(command, transaction));
            if (FailureAtCall == Operations.Count)
            {
                return Task.FromResult(
                    new HidSessionResult<ReadOnlyMemory<byte>>(default, Failure));
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(
                command.Arguments,
                null));
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

    private sealed class QueueReader(Queue<string> responses) : TextReader
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                responses.TryDequeue(out string? response) ? response : null);
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
