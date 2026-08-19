using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal enum CoolingPadLightingHandbackValidationStage
{
    BaselinePreset,
    UniformFrame,
    BrightnessRestored,
    PriorPresetResumedWithHandleOpen,
    PriorPresetResumed,
    ManualPresetRestored,
}

internal enum CoolingPadLightingHandbackValidationFailure
{
    None,
    Canceled,
    BaselinePresetNotConfirmed,
    OpenFailed,
    SetOnlyUnavailable,
    TransactionBudgetUnavailable,
    BrightnessQueryFailed,
    BaselineBrightnessZero,
    FrameSubmissionIndeterminate,
    UniformFrameNotConfirmed,
    BrightnessRestoreIndeterminate,
    BrightnessRestoreNotConfirmed,
    HandleDisposalUnconfirmed,
    PriorPresetDidNotResume,
    ManualPresetRecoveryNotConfirmed,
    UnexpectedFailure,
}

internal sealed record CoolingPadLightingHandbackValidationResult(
    byte? BaselineBrightnessRaw,
    byte? BaselineBrightnessPercent,
    bool BaselinePresetPhysicallyConfirmed,
    bool FrameSubmissionAttempted,
    bool FrameSubmissionCompleted,
    bool UniformFramePhysicallyConfirmed,
    bool BrightnessRestoreAttempted,
    bool BrightnessRestoreAcknowledged,
    bool BrightnessRestorePhysicallyConfirmed,
    bool PriorPresetResumedWithHandleOpen,
    bool HandleDisposalConfirmedBeforeHandbackCheck,
    bool PriorPresetResumedAutomatically,
    bool ManualPresetRecoveryAttempted,
    bool ManualPresetRecoveryConfirmed,
    bool ManualRecoveryRequired,
    CoolingPadLightingHandbackValidationFailure Failure)
{
    internal bool Success => Failure == CoolingPadLightingHandbackValidationFailure.None
        && BaselineBrightnessRaw is > 0
        && BaselinePresetPhysicallyConfirmed
        && FrameSubmissionCompleted
        && UniformFramePhysicallyConfirmed
        && BrightnessRestoreAcknowledged
        && BrightnessRestorePhysicallyConfirmed
        && HandleDisposalConfirmedBeforeHandbackCheck
        && PriorPresetResumedAutomatically
        && !ManualRecoveryRequired;
}

