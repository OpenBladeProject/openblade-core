using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

public sealed class ThermalTransitionExecutorTests
{
    [Fact]
    public async Task ConfirmedFirstAttemptDoesNotConsumeRetryLease()
    {
        var device = new StubDevice();
        int attempts = 0;

        ThermalTransitionExecution execution = await ThermalTransitionExecutor
            .ExecuteScopedPerformanceAsync(
                device,
                (_, _) =>
                {
                    attempts++;
                    return Task.FromResult(ConfirmedOutcome(writeAttempted: true));
                },
                (_, _) => throw new InvalidOperationException("Retry admission was unexpected."),
                CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal(1, execution.Trace.AttemptCount);
        Assert.False(execution.Trace.RetryConsumed);
        Assert.Equal(ThermalTransitionStage.Confirmation, execution.Trace.Stage);
        Assert.Equal(
            ThermalSubmissionProgress.Acknowledged,
            execution.Trace.SubmissionProgress);
        Assert.Equal(
            ThermalTransitionDisposition.Confirmed,
            execution.Trace.Disposition);
        Assert.Equal(
            new[] { DeviceSubsystem.Performance },
            execution.Trace.Components);
    }

    [Fact]
    public async Task ProvenPreSubmissionFailureConsumesOnlyRetryLease()
    {
        var initialDevice = new StubDevice();
        var retryDevice = new StubDevice();
        int attempts = 0;
        int admissions = 0;

        ThermalTransitionExecution execution = await ThermalTransitionExecutor
            .ExecuteScopedPerformanceAsync(
                initialDevice,
                (device, _) =>
                {
                    attempts++;
                    return Task.FromResult(ReferenceEquals(device, initialDevice)
                        ? KnownNotAppliedOutcome()
                        : ConfirmedOutcome(writeAttempted: true));
                },
                (_, _) =>
                {
                    admissions++;
                    return Task.FromResult(ThermalRetryAdmission.Allow(retryDevice));
                },
                CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(1, admissions);
        Assert.Equal(2, execution.Trace.AttemptCount);
        Assert.True(execution.Trace.RetryConsumed);
        Assert.Equal(
            ThermalTransitionDisposition.Confirmed,
            execution.Trace.Disposition);
    }

    [Fact]
    public async Task RepeatedPreSubmissionFailureStopsAfterFreshSession()
    {
        var device = new StubDevice();
        int attempts = 0;

        ThermalTransitionExecution execution = await ThermalTransitionExecutor
            .ExecuteScopedPerformanceAsync(
                device,
                (_, _) =>
                {
                    attempts++;
                    return Task.FromResult(KnownNotAppliedOutcome());
                },
                (_, _) => Task.FromResult(ThermalRetryAdmission.Allow(device)),
                CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(2, execution.Trace.AttemptCount);
        Assert.True(execution.Trace.RetryConsumed);
        Assert.Equal(ThermalTransitionStage.Preflight, execution.Trace.Stage);
        Assert.Equal(
            ThermalSubmissionProgress.NotSubmitted,
            execution.Trace.SubmissionProgress);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, execution.Trace.Certainty);
        Assert.Equal(ThermalTransitionDisposition.Failed, execution.Trace.Disposition);
    }

    [Fact]
    public async Task RetryAdmissionBlockPreventsSecondAttempt()
    {
        var device = new StubDevice();
        int attempts = 0;

        ThermalTransitionExecution execution = await ThermalTransitionExecutor
            .ExecuteScopedPerformanceAsync(
                device,
                (_, _) =>
                {
                    attempts++;
                    return Task.FromResult(KnownNotAppliedOutcome());
                },
                (_, _) => Task.FromResult(
                    ThermalRetryAdmission.Block("Runtime authority changed.")),
                CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal(1, execution.Trace.AttemptCount);
        Assert.True(execution.Trace.RetryConsumed);
        Assert.Equal("Runtime authority changed.", execution.Trace.RetryBlockReason);
        Assert.Equal(ThermalTransitionStage.Preflight, execution.Trace.Stage);
        Assert.Equal(
            ThermalTransitionDisposition.Blocked,
            execution.Trace.Disposition);
        Assert.Contains("No retry was submitted", execution.Outcome.Result.Message);
    }

    [Fact]
    public async Task RetryAdmissionFaultBecomesTypedBlockWithoutSecondAttempt()
    {
        var device = new StubDevice();
        int attempts = 0;

        ThermalTransitionExecution execution = await ThermalTransitionExecutor
            .ExecuteScopedPerformanceAsync(
                device,
                (_, _) =>
                {
                    attempts++;
                    return Task.FromResult(KnownNotAppliedOutcome());
                },
                (_, _) => throw new IOException("Injected admission failure."),
                CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.True(execution.Trace.RetryConsumed);
        Assert.Equal(
            ThermalTransitionDisposition.Blocked,
            execution.Trace.Disposition);
        Assert.Contains("Injected admission failure", execution.Trace.RetryBlockReason);
        Assert.Contains("Injected admission failure", execution.Outcome.Result.Message);
    }

    [Fact]
    public async Task IndeterminateSubmissionNeverRetries()
    {
        var device = new StubDevice();
        int attempts = 0;

        ThermalTransitionExecution execution = await ThermalTransitionExecutor
            .ExecuteScopedPerformanceAsync(
                device,
                (_, _) =>
                {
                    attempts++;
                    return Task.FromResult(IndeterminateOutcome());
                },
                (_, _) => throw new InvalidOperationException("Retry admission was unexpected."),
                CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.False(execution.Trace.RetryConsumed);
        Assert.Equal(
            ThermalSubmissionProgress.Submitted,
            execution.Trace.SubmissionProgress);
        Assert.Equal(WriteSubmissionState.Indeterminate, execution.Trace.Certainty);
        Assert.Equal(
            ThermalTransitionDisposition.ReconciliationUnavailable,
            execution.Trace.Disposition);
    }

    [Fact]
    public async Task ReconciledPriorStateRetainsPossibleSubmissionChronology()
    {
        var device = new StubDevice();
        ScopedApplyOutcome outcome = IndeterminateOutcome() with
        {
            Readback = State(PerformancePreset.Balanced),
            WriteAttempted = false,
            ConfirmationUnavailable = false,
        };

        ThermalTransitionExecution execution = await ThermalTransitionExecutor
            .ExecuteScopedPerformanceAsync(
                device,
                (_, _) => Task.FromResult(outcome),
                (_, _) => throw new InvalidOperationException("Retry admission was unexpected."),
                CancellationToken.None);

        Assert.Equal(
            ThermalSubmissionProgress.Submitted,
            execution.Trace.SubmissionProgress);
        Assert.Equal(ThermalTransitionStage.Confirmation, execution.Trace.Stage);
        Assert.Equal(ThermalTransitionDisposition.External, execution.Trace.Disposition);
    }

    [Fact]
    public async Task LaterStepRetryDoesNotReplayConfirmedBridge()
    {
        var initialDevice = new StubDevice();
        var retryDevice = new StubDevice();
        int bridgeAttempts = 0;
        int requestedAttempts = 0;

        ThermalTransitionExecution execution = await ThermalTransitionExecutor.ExecuteAsync(
            new[] { DeviceSubsystem.Fans, DeviceSubsystem.Performance },
            initialDevice,
            (device, _) => new ControlCoordinator(device),
            async (runner, token) =>
            {
                await runner.ApplyStepAsync(
                    DeviceSubsystem.Fans,
                    ThermalTransitionStepPurpose.AutomaticBridge,
                    (_, _) =>
                    {
                        bridgeAttempts++;
                        return Task.FromResult(ConfirmedOutcome(writeAttempted: true));
                    },
                    token);
                return await runner.ApplyStepAsync(
                    DeviceSubsystem.Performance,
                    ThermalTransitionStepPurpose.BalancedBridge,
                    (_, _) =>
                    {
                        requestedAttempts++;
                        return Task.FromResult(requestedAttempts == 1
                            ? KnownNotAppliedOutcome()
                            : ConfirmedOutcome(writeAttempted: true));
                    },
                    token);
            },
            (_, _) => Task.FromResult(ThermalRetryAdmission.Allow(retryDevice)),
            CancellationToken.None);

        Assert.Equal(1, bridgeAttempts);
        Assert.Equal(2, requestedAttempts);
        Assert.Equal(3, execution.Trace.AttemptCount);
        Assert.Equal(2, execution.Trace.Steps!.Count);
        Assert.Single(execution.Trace.Steps[0].Attempts);
        Assert.Equal(2, execution.Trace.Steps[1].Attempts.Count);
    }

    [Fact]
    public async Task RecoveryLeaseConsumedByFirstStepPreventsLaterRetry()
    {
        var device = new StubDevice();
        int firstAttempts = 0;
        int secondAttempts = 0;
        int admissions = 0;

        ThermalTransitionExecution execution = await ThermalTransitionExecutor.ExecuteAsync(
            new[] { DeviceSubsystem.Fans, DeviceSubsystem.Performance },
            device,
            (attemptDevice, _) => new ControlCoordinator(attemptDevice),
            async (runner, token) =>
            {
                await runner.ApplyStepAsync(
                    DeviceSubsystem.Fans,
                    ThermalTransitionStepPurpose.AutomaticBridge,
                    (_, _) => Task.FromResult(++firstAttempts == 1
                        ? KnownNotAppliedOutcome()
                        : ConfirmedOutcome(writeAttempted: true)),
                    token);
                return await runner.ApplyStepAsync(
                    DeviceSubsystem.Performance,
                    ThermalTransitionStepPurpose.BalancedBridge,
                    (_, _) =>
                    {
                        secondAttempts++;
                        return Task.FromResult(KnownNotAppliedOutcome());
                    },
                    token);
            },
            (_, _) =>
            {
                admissions++;
                return Task.FromResult(ThermalRetryAdmission.Allow(device));
            },
            CancellationToken.None);

        Assert.Equal(2, firstAttempts);
        Assert.Equal(1, secondAttempts);
        Assert.Equal(1, admissions);
        Assert.True(execution.Trace.RetryConsumed);
        Assert.Contains("recovery lease was already consumed", execution.Outcome.Result.Message);
    }

    [Fact]
    public async Task UndeclaredComponentIsRejectedBeforeOperationRuns()
    {
        var device = new StubDevice();
        int attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ThermalTransitionExecutor.ExecuteAsync(
                new[] { DeviceSubsystem.Performance },
                device,
                (attemptDevice, _) => new ControlCoordinator(attemptDevice),
                (runner, token) => runner.ApplyStepAsync(
                    DeviceSubsystem.Fans,
                    ThermalTransitionStepPurpose.Requested,
                    (_, _) =>
                    {
                        attempts++;
                        return Task.FromResult(ConfirmedOutcome(writeAttempted: true));
                    },
                    token),
                (_, _) => Task.FromResult(ThermalRetryAdmission.Allow(device)),
                CancellationToken.None));

        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task SuccessfulManualSetterWithoutExactReadbackIsAcknowledged()
    {
        var device = new StubDevice();
        ScopedApplyOutcome acknowledged = ConfirmedOutcome(writeAttempted: true) with
        {
            ReadbackMatched = false,
        };

        ThermalTransitionExecution execution = await ThermalTransitionExecutor.ExecuteAsync(
            new[] { DeviceSubsystem.Fans },
            device,
            (attemptDevice, _) => new ControlCoordinator(attemptDevice),
            (runner, token) => runner.ApplyStepAsync(
                DeviceSubsystem.Fans,
                ThermalTransitionStepPurpose.Requested,
                (_, _) => Task.FromResult(acknowledged),
                token),
            (_, _) => throw new InvalidOperationException("Retry was unexpected."),
            CancellationToken.None);

        Assert.Equal(ThermalTransitionStage.Acknowledgement, execution.Trace.Stage);
        Assert.Equal(
            ThermalTransitionDisposition.Acknowledged,
            execution.Trace.Disposition);
    }

    [Fact]
    public async Task BlockedInitialReadRemainsPreflightDespiteReadback()
    {
        var device = new StubDevice();
        ScopedApplyOutcome blocked = ConfirmedOutcome(writeAttempted: false) with
        {
            Result = new ControlResult(ControlResultStatus.Blocked, "Blocked before write."),
            ReadbackMatched = false,
        };

        ThermalTransitionExecution execution = await ThermalTransitionExecutor.ExecuteAsync(
            new[] { DeviceSubsystem.Fans },
            device,
            (attemptDevice, _) => new ControlCoordinator(attemptDevice),
            (runner, token) => runner.ApplyStepAsync(
                DeviceSubsystem.Fans,
                ThermalTransitionStepPurpose.Requested,
                (_, _) => Task.FromResult(blocked),
                token),
            (_, _) => throw new InvalidOperationException("Retry was unexpected."),
            CancellationToken.None);

        Assert.Equal(ThermalTransitionStage.Preflight, execution.Trace.Stage);
        Assert.Equal(ThermalTransitionDisposition.Blocked, execution.Trace.Disposition);
    }

    [Fact]
    public async Task PossibleSubmissionContinuationConsumesSharedRecoveryLease()
    {
        var device = new StubDevice();
        int continuationCalls = 0;
        int admissions = 0;

        ThermalTransitionExecution execution = await ThermalTransitionExecutor.ExecuteAsync(
            new[] { DeviceSubsystem.Fans },
            device,
            (attemptDevice, _) => new ControlCoordinator(attemptDevice),
            async (runner, token) =>
            {
                ScopedApplyOutcome possibleSubmission = await runner.ApplyStepAsync(
                    DeviceSubsystem.Fans,
                    ThermalTransitionStepPurpose.Requested,
                    (_, _) => Task.FromResult(IndeterminateOutcome() with
                    {
                        WriteFailurePhase = BladeWriteFailurePhase
                            .AdjustableFanSecondManualEntrySubmission,
                    }),
                    token);
                return await runner.ApplyFreshContinuationAsync(
                    DeviceSubsystem.Fans,
                    ThermalTransitionStepPurpose.ManualTargetContinuation,
                    possibleSubmission,
                    (_, _) =>
                    {
                        continuationCalls++;
                        return Task.FromResult(ConfirmedOutcome(writeAttempted: true));
                    },
                    token);
            },
            (_, _) =>
            {
                admissions++;
                return Task.FromResult(ThermalRetryAdmission.Allow(device));
            },
            CancellationToken.None);

        Assert.Equal(1, admissions);
        Assert.Equal(1, continuationCalls);
        Assert.True(execution.Trace.RetryConsumed);
        Assert.Equal(
            ThermalTransitionStepPurpose.ManualTargetContinuation,
            execution.Trace.Steps![1].Purpose);
    }

    [Fact]
    public async Task PriorRetryPreventsPostSubmissionContinuationWrite()
    {
        var device = new StubDevice();
        int firstStepAttempts = 0;
        int continuationCalls = 0;

        ThermalTransitionExecution execution = await ThermalTransitionExecutor.ExecuteAsync(
            new[] { DeviceSubsystem.Fans, DeviceSubsystem.Performance },
            device,
            (attemptDevice, _) => new ControlCoordinator(attemptDevice),
            async (runner, token) =>
            {
                await runner.ApplyStepAsync(
                    DeviceSubsystem.Performance,
                    ThermalTransitionStepPurpose.BalancedBridge,
                    (_, _) => Task.FromResult(++firstStepAttempts == 1
                        ? KnownNotAppliedOutcome()
                        : ConfirmedOutcome(writeAttempted: true)),
                    token);
                ScopedApplyOutcome possibleSubmission = await runner.ApplyStepAsync(
                    DeviceSubsystem.Fans,
                    ThermalTransitionStepPurpose.Requested,
                    (_, _) => Task.FromResult(IndeterminateOutcome() with
                    {
                        WriteFailurePhase = BladeWriteFailurePhase
                            .AdjustableFanSecondManualEntrySubmission,
                    }),
                    token);
                return await runner.ApplyFreshContinuationAsync(
                    DeviceSubsystem.Fans,
                    ThermalTransitionStepPurpose.ManualTargetContinuation,
                    possibleSubmission,
                    (_, _) =>
                    {
                        continuationCalls++;
                        return Task.FromResult(ConfirmedOutcome(writeAttempted: true));
                    },
                    token);
            },
            (_, _) => Task.FromResult(ThermalRetryAdmission.Allow(device)),
            CancellationToken.None);

        Assert.Equal(2, firstStepAttempts);
        Assert.Equal(0, continuationCalls);
        Assert.Equal(
            ThermalTransitionDisposition.ReconciliationUnavailable,
            execution.Trace.Disposition);
    }

    [Fact]
    public async Task SuccessfulRestorationDoesNotHideRequestedStepFailure()
    {
        var device = new StubDevice();

        ThermalTransitionExecution execution = await ThermalTransitionExecutor.ExecuteAsync(
            new[] { DeviceSubsystem.Fans, DeviceSubsystem.Performance },
            device,
            (attemptDevice, _) => new ControlCoordinator(attemptDevice),
            async (runner, token) =>
            {
                ScopedApplyOutcome requested = await runner.ApplyStepAsync(
                    DeviceSubsystem.Fans,
                    ThermalTransitionStepPurpose.Requested,
                    (_, _) => Task.FromResult(KnownNotAppliedOutcome() with
                    {
                        WriteFailurePhase = BladeWriteFailurePhase.Unspecified,
                    }),
                    token);
                ScopedApplyOutcome restored = await runner.ApplyStepAsync(
                    DeviceSubsystem.Performance,
                    ThermalTransitionStepPurpose.Restoration,
                    (_, _) => Task.FromResult(ConfirmedOutcome(writeAttempted: true)),
                    token);
                return requested with
                {
                    Readback = restored.Readback,
                    WriteAttempted = restored.WriteAttempted,
                };
            },
            (_, _) => throw new InvalidOperationException("Retry was unexpected."),
            CancellationToken.None);

        Assert.Equal(ThermalTransitionDisposition.Failed, execution.Trace.Disposition);
        Assert.Equal(
            ThermalTransitionDisposition.Confirmed,
            execution.Trace.Steps![1].Attempts[0].Disposition);
    }

    [Fact]
    public async Task FullTransitionLaterKnownFailureIsNotReportedConfirmed()
    {
        var device = new StubDevice();
        ThermalTransitionStepRunner? capturedRunner = null;
        ControlResult applied = new(ControlResultStatus.Applied, "Performance applied.");
        ControlResult failed = new(ControlResultStatus.Failed, "Fans rejected.");
        ControlResult unchanged = new(ControlResultStatus.Unchanged, "Unchanged.");

        ThermalFullTransitionExecution execution = await ThermalTransitionExecutor
            .ExecuteFullTransitionAsync(
                new[] { DeviceSubsystem.Performance, DeviceSubsystem.Fans },
                device,
                (attemptDevice, runner) =>
                {
                    capturedRunner = runner;
                    return new ControlCoordinator(attemptDevice);
                },
                async (_, token) =>
                {
                    await capturedRunner!.ApplyStepAsync(
                        DeviceSubsystem.Performance,
                        ThermalTransitionStepPurpose.Requested,
                        (_, _) => Task.FromResult(ConfirmedOutcome(writeAttempted: true)),
                        token);
                    await capturedRunner.ApplyStepAsync(
                        DeviceSubsystem.Fans,
                        ThermalTransitionStepPurpose.Requested,
                        (_, _) => Task.FromResult(KnownNotAppliedOutcome() with
                        {
                            WriteFailurePhase = BladeWriteFailurePhase.Unspecified,
                        }),
                        token);
                    return new FullTransitionApplyOutcome(
                        new SubsystemApplyResults(applied, failed, unchanged, unchanged),
                        State(PerformancePreset.Performance),
                        new HashSet<DeviceSubsystem> { DeviceSubsystem.Performance },
                        new HashSet<DeviceSubsystem> { DeviceSubsystem.Performance },
                        new HashSet<DeviceSubsystem>(),
                        DeviceCapabilities.Unavailable(),
                        DeviceAccessFailed: false);
                },
                (_, _) => throw new InvalidOperationException(
                    "Preflight recovery was unexpected."),
                (_, _) => throw new InvalidOperationException(
                    "Thermal recovery was unexpected."),
                CancellationToken.None);

        Assert.Equal(ThermalTransitionDisposition.Failed, execution.Trace.Disposition);
        Assert.Equal(
            ThermalTransitionDisposition.Confirmed,
            execution.Trace.Steps![0].Attempts[0].Disposition);
        Assert.Equal(
            ThermalTransitionDisposition.Failed,
            execution.Trace.Steps[1].Attempts[0].Disposition);
    }

    [Fact]
    public async Task FullTransitionZeroStepBlockedResultsAreNotReportedConfirmed()
    {
        var device = new StubDevice();
        ControlResult blocked = new(ControlResultStatus.Blocked, "Externally preserved.");
        ControlResult unchanged = new(ControlResultStatus.Unchanged, "Unchanged.");

        ThermalFullTransitionExecution execution = await ThermalTransitionExecutor
            .ExecuteFullTransitionAsync(
                new[] { DeviceSubsystem.Performance, DeviceSubsystem.Fans },
                device,
                (attemptDevice, _) => new ControlCoordinator(attemptDevice),
                (_, _) => Task.FromResult(new FullTransitionApplyOutcome(
                    new SubsystemApplyResults(blocked, blocked, unchanged, unchanged),
                    State(PerformancePreset.Balanced),
                    new HashSet<DeviceSubsystem>(),
                    new HashSet<DeviceSubsystem>(),
                    new HashSet<DeviceSubsystem>(),
                    DeviceCapabilities.Unavailable(),
                    DeviceAccessFailed: false)),
                (_, _) => throw new InvalidOperationException(
                    "Preflight recovery was unexpected."),
                (_, _) => throw new InvalidOperationException(
                    "Thermal recovery was unexpected."),
                CancellationToken.None);

        Assert.Equal(ThermalTransitionStage.Preflight, execution.Trace.Stage);
        Assert.Equal(ThermalTransitionDisposition.Blocked, execution.Trace.Disposition);
        Assert.Equal(1, execution.Trace.AttemptCount);
    }

    [Fact]
    public async Task FullTransitionPreflightRecoveryCountsLaterThermalStep()
    {
        var initialDevice = new StubDevice();
        var retryDevice = new StubDevice();
        ThermalTransitionStepRunner? capturedRunner = null;
        int recipeAttempts = 0;
        ControlResult applied = new(ControlResultStatus.Applied, "Performance applied.");
        ControlResult unchanged = new(ControlResultStatus.Unchanged, "Unchanged.");

        ThermalFullTransitionExecution execution = await ThermalTransitionExecutor
            .ExecuteFullTransitionAsync(
                new[] { DeviceSubsystem.Performance },
                initialDevice,
                (attemptDevice, runner) =>
                {
                    capturedRunner = runner;
                    return new ControlCoordinator(attemptDevice);
                },
                async (_, token) =>
                {
                    recipeAttempts++;
                    if (recipeAttempts == 1)
                    {
                        return new FullTransitionApplyOutcome(
                            new SubsystemApplyResults(
                                new ControlResult(ControlResultStatus.Failed, "Read failed."),
                                unchanged,
                                unchanged,
                                unchanged),
                            null,
                            new HashSet<DeviceSubsystem>(),
                            new HashSet<DeviceSubsystem>(),
                            new HashSet<DeviceSubsystem>(),
                            null,
                            DeviceAccessFailed: true);
                    }

                    await capturedRunner!.ApplyStepAsync(
                        DeviceSubsystem.Performance,
                        ThermalTransitionStepPurpose.Requested,
                        (_, _) => Task.FromResult(ConfirmedOutcome(writeAttempted: true)),
                        token);
                    return new FullTransitionApplyOutcome(
                        new SubsystemApplyResults(applied, unchanged, unchanged, unchanged),
                        State(PerformancePreset.Performance),
                        new HashSet<DeviceSubsystem> { DeviceSubsystem.Performance },
                        new HashSet<DeviceSubsystem> { DeviceSubsystem.Performance },
                        new HashSet<DeviceSubsystem>(),
                        DeviceCapabilities.Unavailable(),
                        DeviceAccessFailed: false);
                },
                (_, _) => Task.FromResult(ThermalRetryAdmission.Allow(retryDevice)),
                (_, _) => throw new InvalidOperationException(
                    "Thermal recovery was unexpected."),
                CancellationToken.None);

        Assert.Equal(2, recipeAttempts);
        Assert.True(execution.Trace.RetryConsumed);
        Assert.Equal(2, execution.Trace.AttemptCount);
        Assert.Same(retryDevice, execution.Device);
        Assert.Equal(ThermalTransitionDisposition.Confirmed, execution.Trace.Disposition);
    }

    [Fact]
    public async Task FullTransitionPreflightRecoveryCountsUnchangedSecondRecipe()
    {
        var initialDevice = new StubDevice();
        var retryDevice = new StubDevice();
        int recipeAttempts = 0;
        ControlResult unchanged = new(ControlResultStatus.Unchanged, "Unchanged.");

        ThermalFullTransitionExecution execution = await ThermalTransitionExecutor
            .ExecuteFullTransitionAsync(
                new[] { DeviceSubsystem.Performance, DeviceSubsystem.Fans },
                initialDevice,
                (attemptDevice, _) => new ControlCoordinator(attemptDevice),
                (_, _) =>
                {
                    recipeAttempts++;
                    return Task.FromResult(recipeAttempts == 1
                        ? new FullTransitionApplyOutcome(
                            new SubsystemApplyResults(
                                new ControlResult(ControlResultStatus.Failed, "Read failed."),
                                unchanged,
                                unchanged,
                                unchanged),
                            null,
                            new HashSet<DeviceSubsystem>(),
                            new HashSet<DeviceSubsystem>(),
                            new HashSet<DeviceSubsystem>(),
                            null,
                            DeviceAccessFailed: true)
                        : new FullTransitionApplyOutcome(
                            new SubsystemApplyResults(
                                unchanged,
                                unchanged,
                                unchanged,
                                unchanged),
                            State(PerformancePreset.Balanced),
                            new HashSet<DeviceSubsystem>(),
                            new HashSet<DeviceSubsystem>(),
                            new HashSet<DeviceSubsystem>(),
                            DeviceCapabilities.Unavailable(),
                            DeviceAccessFailed: false));
                },
                (_, _) => Task.FromResult(ThermalRetryAdmission.Allow(retryDevice)),
                (_, _) => throw new InvalidOperationException(
                    "Thermal recovery was unexpected."),
                CancellationToken.None);

        Assert.Equal(2, recipeAttempts);
        Assert.True(execution.Trace.RetryConsumed);
        Assert.Equal(2, execution.Trace.AttemptCount);
        Assert.Empty(execution.Trace.Steps!);
        Assert.Equal(ThermalTransitionDisposition.Confirmed, execution.Trace.Disposition);
    }

    [Fact]
    public async Task CompositeHyperBoostCommandRetriesOnlyTypedAdapterPreflight()
    {
        var initialDevice = new StubDevice();
        var retryDevice = new StubDevice();
        int commandAttempts = 0;
        int admissions = 0;
        var runner = new ThermalTransitionStepRunner(
            Guid.NewGuid(),
            [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
            initialDevice,
            (device, _) => new ControlCoordinator(device),
            (_, _) =>
            {
                admissions++;
                return Task.FromResult(ThermalRetryAdmission.Allow(retryDevice));
            });

        await runner.ApplyCommandStepAsync(
            [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
            ThermalTransitionStepPurpose.HyperBoost,
            (_, _) => ++commandAttempts == 1
                ? Task.FromException(new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    BladeWriteFailurePhase.HyperBoostAdapterEligibilityRead,
                    "Injected adapter preflight failure."))
                : Task.CompletedTask,
            failure => failure.SubmissionState
                    == WriteSubmissionState.KnownNotApplied
                && failure.FailurePhase
                    == BladeWriteFailurePhase.HyperBoostAdapterEligibilityRead,
            CancellationToken.None);

        Assert.Equal(2, commandAttempts);
        Assert.Equal(1, admissions);
        Assert.True(runner.RecoveryLeaseConsumed);
        Assert.Same(retryDevice, runner.CurrentDevice);
        ThermalTransitionStepTrace step = Assert.Single(runner.Steps);
        Assert.Equal(
            [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
            step.Components);
        Assert.Equal(2, step.Attempts.Count);
    }

    [Fact]
    public async Task BridgeRetryPreventsSecondLeaseForHyperBoostCommand()
    {
        var device = new StubDevice();
        int bridgeAttempts = 0;
        int commandAttempts = 0;
        int admissions = 0;
        var runner = new ThermalTransitionStepRunner(
            Guid.NewGuid(),
            [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
            device,
            (attemptDevice, _) => new ControlCoordinator(attemptDevice),
            (_, _) =>
            {
                admissions++;
                return Task.FromResult(ThermalRetryAdmission.Allow(device));
            });
        await runner.ApplyStepAsync(
            DeviceSubsystem.Fans,
            ThermalTransitionStepPurpose.AutomaticBridge,
            (_, _) => Task.FromResult(++bridgeAttempts == 1
                ? KnownNotAppliedOutcome()
                : ConfirmedOutcome(writeAttempted: true)),
            CancellationToken.None);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => runner.ApplyCommandStepAsync(
                [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
                ThermalTransitionStepPurpose.HyperBoost,
                (_, _) =>
                {
                    commandAttempts++;
                    return Task.FromException(new BladeWriteException(
                        WriteSubmissionState.KnownNotApplied,
                        BladeWriteFailurePhase.HyperBoostAdapterEligibilityRead,
                        "Injected adapter preflight failure."));
                },
                failure => failure.SubmissionState
                        == WriteSubmissionState.KnownNotApplied
                    && failure.FailurePhase
                        == BladeWriteFailurePhase.HyperBoostAdapterEligibilityRead,
                CancellationToken.None));

        Assert.Equal(2, bridgeAttempts);
        Assert.Equal(1, commandAttempts);
        Assert.Equal(1, admissions);
        Assert.Contains("recovery lease was already consumed", error.Message);
    }

    [Fact]
    public async Task CommandSubmissionAdmissionBlocksBeforeApply()
    {
        var device = new StubDevice();
        int commandAttempts = 0;
        var runner = new ThermalTransitionStepRunner(
            Guid.NewGuid(),
            [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
            device,
            (attemptDevice, _) => new ControlCoordinator(attemptDevice),
            (_, _) => throw new InvalidOperationException(
                "Recovery admission was unexpected."),
            revalidateSubmission: (_, _, _) => Task.FromResult(
                ThermalRetryAdmission.Block("Injected late low-power gate.")));

        CapabilityUnavailableException error =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
                () => runner.ApplyCommandStepAsync(
                    [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
                    ThermalTransitionStepPurpose.HyperBoost,
                    (_, _) =>
                    {
                        commandAttempts++;
                        return Task.CompletedTask;
                    },
                    _ => true,
                    CancellationToken.None));

        Assert.Equal(0, commandAttempts);
        Assert.False(runner.RecoveryLeaseConsumed);
        Assert.Contains("late low-power", error.Message);
        Assert.Equal(
            ThermalTransitionDisposition.Blocked,
            Assert.Single(Assert.Single(runner.Steps).Attempts).Disposition);
    }

    [Fact]
    public async Task SubmissionAdmissionUsesFreshDeviceWithoutConsumingRetry()
    {
        var initialDevice = new StubDevice();
        var admittedDevice = new StubDevice();
        IBladeDevice? commandDevice = null;
        var runner = new ThermalTransitionStepRunner(
            Guid.NewGuid(),
            [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
            initialDevice,
            (attemptDevice, _) => new ControlCoordinator(attemptDevice),
            (_, _) => throw new InvalidOperationException(
                "Recovery admission was unexpected."),
            revalidateSubmission: (_, _, _) => Task.FromResult(
                ThermalRetryAdmission.Allow(admittedDevice)));

        await runner.ApplyCommandStepAsync(
            [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
            ThermalTransitionStepPurpose.HyperBoost,
            (device, _) =>
            {
                commandDevice = device;
                return Task.CompletedTask;
            },
            _ => true,
            CancellationToken.None);

        Assert.Same(admittedDevice, commandDevice);
        Assert.Same(admittedDevice, runner.CurrentDevice);
        Assert.False(runner.RecoveryLeaseConsumed);
    }

    private static ScopedApplyOutcome ConfirmedOutcome(bool writeAttempted) => new(
        new ControlResult(
            writeAttempted ? ControlResultStatus.Applied : ControlResultStatus.Unchanged,
            "Performance confirmed."),
        State(PerformancePreset.Performance),
        writeAttempted,
        ReadbackMatched: true,
        DeviceCapabilities.Unavailable(),
        DeviceAccessFailed: false);

    private static ScopedApplyOutcome KnownNotAppliedOutcome() => new(
        new ControlResult(ControlResultStatus.Failed, "Performance write was rejected."),
        State(PerformancePreset.Balanced),
        WriteAttempted: false,
        ReadbackMatched: false,
        DeviceCapabilities.Unavailable(),
        DeviceAccessFailed: true,
        WriteSubmissionState: WriteSubmissionState.KnownNotApplied,
        WriteFailurePhase: BladeWriteFailurePhase.ThermalPreWriteRead);

    private static ScopedApplyOutcome IndeterminateOutcome() => new(
        new ControlResult(ControlResultStatus.Failed, "Performance confirmation is unavailable."),
        null,
        WriteAttempted: true,
        ReadbackMatched: false,
        DeviceCapabilities.Unavailable(),
        DeviceAccessFailed: true,
        ConfirmationUnavailable: true,
        PreWriteState: State(PerformancePreset.Balanced),
        WriteSubmissionState: WriteSubmissionState.Indeterminate);

    private static ConfirmedDeviceState State(PerformancePreset performance) => new(
        performance,
        FanMode.Automatic,
        null,
        null);

    private sealed class StubDevice : IBladeDevice
    {
        public Task<DeviceCapabilities> ReadCapabilitiesAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ConfirmedDeviceState> ReadStateAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PowerTransitionDeviceStateReadResult> ReadStateForPowerTransitionAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FanRpmReadings?> ReadFanRpmAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AdapterPowerState> ReadAdapterPowerStateAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            GpuClockOffsetSettings? gpuClockOffsets,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetGpuClockOffsetsAsync(
            GpuClockOffsetSettings value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetFanModeAsync(
            FanMode value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetFanControlAsync(
            FanControlSettings value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetFanTargetsAsync(
            int fan1Rpm,
            int fan2Rpm,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetChargeLimitAsync(
            BatteryPolicy value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetBatteryProtectionOffAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetTemporaryFullChargeAsync(
            bool enabled,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetLightingAsync(
            LightingSettings value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<byte> StepKeyboardBrightnessAsync(
            KeyboardBrightnessDirection direction,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ApplyOneShotLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PushAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetPlaybackMuteIndicatorAsync(
            bool muted,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
