using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadLightingBoundaryValidationTests
{
    [Theory]
    [InlineData((int)CoolingPadLightingBoundaryKind.DisconnectReconnect)]
    [InlineData((int)CoolingPadLightingBoundaryKind.LowPowerResume)]
    [InlineData((int)CoolingPadLightingBoundaryKind.ControllerHandoff)]
    public async Task Runner_RequiresActiveEpochBeforePostBoundaryHandback(
        int boundaryValue)
    {
        var boundary = (CoolingPadLightingBoundaryKind)boundaryValue;
        int activeCalls = 0;
        int handbackCalls = 0;
        var stages = new List<CoolingPadLightingBoundaryStage>();
        var runner = new CoolingPadLightingBoundaryValidationRunner(
            async (selected, confirm, cancellationToken) =>
            {
                activeCalls++;
                Assert.Equal(boundary, selected);
                foreach (CoolingPadLightingBoundaryStage stage in
                    ActiveStages(selected))
                {
                    Assert.True(await confirm(stage, cancellationToken));
                }
                return SuccessfulActiveBoundary(selected);
            },
            async (confirm, cancellationToken) =>
            {
                handbackCalls++;
                foreach (CoolingPadLightingHandbackValidationStage stage in
                    SuccessfulHandbackStages)
                {
                    Assert.True(await confirm(stage, cancellationToken));
                }
                return SuccessfulHandback();
            });

        CoolingPadLightingBoundaryValidationResult result = await runner.RunAsync(
            boundary,
            (stage, _) =>
            {
                stages.Add(stage);
                return Task.FromResult(true);
            },
            default);

        Assert.True(result.Success);
        Assert.True(result.ActiveEpochValidation.Success);
        Assert.True(result.ActiveEpochValidation.HandleOpenAtBoundary);
        Assert.True(result.ActiveEpochValidation.HandleClosedAfterBoundary);
        Assert.NotNull(result.PostBoundaryValidation);
        Assert.True(result.PostBoundaryValidation!.Success);
        Assert.True(result.ReopenedAndQueriedAfterBoundary);
        Assert.True(result.PostBoundaryHandleClosed);
        Assert.True(result.PostBoundaryPriorPresetResumed);
        Assert.False(result.RetryAttempted);
        Assert.False(result.ManualRecoveryRequired);
        Assert.False(result.ProductionWriteAdmitted);
        Assert.Equal(1, activeCalls);
        Assert.Equal(1, handbackCalls);
        Assert.Equal(ExpectedStages(boundary), stages);
    }

    [Fact]
    public async Task Runner_ClosedHandleAtBoundaryCannotSucceed()
    {
        int handbackCalls = 0;
        CoolingPadLightingActiveBoundaryValidationResult invalid =
            SuccessfulActiveBoundary(
                CoolingPadLightingBoundaryKind.DisconnectReconnect) with
            {
                HandleOpenAtBoundary = false,
            };
        var runner = new CoolingPadLightingBoundaryValidationRunner(
            (_, _, _) => Task.FromResult(invalid),
            (_, _) =>
            {
                handbackCalls++;
                return Task.FromResult(SuccessfulHandback());
            });

        CoolingPadLightingBoundaryValidationResult result = await runner.RunAsync(
            CoolingPadLightingBoundaryKind.DisconnectReconnect,
            static (_, _) => Task.FromResult(true),
            default);

        Assert.False(result.Success);
        Assert.Equal(
            CoolingPadLightingBoundaryValidationFailure
                .ActiveBoundaryValidationFailed,
            result.Failure);
        Assert.False(result.ActiveEpochValidation.Success);
        Assert.Equal(0, handbackCalls);
    }

    [Fact]
    public async Task Runner_LowPowerRequiresIndependentEventReadback()
    {
        CoolingPadLightingActiveBoundaryValidationResult invalid =
            SuccessfulActiveBoundary(CoolingPadLightingBoundaryKind.LowPowerResume)
            with
            {
                LowPowerEventReadbackConfirmed = false,
            };
        var runner = new CoolingPadLightingBoundaryValidationRunner(
            (_, _, _) => Task.FromResult(invalid),
            (_, _) => Task.FromResult(SuccessfulHandback()));

        CoolingPadLightingBoundaryValidationResult result = await runner.RunAsync(
            CoolingPadLightingBoundaryKind.LowPowerResume,
            static (_, _) => Task.FromResult(true),
            default);

        Assert.False(result.Success);
        Assert.False(result.ActiveEpochValidation.Success);
        Assert.Null(result.PostBoundaryValidation);
    }

    [Fact]
    public async Task Runner_ControllerRequiresConcurrentOwnershipAttempt()
    {
        CoolingPadLightingActiveBoundaryValidationResult invalid =
            SuccessfulActiveBoundary(CoolingPadLightingBoundaryKind.ControllerHandoff)
            with
            {
                ConcurrentControllerOwnershipAttempted = false,
            };
        var runner = new CoolingPadLightingBoundaryValidationRunner(
            (_, _, _) => Task.FromResult(invalid),
            (_, _) => Task.FromResult(SuccessfulHandback()));

        CoolingPadLightingBoundaryValidationResult result = await runner.RunAsync(
            CoolingPadLightingBoundaryKind.ControllerHandoff,
            static (_, _) => Task.FromResult(true),
            default);

        Assert.False(result.Success);
        Assert.False(result.ActiveEpochValidation.Success);
        Assert.Null(result.PostBoundaryValidation);
    }

    [Fact]
    public async Task Runner_PostBoundaryIndeterminateFailureIsNeverRetried()
    {
        var runner = new CoolingPadLightingBoundaryValidationRunner(
            static (boundary, _, _) =>
                Task.FromResult(SuccessfulActiveBoundary(boundary)),
            static (_, _) => Task.FromResult(FailedHandback()));

        CoolingPadLightingBoundaryValidationResult result = await runner.RunAsync(
            CoolingPadLightingBoundaryKind.LowPowerResume,
            static (_, _) => Task.FromResult(true),
            default);

        Assert.Equal(
            CoolingPadLightingBoundaryValidationFailure
                .PostBoundaryValidationFailed,
            result.Failure);
        Assert.NotNull(result.PostBoundaryValidation);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.RetryAttempted);
    }

    [Fact]
    public async Task Runner_IsOneShot()
    {
        var runner = new CoolingPadLightingBoundaryValidationRunner(
            static (boundary, _, _) =>
                Task.FromResult(SuccessfulActiveBoundary(boundary)),
            static (_, _) => Task.FromResult(SuccessfulHandback()));

        CoolingPadLightingBoundaryValidationResult first = await runner.RunAsync(
            CoolingPadLightingBoundaryKind.LowPowerResume,
            static (_, _) => Task.FromResult(true),
            default);
        CoolingPadLightingBoundaryValidationResult second = await runner.RunAsync(
            CoolingPadLightingBoundaryKind.LowPowerResume,
            static (_, _) => Task.FromResult(true),
            default);

        Assert.True(first.Success);
        Assert.Equal(
            CoolingPadLightingBoundaryValidationFailure.AlreadyRun,
            second.Failure);
    }

    [Fact]
    public async Task ActiveRunner_DisconnectCrossesBoundaryWithHandleOpenAndFaults()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x80]));
        session.SubmitResults.Enqueue(Success([]));
        var stages = new List<CoolingPadLightingBoundaryStage>();
        CoolingPadLightingActiveBoundaryValidationRunner runner = CreateActiveRunner(
            session,
            CoolingPadPhysicalPresenceObservation.Absent);

        CoolingPadLightingActiveBoundaryValidationResult result = await runner.RunAsync(
            CoolingPadLightingBoundaryKind.DisconnectReconnect,
            (stage, _) =>
            {
                stages.Add(stage);
                return Task.FromResult(true);
            },
            default);

        Assert.True(result.Success);
        Assert.True(result.HandleOpenAtBoundary);
        Assert.True(result.PhysicalAbsenceObserved);
        Assert.Equal(
            CoolingPadLightingSyncState.Faulted,
            result.CoordinatorStateAfterBoundary);
        Assert.False(result.LifecycleCleanupAttempted);
        Assert.True(result.ManualRecoveryRequired);
        Assert.True(result.HandleClosedAfterBoundary);
        Assert.False(result.RetryAttempted);
        Assert.Equal(1, session.ExecuteCount);
        Assert.Equal(1, session.SubmitCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(ActiveStages(result.Boundary), stages);
    }

    [Fact]
    public async Task ActiveRunner_LowPowerObservesEventAndCleansUpAtSuspend()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x80]));
        session.SubmitResults.Enqueue(Success([]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x80]));
        var observer = new RecordingLowPowerObserver();
        CoolingPadLightingActiveBoundaryValidationRunner runner = CreateActiveRunner(
            session,
            CoolingPadPhysicalPresenceObservation.Present,
            lowPowerObserver: observer);

        CoolingPadLightingActiveBoundaryValidationResult result = await runner.RunAsync(
            CoolingPadLightingBoundaryKind.LowPowerResume,
            static (_, _) => Task.FromResult(true),
            default);

        Assert.True(result.Success);
        Assert.True(observer.CleanupInvoked);
        Assert.True(result.LowPowerEventReadbackConfirmed);
        Assert.True(result.LifecycleCleanupAttempted);
        Assert.True(result.LifecycleCleanupAcknowledged);
        Assert.Equal(
            CoolingPadLightingSyncState.WaitingForPad,
            result.CoordinatorStateAfterBoundary);
        Assert.True(result.HandleClosedAfterBoundary);
        Assert.Equal(2, session.ExecuteCount);
        Assert.Equal(1, session.SubmitCount);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task ActiveRunner_ControllerDelegatesWhileSameHandleRemainsOpen()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x80]));
        session.SubmitResults.Enqueue(Success([]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x00]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x80]));
        var observations = new Queue<SynapseActivityObservation>(
        [
            SynapseActivityObservation.Active,
            SynapseActivityObservation.Inactive,
        ]);
        CoolingPadLightingActiveBoundaryValidationRunner runner = CreateActiveRunner(
            session,
            CoolingPadPhysicalPresenceObservation.Present,
            () => observations.Dequeue());

        CoolingPadLightingActiveBoundaryValidationResult result = await runner.RunAsync(
            CoolingPadLightingBoundaryKind.ControllerHandoff,
            static (_, _) => Task.FromResult(true),
            default);

        Assert.True(result.Success);
        Assert.True(result.HandleOpenAtBoundary);
        Assert.True(result.ConcurrentControllerOwnershipAttempted);
        Assert.True(result.ControllerActiveObserved);
        Assert.True(result.ControllerPresetPhysicallyConfirmed);
        Assert.True(result.ControllerInactiveObserved);
        Assert.True(result.LifecycleCleanupAcknowledged);
        Assert.Equal(
            CoolingPadLightingSyncState.WaitingForPad,
            result.CoordinatorStateAfterBoundary);
        Assert.Equal(3, session.ExecuteCount);
        Assert.Equal(1, session.SubmitCount);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task ActiveRunner_ControllerObservationFailureCleansUpOnce()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x80]));
        session.SubmitResults.Enqueue(Success([]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x80]));
        CoolingPadLightingActiveBoundaryValidationRunner runner = CreateActiveRunner(
            session,
            CoolingPadPhysicalPresenceObservation.Present,
            static () => SynapseActivityObservation.Inactive);

        CoolingPadLightingActiveBoundaryValidationResult result = await runner.RunAsync(
            CoolingPadLightingBoundaryKind.ControllerHandoff,
            static (_, _) => Task.FromResult(true),
            default);

        Assert.False(result.Success);
        Assert.Equal(
            CoolingPadLightingActiveBoundaryValidationFailure
                .ControllerActivityNotConfirmed,
            result.Failure);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(2, session.ExecuteCount);
        Assert.Equal(1, session.SubmitCount);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task LowPowerObserver_RequiresOrderedSuspendBeforeResume()
    {
        int cleanupCalls = 0;
        var observer = new WindowsCoolingPadLightingLowPowerBoundaryObserver(
            () => new SequenceLifecycleSource([ServiceLifecycleTrigger.Resumed]));

        bool observed = await observer.ObserveAsync(
            _ =>
            {
                cleanupCalls++;
                return Task.FromResult(true);
            },
            default);

        Assert.False(observed);
        Assert.Equal(0, cleanupCalls);
    }

    [Fact]
    public async Task LowPowerObserver_RequiresCleanupAtSuspendBeforeResume()
    {
        int cleanupCalls = 0;
        var observer = new WindowsCoolingPadLightingLowPowerBoundaryObserver(
            () => new SequenceLifecycleSource(
            [
                ServiceLifecycleTrigger.Suspending,
                ServiceLifecycleTrigger.Suspending,
                ServiceLifecycleTrigger.Resumed,
            ]));

        bool observed = await observer.ObserveAsync(
            _ =>
            {
                cleanupCalls++;
                return Task.FromResult(false);
            },
            default);

        Assert.False(observed);
        Assert.Equal(1, cleanupCalls);
    }

    [Theory]
    [InlineData(
        (int)CoolingPadLightingBoundaryKind.DisconnectReconnect,
        CoolingPadLightingBoundaryValidationCommand.DisconnectReconnectConfirmation)]
    [InlineData(
        (int)CoolingPadLightingBoundaryKind.LowPowerResume,
        CoolingPadLightingBoundaryValidationCommand.LowPowerResumeConfirmation)]
    [InlineData(
        (int)CoolingPadLightingBoundaryKind.ControllerHandoff,
        CoolingPadLightingBoundaryValidationCommand.ControllerHandoffConfirmation)]
    public async Task Command_RequiresExactTargetAndKeepsAdmissionClosed(
        int boundaryValue,
        string confirmation)
    {
        var boundary = (CoolingPadLightingBoundaryKind)boundaryValue;
        int calls = 0;
        var output = new StringWriter();
        var command = new CoolingPadLightingBoundaryValidationCommand(
            (selected, _, _) =>
            {
                calls++;
                Assert.Equal(boundary, selected);
                return Task.FromResult(SuccessfulBoundary(selected));
            },
            new StringReader(string.Empty),
            output);

        Assert.Equal(2, await command.RunAsync(boundary, [], default));
        Assert.Equal(0, calls);
        int exitCode = await command.RunAsync(
            boundary,
            ["--confirm-target", confirmation],
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, calls);
        string text = output.ToString();
        string json = text[text.IndexOf('{')..];
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty(
            "activeOwnershipEpochValidated").GetBoolean());
        Assert.True(document.RootElement.GetProperty(
            "handleOpenAtBoundary").GetBoolean());
        Assert.Equal(
            boundary == CoolingPadLightingBoundaryKind.DisconnectReconnect,
            document.RootElement.GetProperty(
                "physicalAbsenceReadbackConfirmed").GetBoolean());
        Assert.Equal(
            boundary == CoolingPadLightingBoundaryKind.LowPowerResume,
            document.RootElement.GetProperty(
                "lowPowerEventReadbackConfirmed").GetBoolean());
        Assert.Equal(
            boundary == CoolingPadLightingBoundaryKind.ControllerHandoff,
            document.RootElement.GetProperty(
                "controllerProcessHandoffConfirmed").GetBoolean());
        Assert.Equal(
            boundary == CoolingPadLightingBoundaryKind.ControllerHandoff,
            document.RootElement.GetProperty(
                "concurrentControllerOwnershipAttempted").GetBoolean());
        Assert.True(document.RootElement.GetProperty(
            "postBoundaryHandbackValidated").GetBoolean());
        Assert.False(document.RootElement.GetProperty(
            "postBoundaryHandbackRequiresHandleClose").GetBoolean());
        Assert.False(document.RootElement.GetProperty(
            "productionAdmitted").GetBoolean());
        Assert.False(document.RootElement.GetProperty("retryAllowed").GetBoolean());
        Assert.DoesNotContain("C:\\", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serial", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProgramPreflight_DescribesActiveEpochAndOneHandback()
    {
        Assert.Contains(
            "keep the active ownership handle across the selected boundary",
            CoolingPadLightingBoundaryValidationCommand.PreflightAction,
            StringComparison.Ordinal);
        Assert.Contains(
            "observed suspend callback",
            CoolingPadLightingBoundaryValidationCommand.PreflightAction,
            StringComparison.Ordinal);
        Assert.Contains(
            "delegate while the same handle is open",
            CoolingPadLightingBoundaryValidationCommand.PreflightAction,
            StringComparison.Ordinal);
        Assert.Contains(
            "one post-boundary handback validation",
            CoolingPadLightingBoundaryValidationCommand.PreflightAction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "repeat the same bounded handback",
            CoolingPadLightingBoundaryValidationCommand.PreflightAction,
            StringComparison.Ordinal);
        Assert.Contains(
            "active epoch or post-boundary handback is uncertain",
            CoolingPadLightingBoundaryValidationCommand.PreflightRecovery,
            StringComparison.Ordinal);
    }

    private static CoolingPadLightingActiveBoundaryValidationRunner CreateActiveRunner(
        ScriptedSession session,
        CoolingPadPhysicalPresenceObservation presence,
        Func<SynapseActivityObservation>? observeSynapse = null,
        ICoolingPadLightingLowPowerBoundaryObserver? lowPowerObserver = null) => new(
        static () => new ValidationRunContext(),
        new ActorFactory(new ValidationActor(
            session,
            new UnsupportedBatteryCommands(),
            new UnsupportedTemporaryCommands())),
        new PhysicalPresenceSource(presence),
        observeSynapse ?? (() => SynapseActivityObservation.Inactive),
        lowPowerObserver ?? new RecordingLowPowerObserver(),
        TimeProvider.System,
        new PendingDisposalRegistry());

    private static readonly CoolingPadLightingHandbackValidationStage[]
        SuccessfulHandbackStages =
        [
            CoolingPadLightingHandbackValidationStage.BaselinePreset,
            CoolingPadLightingHandbackValidationStage.UniformFrame,
            CoolingPadLightingHandbackValidationStage.BrightnessRestored,
            CoolingPadLightingHandbackValidationStage
                .PriorPresetResumedWithHandleOpen,
        ];

    private static CoolingPadLightingBoundaryStage[] ActiveStages(
        CoolingPadLightingBoundaryKind boundary) => boundary switch
        {
            CoolingPadLightingBoundaryKind.DisconnectReconnect =>
            [
                CoolingPadLightingBoundaryStage.InitialBaselinePreset,
                CoolingPadLightingBoundaryStage.InitialUniformFrame,
                CoolingPadLightingBoundaryStage.Disconnected,
                CoolingPadLightingBoundaryStage.BoundaryCompleted,
            ],
            CoolingPadLightingBoundaryKind.LowPowerResume =>
            [
                CoolingPadLightingBoundaryStage.InitialBaselinePreset,
                CoolingPadLightingBoundaryStage.InitialUniformFrame,
                CoolingPadLightingBoundaryStage.LowPowerBoundaryArmed,
            ],
            CoolingPadLightingBoundaryKind.ControllerHandoff =>
            [
                CoolingPadLightingBoundaryStage.InitialBaselinePreset,
                CoolingPadLightingBoundaryStage.InitialUniformFrame,
                CoolingPadLightingBoundaryStage.ControllerActive,
                CoolingPadLightingBoundaryStage.ControllerPresetConfirmed,
                CoolingPadLightingBoundaryStage.ControllerInactive,
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };

    private static CoolingPadLightingBoundaryStage[] ExpectedStages(
        CoolingPadLightingBoundaryKind boundary) =>
    [
        .. ActiveStages(boundary),
        CoolingPadLightingBoundaryStage.PostBoundaryBaselinePreset,
        CoolingPadLightingBoundaryStage.PostBoundaryUniformFrame,
        CoolingPadLightingBoundaryStage.PostBoundaryBrightnessRestored,
        CoolingPadLightingBoundaryStage
            .PostBoundaryPriorPresetResumedWithHandleOpen,
    ];

    private static CoolingPadLightingActiveBoundaryValidationResult
        SuccessfulActiveBoundary(CoolingPadLightingBoundaryKind boundary) => new(
        boundary,
        BaselineBrightnessRaw: 0x80,
        BaselinePresetPhysicallyConfirmed: true,
        FrameSubmissionAttempted: true,
        FrameSubmissionAcknowledged: true,
        UniformFramePhysicallyConfirmed: true,
        HandleOpenAtBoundary: true,
        PhysicalAbsenceObserved:
            boundary == CoolingPadLightingBoundaryKind.DisconnectReconnect
                ? true
                : null,
        LowPowerEventReadbackConfirmed:
            boundary == CoolingPadLightingBoundaryKind.LowPowerResume,
        ControllerActiveObserved:
            boundary == CoolingPadLightingBoundaryKind.ControllerHandoff
                ? true
                : null,
        ConcurrentControllerOwnershipAttempted:
            boundary == CoolingPadLightingBoundaryKind.ControllerHandoff,
        ControllerPresetPhysicallyConfirmed:
            boundary == CoolingPadLightingBoundaryKind.ControllerHandoff
                ? true
                : null,
        ControllerInactiveObserved:
            boundary == CoolingPadLightingBoundaryKind.ControllerHandoff
                ? true
                : null,
        CoordinatorStateAfterBoundary:
            boundary == CoolingPadLightingBoundaryKind.DisconnectReconnect
                ? CoolingPadLightingSyncState.Faulted
                : CoolingPadLightingSyncState.WaitingForPad,
        LifecycleCleanupAttempted:
            boundary != CoolingPadLightingBoundaryKind.DisconnectReconnect,
        LifecycleCleanupAcknowledged:
            boundary != CoolingPadLightingBoundaryKind.DisconnectReconnect,
        BoundaryCompleted: true,
        HandleClosedAfterBoundary: true,
        RetryAttempted: false,
        ManualRecoveryRequired:
            boundary == CoolingPadLightingBoundaryKind.DisconnectReconnect,
        CoolingPadLightingActiveBoundaryValidationFailure.None);

    private static CoolingPadLightingHandbackValidationResult SuccessfulHandback() =>
        new(
            BaselineBrightnessRaw: 0x80,
            BaselineBrightnessPercent: 51,
            BaselinePresetPhysicallyConfirmed: true,
            FrameSubmissionAttempted: true,
            FrameSubmissionCompleted: true,
            UniformFramePhysicallyConfirmed: true,
            BrightnessRestoreAttempted: true,
            BrightnessRestoreAcknowledged: true,
            BrightnessRestorePhysicallyConfirmed: true,
            PriorPresetResumedWithHandleOpen: true,
            HandleDisposalConfirmedBeforeHandbackCheck: true,
            PriorPresetResumedAutomatically: true,
            ManualPresetRecoveryAttempted: false,
            ManualPresetRecoveryConfirmed: false,
            ManualRecoveryRequired: false,
            CoolingPadLightingHandbackValidationFailure.None);

    private static CoolingPadLightingHandbackValidationResult FailedHandback() => new(
        BaselineBrightnessRaw: 0x80,
        BaselineBrightnessPercent: 51,
        BaselinePresetPhysicallyConfirmed: true,
        FrameSubmissionAttempted: true,
        FrameSubmissionCompleted: false,
        UniformFramePhysicallyConfirmed: false,
        BrightnessRestoreAttempted: false,
        BrightnessRestoreAcknowledged: false,
        BrightnessRestorePhysicallyConfirmed: false,
        PriorPresetResumedWithHandleOpen: false,
        HandleDisposalConfirmedBeforeHandbackCheck: true,
        PriorPresetResumedAutomatically: false,
        ManualPresetRecoveryAttempted: true,
        ManualPresetRecoveryConfirmed: false,
        ManualRecoveryRequired: true,
        CoolingPadLightingHandbackValidationFailure.FrameSubmissionIndeterminate);

    private static CoolingPadLightingBoundaryValidationResult SuccessfulBoundary(
        CoolingPadLightingBoundaryKind boundary) => new(
        boundary,
        SuccessfulActiveBoundary(boundary),
        SuccessfulHandback(),
        ReopenedAndQueriedAfterBoundary: true,
        PostBoundaryHandleClosed: true,
        PostBoundaryPriorPresetResumed: true,
        RetryAttempted: false,
        ManualRecoveryRequired: false,
        ProductionWriteAdmitted: false,
        CoolingPadLightingBoundaryValidationFailure.None);

    private static HidSessionResult<ReadOnlyMemory<byte>> Success(byte[] payload) =>
        new(payload, null);

    private sealed class PhysicalPresenceSource(
        CoolingPadPhysicalPresenceObservation observation)
        : ICoolingPadPhysicalPresenceSource
    {
        public CoolingPadPhysicalPresenceObservation Observe() => observation;
    }

    private sealed class RecordingLowPowerObserver
        : ICoolingPadLightingLowPowerBoundaryObserver
    {
        internal bool CleanupInvoked { get; private set; }

        public async Task<bool> ObserveAsync(
            Func<CancellationToken, Task<bool>> onSuspending,
            CancellationToken cancellationToken)
        {
            CleanupInvoked = true;
            return await onSuspending(cancellationToken);
        }
    }

    private sealed class SequenceLifecycleSource(
        IReadOnlyList<ServiceLifecycleTrigger> triggers)
        : IServiceLifecycleEventSource
    {
        public async IAsyncEnumerable<ServiceLifecycleTrigger> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            foreach (ServiceLifecycleTrigger trigger in triggers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return trigger;
                await Task.Yield();
            }
        }
    }

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context) => ValueTask.FromResult(
            new HidSessionResult<ValidationActor>(actor, null));
    }

    private sealed class ScriptedSession
        : IRazerCommandSession,
          IRazerSetFeatureCommandSession
    {
        internal Queue<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteResults { get; } = [];
        internal Queue<HidSessionResult<ReadOnlyMemory<byte>>> SubmitResults { get; } = [];
        internal int ExecuteCount { get; private set; }
        internal int SubmitCount { get; private set; }
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
            _ = assignTransactionId();
            ExecuteCount++;
            return Task.FromResult(ExecuteResults.Dequeue());
        }

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = assignTransactionId();
            SubmitCount++;
            return Task.FromResult(SubmitResults.Dequeue());
        }

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitBatchAsync(
            IReadOnlyList<RazerCommandSpec> commands,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();

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