internal sealed class CoolingPadLightingHandbackValidationRunner
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int used;

    internal CoolingPadLightingHandbackValidationRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider? timeProvider = null,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.createContext = createContext
            ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory
            ?? throw new ArgumentNullException(nameof(actorFactory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    internal static CoolingPadLightingHandbackValidationRunner CreateWindows() => new(
        static () => new ValidationRunContext(),
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400CoolingPadFanGetterActorFactory());

    internal async Task<CoolingPadLightingHandbackValidationResult> RunAsync(
        Func<CoolingPadLightingHandbackValidationStage, CancellationToken, Task<bool>>
            confirm,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        if (Interlocked.Exchange(ref used, 1) != 0)
        {
            return Empty(CoolingPadLightingHandbackValidationFailure.UnexpectedFailure);
        }

        var state = new MutableResult();
        if (!await ConfirmAsync(
                confirm,
                CoolingPadLightingHandbackValidationStage.BaselinePreset,
                cancellationToken).ConfigureAwait(false))
        {
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadLightingHandbackValidationFailure.Canceled
                : CoolingPadLightingHandbackValidationFailure
                    .BaselinePresetNotConfirmed;
            return state.ToResult();
        }
        state.BaselinePresetPhysicallyConfirmed = true;

        ValidationRunContext context;
        try
        {
            context = createContext();
        }
        catch
        {
            state.Failure = CoolingPadLightingHandbackValidationFailure.UnexpectedFailure;
            return state.ToResult();
        }

        HidSessionResult<ValidationActor> opened;
        try
        {
            opened = await actorFactory.OpenAsync(context).ConfigureAwait(false);
        }
        catch
        {
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadLightingHandbackValidationFailure.Canceled
                : CoolingPadLightingHandbackValidationFailure.OpenFailed;
            return state.ToResult();
        }
        if (opened.Failure is not null || opened.Value is null)
        {
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadLightingHandbackValidationFailure.Canceled
                : CoolingPadLightingHandbackValidationFailure.OpenFailed;
            return state.ToResult();
        }

        ValidationActor actor = opened.Value;
        await RunWithActorAsync(
                actor,
                context,
                confirm,
                state,
                cancellationToken)
            .ConfigureAwait(false);

        if (state.FrameSubmissionCompleted
            && state.BrightnessRestoreAcknowledged
            && !cancellationToken.IsCancellationRequested)
        {
            state.PriorPresetResumedWithHandleOpen = await ConfirmAsync(
                    confirm,
                    CoolingPadLightingHandbackValidationStage
                        .PriorPresetResumedWithHandleOpen,
                    cancellationToken)
                .ConfigureAwait(false);
            state.PriorPresetResumedAutomatically =
                state.PriorPresetResumedWithHandleOpen;
        }

        state.HandleDisposalConfirmedBeforeHandbackCheck =
            await DisposeAndConfirmAsync(actor).ConfigureAwait(false);
        if (!state.HandleDisposalConfirmedBeforeHandbackCheck)
        {
            state.ManualRecoveryRequired |= state.FrameSubmissionAttempted;
            state.Failure = CoolingPadLightingHandbackValidationFailure
                .HandleDisposalUnconfirmed;
            return state.ToResult();
        }

        if (!state.FrameSubmissionAttempted)
        {
            return state.ToResult();
        }

        if (!state.PriorPresetResumedWithHandleOpen
            && state.FrameSubmissionCompleted
            && state.BrightnessRestoreAcknowledged
            && !cancellationToken.IsCancellationRequested)
        {
            state.PriorPresetResumedAutomatically = await ConfirmAsync(
                    confirm,
                    CoolingPadLightingHandbackValidationStage.PriorPresetResumed,
                    cancellationToken)
                .ConfigureAwait(false);
            if (state.PriorPresetResumedAutomatically)
            {
                state.ManualRecoveryRequired = false;
                return state.ToResult();
            }
        }
        else if (state.PriorPresetResumedWithHandleOpen)
        {
            state.ManualRecoveryRequired = false;
            return state.ToResult();
        }

        state.ManualPresetRecoveryAttempted = true;
        state.ManualPresetRecoveryConfirmed = !cancellationToken.IsCancellationRequested
            && await ConfirmAsync(
                    confirm,
                    CoolingPadLightingHandbackValidationStage.ManualPresetRestored,
                    cancellationToken)
                .ConfigureAwait(false);
        state.ManualRecoveryRequired = !state.ManualPresetRecoveryConfirmed;
        if (state.Failure == CoolingPadLightingHandbackValidationFailure.None)
        {
            state.Failure = state.ManualPresetRecoveryConfirmed
                ? CoolingPadLightingHandbackValidationFailure.PriorPresetDidNotResume
                : CoolingPadLightingHandbackValidationFailure
                    .ManualPresetRecoveryNotConfirmed;
        }
        return state.ToResult();
    }

    private async Task RunWithActorAsync(
        ValidationActor actor,
        ValidationRunContext context,
        Func<CoolingPadLightingHandbackValidationStage, CancellationToken, Task<bool>>
            confirm,
        MutableResult state,
        CancellationToken cancellationToken)
    {
        if (actor.Session is not IRazerSetFeatureCommandSession submitter)
        {
            state.Failure = CoolingPadLightingHandbackValidationFailure
                .SetOnlyUnavailable;
            return;
        }
        if (!context.TryAllocateTransaction(out byte queryTransaction)
            || !context.TryAllocateTransaction(out byte frameTransaction)
            || !context.TryAllocateTransaction(out byte restoreTransaction))
        {
            state.Failure = CoolingPadLightingHandbackValidationFailure
                .TransactionBudgetUnavailable;
            return;
        }

        HidSessionResult<ReadOnlyMemory<byte>> query;
        try
        {
            query = await actor.Session.ExecuteAsync(
                    CoolingPadLightingProtocol.CreateBrightnessQueryCommand(),
                    () => TransactionIdAssignment.Valid(queryTransaction),
                    ExchangeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadLightingHandbackValidationFailure.Canceled
                : CoolingPadLightingHandbackValidationFailure.BrightnessQueryFailed;
            return;
        }
        if (query.Failure is not null
            || !CoolingPadLightingProtocol.TryDecodeBrightnessQuery(
                query.Value.Span,
                out CoolingPadBrightnessReading baseline))
        {
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadLightingHandbackValidationFailure.Canceled
                : CoolingPadLightingHandbackValidationFailure.BrightnessQueryFailed;
            return;
        }

        state.BaselineBrightnessRaw = baseline.Raw;
        state.BaselineBrightnessPercent = baseline.Percent;
        if (baseline.Raw == 0)
        {
            state.Failure = CoolingPadLightingHandbackValidationFailure
                .BaselineBrightnessZero;
            return;
        }

        var frame = new CoolingPadLightingFrame(
            Enumerable.Repeat(
                CoolingPadLightingLifecycleValidationRunner.ValidationColor,
                CoolingPadLightingFrame.SlotCount).ToArray());
        state.FrameSubmissionAttempted = true;
        HidSessionResult<ReadOnlyMemory<byte>> frameResult;
        try
        {
            frameResult = await submitter.SubmitAsync(
                    CoolingPadLightingProtocol.CreateFrameSetOnlyCommand(frame),
                    () => TransactionIdAssignment.Valid(frameTransaction),
                    ExchangeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            state.ManualRecoveryRequired = true;
            state.Failure = CoolingPadLightingHandbackValidationFailure
                .FrameSubmissionIndeterminate;
            return;
        }
        if (frameResult.Failure is not null)
        {
            state.ManualRecoveryRequired = true;
            state.Failure = CoolingPadLightingHandbackValidationFailure
                .FrameSubmissionIndeterminate;
            return;
        }
        state.FrameSubmissionCompleted = true;

        bool uniformConfirmed = await ConfirmAsync(
                confirm,
                CoolingPadLightingHandbackValidationStage.UniformFrame,
                cancellationToken)
            .ConfigureAwait(false);
        state.UniformFramePhysicallyConfirmed = uniformConfirmed;
        if (!uniformConfirmed)
        {
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadLightingHandbackValidationFailure.Canceled
                : CoolingPadLightingHandbackValidationFailure
                    .UniformFrameNotConfirmed;
        }

        state.BrightnessRestoreAttempted = true;
        try
        {
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            HidSessionResult<ReadOnlyMemory<byte>> restore =
                await actor.Session.ExecuteAsync(
                        CoolingPadLightingProtocol.CreateBrightnessRawSetCommand(
                            baseline.Raw),
                        () => TransactionIdAssignment.Valid(restoreTransaction),
                        ExchangeTimeout,
                        cleanup.Token)
                    .ConfigureAwait(false);
            if (restore.Failure is not null)
            {
                state.ManualRecoveryRequired = true;
                state.Failure = CoolingPadLightingHandbackValidationFailure
                    .BrightnessRestoreIndeterminate;
                return;
            }
            state.BrightnessRestoreAcknowledged = true;
        }
        catch
        {
            state.ManualRecoveryRequired = true;
            state.Failure = CoolingPadLightingHandbackValidationFailure
                .BrightnessRestoreIndeterminate;
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            state.ManualRecoveryRequired = true;
            state.Failure = CoolingPadLightingHandbackValidationFailure.Canceled;
            return;
        }

        state.BrightnessRestorePhysicallyConfirmed = await ConfirmAsync(
                confirm,
                CoolingPadLightingHandbackValidationStage.BrightnessRestored,
                cancellationToken)
            .ConfigureAwait(false);
        if (!state.BrightnessRestorePhysicallyConfirmed
            && state.Failure == CoolingPadLightingHandbackValidationFailure.None)
        {
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadLightingHandbackValidationFailure.Canceled
                : CoolingPadLightingHandbackValidationFailure
                    .BrightnessRestoreNotConfirmed;
        }
    }

    private async Task<bool> DisposeAndConfirmAsync(ValidationActor actor)
    {
        Task disposal;
        try
        {
            disposal = actor.DisposeAsync().AsTask();
        }
        catch
        {
            return false;
        }

        if (!disposal.IsCompleted)
        {
            Task deadline = Task.Delay(CleanupTimeout, timeProvider);
            await Task.WhenAny(disposal, deadline).ConfigureAwait(false);
        }
        if (!disposal.IsCompleted)
        {
            pendingDisposals.Retain(disposal);
            return false;
        }

        try
        {
            await disposal.ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ConfirmAsync(
        Func<CoolingPadLightingHandbackValidationStage, CancellationToken, Task<bool>>
            confirm,
        CoolingPadLightingHandbackValidationStage stage,
        CancellationToken cancellationToken)
    {
        try
        {
            return await confirm(stage, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static CoolingPadLightingHandbackValidationResult Empty(
        CoolingPadLightingHandbackValidationFailure failure) => new(
        BaselineBrightnessRaw: null,
        BaselineBrightnessPercent: null,
        BaselinePresetPhysicallyConfirmed: false,
        FrameSubmissionAttempted: false,
        FrameSubmissionCompleted: false,
        UniformFramePhysicallyConfirmed: false,
        BrightnessRestoreAttempted: false,
        BrightnessRestoreAcknowledged: false,
        BrightnessRestorePhysicallyConfirmed: false,
        PriorPresetResumedWithHandleOpen: false,
        HandleDisposalConfirmedBeforeHandbackCheck: false,
        PriorPresetResumedAutomatically: false,
        ManualPresetRecoveryAttempted: false,
        ManualPresetRecoveryConfirmed: false,
        ManualRecoveryRequired: false,
        failure);

    private sealed class MutableResult
    {
        internal byte? BaselineBrightnessRaw { get; set; }
        internal byte? BaselineBrightnessPercent { get; set; }
        internal bool BaselinePresetPhysicallyConfirmed { get; set; }
        internal bool FrameSubmissionAttempted { get; set; }
        internal bool FrameSubmissionCompleted { get; set; }
        internal bool UniformFramePhysicallyConfirmed { get; set; }
        internal bool BrightnessRestoreAttempted { get; set; }
        internal bool BrightnessRestoreAcknowledged { get; set; }
        internal bool BrightnessRestorePhysicallyConfirmed { get; set; }
        internal bool PriorPresetResumedWithHandleOpen { get; set; }
        internal bool HandleDisposalConfirmedBeforeHandbackCheck { get; set; }
        internal bool PriorPresetResumedAutomatically { get; set; }
        internal bool ManualPresetRecoveryAttempted { get; set; }
        internal bool ManualPresetRecoveryConfirmed { get; set; }
        internal bool ManualRecoveryRequired { get; set; }
        internal CoolingPadLightingHandbackValidationFailure Failure { get; set; }

        internal CoolingPadLightingHandbackValidationResult ToResult() => new(
            BaselineBrightnessRaw,
            BaselineBrightnessPercent,
            BaselinePresetPhysicallyConfirmed,
            FrameSubmissionAttempted,
            FrameSubmissionCompleted,
            UniformFramePhysicallyConfirmed,
            BrightnessRestoreAttempted,
            BrightnessRestoreAcknowledged,
            BrightnessRestorePhysicallyConfirmed,
            PriorPresetResumedWithHandleOpen,
            HandleDisposalConfirmedBeforeHandbackCheck,
            PriorPresetResumedAutomatically,
            ManualPresetRecoveryAttempted,
            ManualPresetRecoveryConfirmed,
            ManualRecoveryRequired,
            Failure);
    }
}

internal sealed class CoolingPadLightingHandbackValidationCommand(
    Func<Func<CoolingPadLightingHandbackValidationStage, CancellationToken, Task<bool>>,
        CancellationToken,
        Task<CoolingPadLightingHandbackValidationResult>> run,
    TextReader input,
    TextWriter output)
{
    internal const string Confirmation =
        "RZ09-0581:4.00:1532:0F43:LIGHTING-HANDBACK:UNIFORM-123456:RESTORE-BRIGHTNESS:ONCE";

    internal async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args is not ["--confirm-target", Confirmation])
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture validate-cooling-pad-lighting-handback " +
                "--confirm-target " + Confirmation).ConfigureAwait(false);
            return 2;
        }

        CoolingPadLightingHandbackValidationResult result = await run(
                ConfirmAsync,
                cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync(JsonSerializer.Serialize(
            new
            {
                schemaVersion = 2,
                device = new
                {
                    model = TargetBladeDevice.Model,
                    vendorId = "1532",
                    productId = "0F43",
                    revision = "0200",
                    biosVersion = "4.00",
                },
                uniformColor = "123456",
                readOnly = false,
                productionAdmitted = false,
                priorEffectReadbackAvailable = false,
                priorEffectHandbackValidated =
                    result.PriorPresetResumedWithHandleOpen,
                priorEffectHandbackRequiresHandleClose =
                    !result.PriorPresetResumedWithHandleOpen
                    && result.PriorPresetResumedAutomatically,
                result,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            })).ConfigureAwait(false);
        return result.Success ? 0 : 2;
    }

    private async Task<bool> ConfirmAsync(
        CoolingPadLightingHandbackValidationStage stage,
        CancellationToken cancellationToken)
    {
        string expected = stage switch
        {
            CoolingPadLightingHandbackValidationStage.BaselinePreset =>
                "BASELINE-PRESET-CONFIRMED",
            CoolingPadLightingHandbackValidationStage.UniformFrame =>
                "UNIFORM-123456-CONFIRMED",
            CoolingPadLightingHandbackValidationStage.BrightnessRestored =>
                "BRIGHTNESS-RESTORED-CONFIRMED",
            CoolingPadLightingHandbackValidationStage
                    .PriorPresetResumedWithHandleOpen =>
                "PRIOR-PRESET-RESUMED-WITH-HANDLE-OPEN",
            CoolingPadLightingHandbackValidationStage.PriorPresetResumed =>
                "PRIOR-PRESET-RESUMED",
            CoolingPadLightingHandbackValidationStage.ManualPresetRestored =>
                "MANUAL-PRESET-RESTORED",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        string instruction = stage switch
        {
            CoolingPadLightingHandbackValidationStage.BaselinePreset =>
                "Select a distinctive pad lighting preset with the physical button, then type",
            CoolingPadLightingHandbackValidationStage.PriorPresetResumed =>
                "After the pad handle closes, type this only if the exact prior preset resumed automatically:",
            CoolingPadLightingHandbackValidationStage
                    .PriorPresetResumedWithHandleOpen =>
                "While OpenBlade still holds the pad handle, type this only if the exact prior preset has already resumed automatically:",
            CoolingPadLightingHandbackValidationStage.ManualPresetRestored =>
                "Restore the prior preset with the physical button or Synapse, then type",
            _ => "Type this after physical confirmation:",
        };
        await output.WriteLineAsync($"{instruction} {expected}").ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        return response?.Equals(expected, StringComparison.Ordinal) == true;
    }
}
