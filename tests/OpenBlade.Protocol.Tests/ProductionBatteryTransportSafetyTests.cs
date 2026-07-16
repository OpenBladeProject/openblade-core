using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProductionBatteryTransportSafetyTests
{
    public static TheoryData<HidSessionFailureKind, HidSessionFailureDiagnostic?, WriteSubmissionState>
        FailureMatrix => new()
        {
            {
                HidSessionFailureKind.ConflictBlocked,
                new(HidSessionFailurePhase.Guard, HidSessionFailureKind.ConflictBlocked, null, null),
                WriteSubmissionState.KnownNotApplied
            },
            {
                HidSessionFailureKind.Canceled,
                null,
                WriteSubmissionState.KnownNotApplied
            },
            {
                HidSessionFailureKind.ProtocolFailure,
                Diagnostic(HidProtocolValidationFailure.Status),
                WriteSubmissionState.KnownNotApplied
            },
            {
                HidSessionFailureKind.ProtocolFailure,
                Diagnostic(HidProtocolValidationFailure.Checksum),
                WriteSubmissionState.Indeterminate
            },
            {
                HidSessionFailureKind.ProtocolFailure,
                Diagnostic(HidProtocolValidationFailure.TransactionMismatch),
                WriteSubmissionState.Indeterminate
            },
            {
                HidSessionFailureKind.ProtocolFailure,
                Diagnostic(HidProtocolValidationFailure.PayloadLength),
                WriteSubmissionState.Indeterminate
            },
            {
                HidSessionFailureKind.Timeout,
                new(HidSessionFailurePhase.SetCompletion, HidSessionFailureKind.Timeout, null, null),
                WriteSubmissionState.Indeterminate
            },
            {
                HidSessionFailureKind.Canceled,
                new(HidSessionFailurePhase.SetCompletion, HidSessionFailureKind.Canceled, null, null),
                WriteSubmissionState.Indeterminate
            },
            {
                HidSessionFailureKind.IdentityBlocked,
                null,
                WriteSubmissionState.Indeterminate
            },
            {
                HidSessionFailureKind.Poisoned,
                null,
                WriteSubmissionState.Indeterminate
            },
            {
                HidSessionFailureKind.Disposed,
                new(HidSessionFailurePhase.SetCompletion, HidSessionFailureKind.Disposed, null, null),
                WriteSubmissionState.Indeterminate
            },
        };

    [Theory]
    [MemberData(nameof(FailureMatrix))]
    public async Task SetterFailureClassificationIsExactAndNeverRetries(
        HidSessionFailureKind failure,
        HidSessionFailureDiagnostic? diagnostic,
        WriteSubmissionState expected)
    {
        var session = new ScriptedSession(
            new HidSessionResult<ReadOnlyMemory<byte>>(default, failure)
            {
                Diagnostic = diagnostic,
            });
        await using var port = CreateAdmittedPort(session);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetChargeLimitAsync(80, default));

        Assert.Equal(expected, error.SubmissionState);
        Assert.Equal(1, session.ExecuteCount);
    }

    [Fact]
    public async Task AmbiguousFailurePoisonsPortUntilFreshSession()
    {
        var poisonedSession = new ScriptedSession(
            new(default, HidSessionFailureKind.Timeout)
            {
                Diagnostic = new(
                    HidSessionFailurePhase.SetCompletion,
                    HidSessionFailureKind.Timeout,
                    null,
                    null),
            },
            Success([0xD0]));
        await using var poisonedPort = CreateAdmittedPort(poisonedSession);

        await Assert.ThrowsAsync<BladeWriteException>(
            () => poisonedPort.SetChargeLimitAsync(80, default));
        BladeWriteException blocked = await Assert.ThrowsAsync<BladeWriteException>(
            () => poisonedPort.SetChargeLimitAsync(80, default));

        Assert.Equal(WriteSubmissionState.Indeterminate, blocked.SubmissionState);
        Assert.Equal(1, poisonedSession.ExecuteCount);

        var freshSession = new ScriptedSession(Success([0xD0]));
        await using var freshPort = CreateAdmittedPort(freshSession);
        await freshPort.SetChargeLimitAsync(80, default);
        Assert.Equal(1, freshSession.ExecuteCount);
    }

    [Fact]
    public async Task CancellationBeforeSubmissionDoesNotCallTransport()
    {
        var session = new ScriptedSession(Success([0xD0]));
        await using var port = CreateAdmittedPort(session);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetChargeLimitAsync(80, cancellation.Token));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(0, session.ExecuteCount);
    }

    [Fact]
    public async Task CancellationWithoutDiagnosticFromPoisonedSessionIsIndeterminate()
    {
        var session = new ScriptedSession(
            new HidSessionResult<ReadOnlyMemory<byte>>(
                default,
                HidSessionFailureKind.Canceled))
        {
            DispositionAfterExecute = RazerCommandSessionDisposition.RecoveryRequired,
        };
        await using var port = CreateAdmittedPort(session);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetChargeLimitAsync(80, default));

        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(1, session.ExecuteCount);
    }

    [Fact]
    public async Task SuccessfulAcknowledgementStillRequiresExactCatalogPayloadEcho()
    {
        var session = new ScriptedSession(Success([0x50]));
        await using var port = CreateAdmittedPort(session);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetChargeLimitAsync(80, default));

        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(1, session.ExecuteCount);
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForTheSameTransportDrain()
    {
        var session = new ScriptedSession(Success([0xD0])) { BlockDispose = true };
        var port = CreateAdmittedPort(session);

        ValueTask first = port.DisposeAsync();
        await session.DisposeStarted.Task;
        ValueTask second = port.DisposeAsync();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        session.ReleaseDispose();
        await first;
        await second;
        Assert.Equal(1, session.DisposeCount);
    }

    private static ProductionBatteryPort CreateAdmittedPort(IRazerCommandSession session)
    {
        DeviceCommandCatalog catalog = DeviceCommandCatalog.Create(
            CapturedFixtureFile.TargetDevice,
            [new(
                CapturedFixtureFile.TargetDevice,
                ProductionBatteryPort.Limit80Action,
                0x07,
                0x12,
                [0xD0],
                true,
                true,
                true,
                true,
                true)]);
        return new ProductionBatteryPort(
            session,
            catalog,
            ProductionBatteryReadEvidence.Production,
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1));
    }

    private static HidSessionFailureDiagnostic Diagnostic(
        HidProtocolValidationFailure validationFailure) => new(
            HidSessionFailurePhase.ProtocolValidation,
            HidSessionFailureKind.ProtocolFailure,
            null,
            null)
        {
            ProtocolValidationFailure = validationFailure,
        };

    private static HidSessionResult<ReadOnlyMemory<byte>> Success(byte[] payload) =>
        new(payload, null);

    private sealed class ScriptedSession(
        params HidSessionResult<ReadOnlyMemory<byte>>[] results) : IRazerCommandSession
    {
        private readonly Queue<HidSessionResult<ReadOnlyMemory<byte>>> results = new(results);

        public int ExecuteCount { get; private set; }

        public RazerCommandSessionDisposition? DispositionAfterExecute { get; init; }

        public bool BlockDispose { get; init; }

        public int DisposeCount { get; private set; }

        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource DisposeRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            _ = assignTransactionId();
            HidSessionResult<ReadOnlyMemory<byte>> result = results.Dequeue();
            if (DispositionAfterExecute is { } disposition)
            {
                Disposition = disposition;
            }

            return Task.FromResult(result);
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeStarted.TrySetResult();
            if (BlockDispose)
            {
                await DisposeRelease.Task;
            }

            Disposition = RazerCommandSessionDisposition.FullyDisposed;
        }

        public void ReleaseDispose() => DisposeRelease.TrySetResult();
    }
}
