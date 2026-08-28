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
