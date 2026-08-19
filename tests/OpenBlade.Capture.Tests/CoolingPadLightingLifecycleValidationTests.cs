using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadLightingLifecycleValidationTests
{
    [Fact]
    public async Task Runner_SubmitsOneFrameBlackoutAndExactBrightnessRestore()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x7F]));
        session.SubmitResults.Enqueue(Success([]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x00]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x7F]));
        var stages = new List<CoolingPadLightingValidationStage>();

        CoolingPadLightingLifecycleValidationResult result = await CreateRunner(session)
            .RunAsync(
                (stage, _) =>
                {
                    stages.Add(stage);
                    return Task.FromResult(true);
                },
                default);

        Assert.True(result.Success);
        Assert.Equal<byte?>(0x7F, result.BaselineBrightnessRaw);
        Assert.Equal<byte?>(50, result.BaselineBrightnessPercent);
        Assert.True(result.ManualPresetRestoreRequired);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(
            [
                CoolingPadLightingValidationStage.UniformFrame,
                CoolingPadLightingValidationStage.Blackout,
                CoolingPadLightingValidationStage.BrightnessRestored,
            ],
            stages);
        Assert.Equal(
            [
                (OperationKind.Execute, (byte)0x84, "010500", (byte)1),
                (OperationKind.Submit, (byte)0x03,
                    "0000000011" + string.Concat(
                        Enumerable.Repeat("123456", 18)), (byte)2),
                (OperationKind.Execute, (byte)0x04, "010000", (byte)3),
                (OperationKind.Execute, (byte)0x04, "01007F", (byte)4),
            ],
            session.Operations.Select(operation => (
                operation.Kind,
                operation.Command.CommandId,
                Convert.ToHexString(operation.Command.Arguments.Span),
                operation.TransactionId)).ToArray());
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Runner_ZeroBaselineStopsBeforeAnyWrite()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x00]));

        CoolingPadLightingLifecycleValidationResult result = await CreateRunner(session)
            .RunAsync((_, _) => throw new InvalidOperationException(), default);

        Assert.Equal(
            CoolingPadLightingValidationFailure.BaselineBrightnessZero,
            result.Failure);
        Assert.False(result.FrameSubmissionAttempted);
        Assert.Single(session.Operations);
        Assert.Equal(OperationKind.Execute, session.Operations[0].Kind);
    }

    [Fact]
    public async Task Runner_FrameFailureStopsWithoutRetryOrBrightnessWrite()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x7F]));
        session.SubmitResults.Enqueue(
            new(default, HidSessionFailureKind.NativeIoFailure));

        CoolingPadLightingLifecycleValidationResult result = await CreateRunner(session)
            .RunAsync((_, _) => throw new InvalidOperationException(), default);

        Assert.Equal(
            CoolingPadLightingValidationFailure.FrameSubmissionIndeterminate,
            result.Failure);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(2, session.Operations.Count);
        Assert.Equal(OperationKind.Submit, session.Operations[1].Kind);
    }

    [Fact]
    public async Task Runner_BlackoutRejectionStillRestoresSavedBrightnessOnce()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x7F]));
        session.SubmitResults.Enqueue(Success([]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x00]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x7F]));

        CoolingPadLightingLifecycleValidationResult result = await CreateRunner(session)
            .RunAsync(
                (stage, _) => Task.FromResult(
                    stage != CoolingPadLightingValidationStage.Blackout),
                default);

        Assert.Equal(
            CoolingPadLightingValidationFailure.BlackoutNotConfirmed,
            result.Failure);
        Assert.True(result.BrightnessRestoreAcknowledged);
        Assert.Equal(4, session.Operations.Count);
        Assert.Equal("01007F", Convert.ToHexString(
            session.Operations[^1].Command.Arguments.Span));
    }

    [Fact]
    public async Task Runner_RestoreFailureIsManualRecoveryAndNeverRetries()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x7F]));
        session.SubmitResults.Enqueue(Success([]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x00]));
        session.ExecuteResults.Enqueue(
            new(default, HidSessionFailureKind.NativeIoFailure));

        CoolingPadLightingLifecycleValidationResult result = await CreateRunner(session)
            .RunAsync((_, _) => Task.FromResult(true), default);

        Assert.Equal(
            CoolingPadLightingValidationFailure.BrightnessRestoreIndeterminate,
            result.Failure);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(4, session.Operations.Count);
    }

    [Fact]
    public async Task Command_RequiresExactTargetAndEmitsBoundedEvidence()
    {
        int calls = 0;
        var output = new StringWriter();
        var command = new CoolingPadLightingLifecycleValidationCommand(
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new CoolingPadLightingLifecycleValidationResult(
                    0x7F,
                    50,
                    FrameSubmissionAttempted: true,
                    FrameSubmissionCompleted: true,
                    UniformFramePhysicallyConfirmed: true,
                    BlackoutAttempted: true,
                    BlackoutAcknowledged: true,
                    BlackoutPhysicallyConfirmed: true,
                    BrightnessRestoreAttempted: true,
                    BrightnessRestoreAcknowledged: true,
                    BrightnessRestorePhysicallyConfirmed: true,
                    ManualPresetRestoreRequired: true,
                    ManualRecoveryRequired: false,
                    CoolingPadLightingValidationFailure.None));
            },
            new StringReader(string.Empty),
            output);

        Assert.Equal(2, await command.RunAsync([], default));
        Assert.Equal(0, calls);
        int exitCode = await command.RunAsync(
            ["--confirm-target", CoolingPadLightingLifecycleValidationCommand.Confirmation],
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, calls);
        string json = output.ToString()[output.ToString().IndexOf('{')..];
        json = json[..(json.LastIndexOf('}') + 1)];
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("productionAdmitted").GetBoolean());
        Assert.False(document.RootElement.GetProperty(
            "priorEffectHandbackValidated").GetBoolean());
        Assert.Equal("123456", document.RootElement.GetProperty(
            "uniformColor").GetString());
        Assert.Contains("restore the prior lighting preset", output.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CadenceRunner_ReservesBoundedSequenceAndRestoresBrightness()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x7F]));
        session.SubmitResults.Enqueue(Success([]));
        session.SubmitResults.Enqueue(Success([]));
        session.SubmitResults.Enqueue(Success([]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x7F]));
        int delays = 0;
        var stages = new List<CoolingPadLightingCadenceValidationStage>();

        CoolingPadLightingCadenceValidationResult result =
            await CreateCadenceRunner(
                    session,
                    (_, _) =>
                    {
                        delays++;
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
        Assert.Equal(3, result.RequestedFrameCount);
        Assert.Equal(3, result.SubmittedFrameCount);
        Assert.True(result.CadenceTimingConfirmed);
        Assert.Equal(2, delays);
        Assert.Equal(
            [
                CoolingPadLightingCadenceValidationStage.CadenceStable,
                CoolingPadLightingCadenceValidationStage.BrightnessRestored,
            ],
            stages);
        Assert.Equal(
            [
                (OperationKind.Execute, (byte)0x84, (byte)1),
                (OperationKind.Submit, (byte)0x03, (byte)2),
                (OperationKind.Submit, (byte)0x03, (byte)3),
                (OperationKind.Submit, (byte)0x03, (byte)4),
                (OperationKind.Execute, (byte)0x04, (byte)5),
            ],
            session.Operations.Select(operation => (
                operation.Kind,
                operation.Command.CommandId,
                operation.TransactionId)).ToArray());
        Assert.Equal("01007F", Convert.ToHexString(
            session.Operations[^1].Command.Arguments.Span));
    }

    [Fact]
    public async Task CadenceRunner_RejectsFrameStartsFasterThanConfiguredCeiling()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x7F]));
        session.SubmitResults.Enqueue(Success([]));
        session.SubmitResults.Enqueue(Success([]));
        session.SubmitResults.Enqueue(Success([]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x7F]));
        int confirmations = 0;

        CoolingPadLightingCadenceValidationResult result =
            await CreateCadenceRunner(
                    session,
                    elapsedPerDelay: TimeSpan.Zero)
                .RunAsync(
                    (_, _) =>
                    {
                        confirmations++;
                        return Task.FromResult(true);
                    },
                    default);

        Assert.Equal(
            CoolingPadLightingCadenceValidationFailure.CadenceNotConfirmed,
            result.Failure);
        Assert.False(result.CadenceTimingConfirmed);
        Assert.False(result.Success);
        Assert.True(result.BrightnessRestoreAcknowledged);
        Assert.Equal(0, confirmations);
        Assert.Equal(5, session.Operations.Count);
    }

    [Fact]
    public async Task CadenceRunner_SubmissionFailureNeverRetriesOrRestores()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x7F]));
        session.SubmitResults.Enqueue(Success([]));
        session.SubmitResults.Enqueue(
            new(default, HidSessionFailureKind.NativeIoFailure));

        CoolingPadLightingCadenceValidationResult result =
            await CreateCadenceRunner(session).RunAsync(
                (_, _) => throw new InvalidOperationException(),
                default);

        Assert.Equal(
            CoolingPadLightingCadenceValidationFailure
                .FrameSubmissionIndeterminate,
            result.Failure);
        Assert.Equal(1, result.SubmittedFrameCount);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(3, session.Operations.Count);
        Assert.DoesNotContain(
            session.Operations,
            operation => operation.Command.CommandId == 0x04);
    }

    [Fact]
    public async Task CadenceRunner_CancellationBetweenFramesRestoresOnce()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x7F]));
        session.SubmitResults.Enqueue(Success([]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x7F]));
        using var cancellation = new CancellationTokenSource();
        CoolingPadLightingCadenceValidationRunner runner = CreateCadenceRunner(
            session,
            (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            });

        CoolingPadLightingCadenceValidationResult result = await runner.RunAsync(
            (_, _) => throw new InvalidOperationException(),
            cancellation.Token);

        Assert.Equal(
            CoolingPadLightingCadenceValidationFailure.Canceled,
            result.Failure);
        Assert.True(result.BrightnessRestoreAcknowledged);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(3, session.Operations.Count);
        Assert.Equal(0x04, session.Operations[^1].Command.CommandId);
    }

    [Fact]
    public async Task CadenceRunner_ConfirmationFailureStillRestoresOnce()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x7F]));
        session.SubmitResults.Enqueue(Success([]));
        session.SubmitResults.Enqueue(Success([]));
        session.SubmitResults.Enqueue(Success([]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x7F]));

        CoolingPadLightingCadenceValidationResult result =
            await CreateCadenceRunner(session).RunAsync(
                (_, _) => throw new IOException("Operator input unavailable."),
                default);

        Assert.Equal(
            CoolingPadLightingCadenceValidationFailure.UnexpectedFailure,
            result.Failure);
        Assert.True(result.BrightnessRestoreAcknowledged);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(5, session.Operations.Count);
        Assert.Equal(0x04, session.Operations[^1].Command.CommandId);
    }

    [Fact]
    public async Task CadenceCommand_RequiresExactTargetAndKeepsAdmissionClosed()
    {
        int calls = 0;
        var output = new StringWriter();
        var command = new CoolingPadLightingCadenceValidationCommand(
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new CoolingPadLightingCadenceValidationResult(
                    0x7F,
                    RequestedFrameCount: 200,
                    SubmittedFrameCount: 200,
                    MinimumFrameIntervalMilliseconds: 50,
                    ObservedAverageFramesPerSecond: 20,
                    CadenceTimingConfirmed: true,
                    CadencePhysicallyConfirmed: true,
                    BrightnessRestoreAttempted: true,
                    BrightnessRestoreAcknowledged: true,
                    BrightnessRestorePhysicallyConfirmed: true,
                    ManualPresetRestoreRequired: true,
                    ManualRecoveryRequired: false,
                    CoolingPadLightingCadenceValidationFailure.None));
            },
            new StringReader(
                "CADENCE-STABLE-NO-SLEEP-CONFIRMED\n" +
                "BRIGHTNESS-RESTORED-CONFIRMED\n"),
            output);

        Assert.Equal(2, await command.RunAsync([], default));
        Assert.Equal(0, calls);
        Assert.Equal(0, await command.RunAsync(
            ["--confirm-target", CoolingPadLightingCadenceValidationCommand.Confirmation],
            default));

        string json = output.ToString()[output.ToString().IndexOf('{')..];
        json = json[..(json.LastIndexOf('}') + 1)];
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(20, document.RootElement
            .GetProperty("requestedFramesPerSecond").GetInt32());
        Assert.False(document.RootElement
            .GetProperty("frameCadenceValidated").GetBoolean());
        Assert.False(document.RootElement
            .GetProperty("productionAdmitted").GetBoolean());
        Assert.False(document.RootElement
            .GetProperty("priorEffectHandbackValidated").GetBoolean());
    }

    [Fact]
    public async Task HandbackRunner_ConfirmsPriorPresetWhileHandleIsOpen()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x80]));
        session.SubmitResults.Enqueue(Success([]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x80]));
        var stages = new List<CoolingPadLightingHandbackValidationStage>();

        CoolingPadLightingHandbackValidationResult result =
            await CreateHandbackRunner(session).RunAsync(
                (stage, _) =>
                {
                    stages.Add(stage);
                    if (stage == CoolingPadLightingHandbackValidationStage
                        .PriorPresetResumedWithHandleOpen)
                    {
                        Assert.Equal(0, session.DisposeCount);
                    }
                    return Task.FromResult(true);
                },
                default);

        Assert.True(result.Success);
        Assert.True(result.HandleDisposalConfirmedBeforeHandbackCheck);
        Assert.True(result.PriorPresetResumedWithHandleOpen);
        Assert.True(result.PriorPresetResumedAutomatically);
        Assert.False(result.ManualPresetRecoveryAttempted);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(
            [
                CoolingPadLightingHandbackValidationStage.BaselinePreset,
                CoolingPadLightingHandbackValidationStage.UniformFrame,
                CoolingPadLightingHandbackValidationStage.BrightnessRestored,
                CoolingPadLightingHandbackValidationStage
                    .PriorPresetResumedWithHandleOpen,
            ],
            stages);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(
            [
                (OperationKind.Execute, (byte)0x84, "010500", (byte)1),
                (OperationKind.Submit, (byte)0x03,
                    "0000000011" + string.Concat(
                        Enumerable.Repeat("123456", 18)), (byte)2),
                (OperationKind.Execute, (byte)0x04, "010080", (byte)3),
            ],
            session.Operations.Select(operation => (
                operation.Kind,
                operation.Command.CommandId,
                Convert.ToHexString(operation.Command.Arguments.Span),
                operation.TransactionId)).ToArray());
    }

    [Fact]
    public async Task HandbackRunner_DistinguishesHandbackThatRequiresHandleClose()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x80]));
        session.SubmitResults.Enqueue(Success([]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x80]));
        var stages = new List<CoolingPadLightingHandbackValidationStage>();

        CoolingPadLightingHandbackValidationResult result =
            await CreateHandbackRunner(session).RunAsync(
                (stage, _) =>
                {
                    stages.Add(stage);
                    if (stage == CoolingPadLightingHandbackValidationStage
                        .PriorPresetResumedWithHandleOpen)
                    {
                        Assert.Equal(0, session.DisposeCount);
                        return Task.FromResult(false);
                    }
                    if (stage == CoolingPadLightingHandbackValidationStage
                        .PriorPresetResumed)
                    {
                        Assert.Equal(1, session.DisposeCount);
                    }
                    return Task.FromResult(true);
                },
                default);

        Assert.True(result.Success);
        Assert.False(result.PriorPresetResumedWithHandleOpen);
        Assert.True(result.PriorPresetResumedAutomatically);
        Assert.Contains(
            CoolingPadLightingHandbackValidationStage
                .PriorPresetResumedWithHandleOpen,
            stages);
        Assert.Contains(
            CoolingPadLightingHandbackValidationStage.PriorPresetResumed,
            stages);
    }

    [Fact]
    public async Task HandbackRunner_DeclinedBaselinePerformsNoHardwareOperation()
    {
        var session = new ScriptedSession();

        CoolingPadLightingHandbackValidationResult result =
            await CreateHandbackRunner(session).RunAsync(
                (stage, _) => Task.FromResult(
                    stage != CoolingPadLightingHandbackValidationStage.BaselinePreset),
                default);

        Assert.Equal(
            CoolingPadLightingHandbackValidationFailure
                .BaselinePresetNotConfirmed,
            result.Failure);
        Assert.Empty(session.Operations);
        Assert.Equal(0, session.DisposeCount);
        Assert.False(result.FrameSubmissionAttempted);
    }

    [Fact]
    public async Task HandbackRunner_NegativeAutomaticResultRequiresManualPresetRecovery()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x7F]));
        session.SubmitResults.Enqueue(Success([]));
        session.ExecuteResults.Enqueue(Success([0x01, 0x00, 0x7F]));
        var stages = new List<CoolingPadLightingHandbackValidationStage>();

        CoolingPadLightingHandbackValidationResult result =
            await CreateHandbackRunner(session).RunAsync(
                (stage, _) =>
                {
                    stages.Add(stage);
                    return Task.FromResult(
                        stage is not (
                            CoolingPadLightingHandbackValidationStage
                                .PriorPresetResumedWithHandleOpen
                            or CoolingPadLightingHandbackValidationStage
                                .PriorPresetResumed));
                },
                default);

        Assert.False(result.Success);
        Assert.Equal(
            CoolingPadLightingHandbackValidationFailure.PriorPresetDidNotResume,
            result.Failure);
        Assert.False(result.PriorPresetResumedAutomatically);
        Assert.True(result.ManualPresetRecoveryAttempted);
        Assert.True(result.ManualPresetRecoveryConfirmed);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(
            CoolingPadLightingHandbackValidationStage.ManualPresetRestored,
            stages[^1]);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task HandbackRunner_IndeterminateFrameNeverWritesAgainAndPromptsRecovery()
    {
        var session = new ScriptedSession();
        session.ExecuteResults.Enqueue(Success([0x01, 0x05, 0x7F]));
        session.SubmitResults.Enqueue(
            new(default, HidSessionFailureKind.NativeIoFailure));
        var stages = new List<CoolingPadLightingHandbackValidationStage>();

        CoolingPadLightingHandbackValidationResult result =
            await CreateHandbackRunner(session).RunAsync(
                (stage, _) =>
                {
                    stages.Add(stage);
                    return Task.FromResult(true);
                },
                default);

        Assert.Equal(
            CoolingPadLightingHandbackValidationFailure
                .FrameSubmissionIndeterminate,
            result.Failure);
        Assert.True(result.ManualPresetRecoveryConfirmed);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(2, session.Operations.Count);
        Assert.DoesNotContain(
            session.Operations,
            operation => operation.Command.CommandId == 0x04);
        Assert.Equal(
            [
                CoolingPadLightingHandbackValidationStage.BaselinePreset,
                CoolingPadLightingHandbackValidationStage.ManualPresetRestored,
            ],
            stages);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task HandbackCommand_EmitsOnlyObservedAutomaticHandback()
    {
        var output = new StringWriter();
        var input = new StringReader(string.Join(
            Environment.NewLine,
            [
                "BASELINE-PRESET-CONFIRMED",
                "UNIFORM-123456-CONFIRMED",
                "BRIGHTNESS-RESTORED-CONFIRMED",
                "PRIOR-PRESET-RESUMED-WITH-HANDLE-OPEN",
            ]));
        var command = new CoolingPadLightingHandbackValidationCommand(
            async (confirm, token) =>
            {
                foreach (CoolingPadLightingHandbackValidationStage stage in
                    new[]
                    {
                        CoolingPadLightingHandbackValidationStage.BaselinePreset,
                        CoolingPadLightingHandbackValidationStage.UniformFrame,
                        CoolingPadLightingHandbackValidationStage.BrightnessRestored,
                        CoolingPadLightingHandbackValidationStage
                            .PriorPresetResumedWithHandleOpen,
                    })
                {
                    Assert.True(await confirm(stage, token));
                }

                return new CoolingPadLightingHandbackValidationResult(
                    0x7F,
                    50,
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
            },
            input,
            output);

        int exitCode = await command.RunAsync(
            ["--confirm-target", CoolingPadLightingHandbackValidationCommand.Confirmation],
            default);

        Assert.Equal(0, exitCode);
        string json = output.ToString()[output.ToString().IndexOf('{')..];
        json = json[..(json.LastIndexOf('}') + 1)];
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement
            .GetProperty("priorEffectHandbackValidated").GetBoolean());
        Assert.False(document.RootElement
            .GetProperty("priorEffectHandbackRequiresHandleClose").GetBoolean());
        Assert.False(document.RootElement
            .GetProperty("productionAdmitted").GetBoolean());
        Assert.False(document.RootElement
            .GetProperty("priorEffectReadbackAvailable").GetBoolean());
    }

    private static CoolingPadLightingLifecycleValidationRunner CreateRunner(
        ScriptedSession session) => new(
        static () => new ValidationRunContext(),
        new ActorFactory(new ValidationActor(
            session,
            new UnsupportedBatteryCommands(),
            new UnsupportedTemporaryCommands())),
        TimeProvider.System,
        new PendingDisposalRegistry());

    private static CoolingPadLightingCadenceValidationRunner CreateCadenceRunner(
        ScriptedSession session,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? elapsedPerDelay = null)
    {
        var timeProvider = new ManualTimeProvider();
        return new CoolingPadLightingCadenceValidationRunner(
            static () => new ValidationRunContext(),
            new ActorFactory(new ValidationActor(
                session,
                new UnsupportedBatteryCommands(),
                new UnsupportedTemporaryCommands())),
            frameCount: 3,
            frameInterval: TimeSpan.FromMilliseconds(1),
            timeProvider,
            async (duration, token) =>
            {
                if (delay is not null)
                {
                    await delay(duration, token);
                }
                timeProvider.Advance(elapsedPerDelay ?? duration);
            },
            pendingDisposals: new PendingDisposalRegistry());
    }

    private static CoolingPadLightingHandbackValidationRunner CreateHandbackRunner(
        ScriptedSession session) => new(
        static () => new ValidationRunContext(),
        new ActorFactory(new ValidationActor(
            session,
            new UnsupportedBatteryCommands(),
            new UnsupportedTemporaryCommands())),
        TimeProvider.System,
        new PendingDisposalRegistry());

    private static HidSessionResult<ReadOnlyMemory<byte>> Success(byte[] payload) =>
        new(payload, null);

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context) => ValueTask.FromResult(
            new HidSessionResult<ValidationActor>(actor, null));
    }

    private enum OperationKind
    {
        Execute,
        Submit,
    }

    private sealed record Operation(
        OperationKind Kind,
        RazerCommandSpec Command,
        byte TransactionId);

    private sealed class ScriptedSession
        : IRazerCommandSession,
          IRazerSetFeatureCommandSession
    {
        internal Queue<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteResults { get; } = [];
        internal Queue<HidSessionResult<ReadOnlyMemory<byte>>> SubmitResults { get; } = [];
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
            Operations.Add(new(OperationKind.Execute, command, transaction));
            return Task.FromResult(ExecuteResults.Dequeue());
        }

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte transaction = assignTransactionId().TransactionId;
            Operations.Add(new(OperationKind.Submit, command, transaction));
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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp = 1;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => timestamp;

        internal void Advance(TimeSpan duration) => timestamp += checked(
            (long)(duration.TotalSeconds * TimestampFrequency));
    }
}
