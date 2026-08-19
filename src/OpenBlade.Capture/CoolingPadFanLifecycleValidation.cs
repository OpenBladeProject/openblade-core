using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal enum CoolingPadFanValidationStage
{
    Minimum500,
    Maximum3200,
    Update2200,
    Release,
}

internal enum CoolingPadFanLifecycleValidationFailure
{
    None,
    Canceled,
    OpenFailed,
    MinimumApplyKnownNotApplied,
    MinimumApplyIndeterminate,
    MinimumPhysicalNotConfirmed,
    MaximumUpdateKnownNotApplied,
    MaximumUpdateIndeterminate,
    MaximumPhysicalNotConfirmed,
    Update2200KnownNotApplied,
    Update2200Indeterminate,
    Update2200PhysicalNotConfirmed,
    ReleaseKnownNotApplied,
    ReleaseIndeterminate,
    ReleasePhysicalNotConfirmed,
    UnexpectedFailure,
}

internal sealed record CoolingPadFanLifecycleValidationResult(
    bool MinimumApplyAcknowledged,
    bool MinimumPhysicalConfirmed,
    bool MaximumUpdateAcknowledged,
    bool MaximumPhysicalConfirmed,
    bool Update2200Acknowledged,
    bool Update2200PhysicalConfirmed,
    bool ReleaseAcknowledged,
    bool ReleasePhysicalConfirmed,
    bool ManualSynapseRecoveryRequired,
    bool ManualPowerCycleMayBeRequired,
    CoolingPadFanLifecycleValidationFailure Failure)
{
    internal bool Success => Failure == CoolingPadFanLifecycleValidationFailure.None
        && MinimumApplyAcknowledged
        && MinimumPhysicalConfirmed
        && MaximumUpdateAcknowledged
        && MaximumPhysicalConfirmed
        && Update2200Acknowledged
        && Update2200PhysicalConfirmed
        && ReleaseAcknowledged
        && ReleasePhysicalConfirmed;
}

internal sealed class CoolingPadFanLifecycleValidationRunner
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int used;

    internal CoolingPadFanLifecycleValidationRunner(
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

    internal static CoolingPadFanLifecycleValidationRunner CreateWindows() => new(
        static () => new ValidationRunContext(),
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400CoolingPadFanGetterActorFactory());

    internal async Task<CoolingPadFanLifecycleValidationResult> RunAsync(
        Func<CoolingPadFanValidationStage, CancellationToken, Task<bool>> confirm,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        if (Interlocked.Exchange(ref used, 1) != 0)
        {
            return Empty(CoolingPadFanLifecycleValidationFailure.UnexpectedFailure);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Empty(CoolingPadFanLifecycleValidationFailure.Canceled);
        }

        ValidationRunContext context;
        try
        {
            context = createContext();
        }
        catch
        {
            return Empty(CoolingPadFanLifecycleValidationFailure.UnexpectedFailure);
        }

        HidSessionResult<ValidationActor> opened =
            await actorFactory.OpenAsync(context).ConfigureAwait(false);
        if (opened.Failure is not null || opened.Value is null)
        {
            return Empty(cancellationToken.IsCancellationRequested
                ? CoolingPadFanLifecycleValidationFailure.Canceled
                : CoolingPadFanLifecycleValidationFailure.OpenFailed);
        }

        ValidationActor actor = opened.Value;
        using var operationGate = new SemaphoreSlim(1, 1);
        bool poisoned = false;
        var executor = new CoolingPadHostControlExecutor(
            actor.Session,
            context,
            operationGate,
            ExchangeTimeout,
            () => poisoned = true);
        var state = new MutableResult();
        try
        {
            if (!await ExecuteAsync(
                    () => executor.AcquireAsync(
                        new CoolingPadFanTarget(500),
                        cancellationToken),
                    known => known
                        ? CoolingPadFanLifecycleValidationFailure
                            .MinimumApplyKnownNotApplied
                        : CoolingPadFanLifecycleValidationFailure
                            .MinimumApplyIndeterminate,
                    state).ConfigureAwait(false))
            {
                return state.ToResult(poisoned);
            }
            state.MinimumApplyAcknowledged = true;
            if (!await ConfirmAsync(
                    confirm,
                    CoolingPadFanValidationStage.Minimum500,
                    cancellationToken).ConfigureAwait(false))
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadFanLifecycleValidationFailure.Canceled
                    : CoolingPadFanLifecycleValidationFailure
                        .MinimumPhysicalNotConfirmed;
                return state.ToResult(poisoned);
            }
            state.MinimumPhysicalConfirmed = true;

            if (!await ExecuteAsync(
                    () => executor.UpdateAsync(
                        new CoolingPadFanTarget(3_200),
                        cancellationToken),
                    known => known
                        ? CoolingPadFanLifecycleValidationFailure
                            .MaximumUpdateKnownNotApplied
                        : CoolingPadFanLifecycleValidationFailure
                            .MaximumUpdateIndeterminate,
                    state).ConfigureAwait(false))
            {
                return state.ToResult(poisoned);
            }
            state.MaximumUpdateAcknowledged = true;
            if (!await ConfirmAsync(
                    confirm,
                    CoolingPadFanValidationStage.Maximum3200,
                    cancellationToken).ConfigureAwait(false))
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadFanLifecycleValidationFailure.Canceled
                    : CoolingPadFanLifecycleValidationFailure
                        .MaximumPhysicalNotConfirmed;
                return state.ToResult(poisoned);
            }
            state.MaximumPhysicalConfirmed = true;

            if (!await ExecuteAsync(
                    () => executor.UpdateAsync(
                        new CoolingPadFanTarget(2_200),
                        cancellationToken),
                    known => known
                        ? CoolingPadFanLifecycleValidationFailure
                            .Update2200KnownNotApplied
                        : CoolingPadFanLifecycleValidationFailure
                            .Update2200Indeterminate,
                    state).ConfigureAwait(false))
            {
                return state.ToResult(poisoned);
            }
            state.Update2200Acknowledged = true;
            if (!await ConfirmAsync(
                    confirm,
                    CoolingPadFanValidationStage.Update2200,
                    cancellationToken).ConfigureAwait(false))
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadFanLifecycleValidationFailure.Canceled
                    : CoolingPadFanLifecycleValidationFailure
                        .Update2200PhysicalNotConfirmed;
                return state.ToResult(poisoned);
            }
            state.Update2200PhysicalConfirmed = true;

            if (!await ExecuteAsync(
                    () => executor.ReleaseAsync(cancellationToken),
                    known => known
                        ? CoolingPadFanLifecycleValidationFailure.ReleaseKnownNotApplied
                        : CoolingPadFanLifecycleValidationFailure.ReleaseIndeterminate,
                    state).ConfigureAwait(false))
            {
                return state.ToResult(poisoned);
            }
            state.ReleaseAcknowledged = true;
            if (!await ConfirmAsync(
                    confirm,
                    CoolingPadFanValidationStage.Release,
                    cancellationToken).ConfigureAwait(false))
            {
                state.Failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadFanLifecycleValidationFailure.Canceled
                    : CoolingPadFanLifecycleValidationFailure.ReleasePhysicalNotConfirmed;
                return state.ToResult(poisoned);
            }
            state.ReleasePhysicalConfirmed = true;
            state.ManualSynapseRecoveryRequired = false;
            state.ManualPowerCycleMayBeRequired = false;
            return state.ToResult(poisoned);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Failure = CoolingPadFanLifecycleValidationFailure.Canceled;
            return state.ToResult(poisoned);
        }
        catch
        {
            state.Failure = CoolingPadFanLifecycleValidationFailure.UnexpectedFailure;
            return state.ToResult(poisoned);
        }
        finally
        {
            await BatteryValidationRun.DisposeBoundedAsync(
                actor,
                timeProvider,
                CleanupTimeout,
                pendingDisposals).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ExecuteAsync(
        Func<Task> execute,
        Func<bool, CoolingPadFanLifecycleValidationFailure> failure,
        MutableResult state)
    {
        try
        {
            await execute().ConfigureAwait(false);
            return true;
        }
        catch (BladeWriteException exception)
        {
            bool knownNotApplied =
                exception.SubmissionState == WriteSubmissionState.KnownNotApplied;
            state.Failure = failure(knownNotApplied);
            state.ManualPowerCycleMayBeRequired |= !knownNotApplied;
            return false;
        }
    }

    private static async Task<bool> ConfirmAsync(
        Func<CoolingPadFanValidationStage, CancellationToken, Task<bool>> confirm,
        CoolingPadFanValidationStage stage,
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

    private static CoolingPadFanLifecycleValidationResult Empty(
        CoolingPadFanLifecycleValidationFailure failure) => new(
        MinimumApplyAcknowledged: false,
        MinimumPhysicalConfirmed: false,
        MaximumUpdateAcknowledged: false,
        MaximumPhysicalConfirmed: false,
        Update2200Acknowledged: false,
        Update2200PhysicalConfirmed: false,
        ReleaseAcknowledged: false,
        ReleasePhysicalConfirmed: false,
        ManualSynapseRecoveryRequired: false,
        ManualPowerCycleMayBeRequired: false,
        failure);

    private sealed class MutableResult
    {
        internal bool MinimumApplyAcknowledged { get; set; }
        internal bool MinimumPhysicalConfirmed { get; set; }
        internal bool MaximumUpdateAcknowledged { get; set; }
        internal bool MaximumPhysicalConfirmed { get; set; }
        internal bool Update2200Acknowledged { get; set; }
        internal bool Update2200PhysicalConfirmed { get; set; }
        internal bool ReleaseAcknowledged { get; set; }
        internal bool ReleasePhysicalConfirmed { get; set; }
        internal bool ManualSynapseRecoveryRequired { get; set; }
        internal bool ManualPowerCycleMayBeRequired { get; set; }
        internal CoolingPadFanLifecycleValidationFailure Failure { get; set; }

        internal CoolingPadFanLifecycleValidationResult ToResult(bool poisoned) => new(
            MinimumApplyAcknowledged,
            MinimumPhysicalConfirmed,
            MaximumUpdateAcknowledged,
            MaximumPhysicalConfirmed,
            Update2200Acknowledged,
            Update2200PhysicalConfirmed,
            ReleaseAcknowledged,
            ReleasePhysicalConfirmed,
            ManualSynapseRecoveryRequired
                || !ReleasePhysicalConfirmed && MinimumApplyAcknowledged,
            ManualPowerCycleMayBeRequired || poisoned,
            Failure);
    }
}

internal sealed class CoolingPadFanLifecycleValidationCommand(
    Func<Func<CoolingPadFanValidationStage, CancellationToken, Task<bool>>,
        CancellationToken,
        Task<CoolingPadFanLifecycleValidationResult>> run,
    TextReader input,
    TextWriter output)
{
    internal const string Confirmation =
        "RZ09-0581:4.00:1532:0F43:FAN-500:3200:2200:RELEASE:ONCE";

    internal async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args is not ["--confirm-target", Confirmation])
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture validate-cooling-pad-fan-lifecycle " +
                "--confirm-target " + Confirmation).ConfigureAwait(false);
            return 2;
        }

        CoolingPadFanLifecycleValidationResult result = await run(
                ConfirmAsync,
                cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync(JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                device = new
                {
                    model = TargetBladeDevice.Model,
                    vendorId = "1532",
                    productId = "0F43",
                    revision = "0200",
                    biosVersion = "4.00",
                },
                readOnly = false,
                productionAdmitted = false,
                activeModeReadbackConfirmed = false,
                physicalRpmReadbackConfirmed = false,
                retryAllowed = false,
                result,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            })).ConfigureAwait(false);
        if (result.ManualSynapseRecoveryRequired)
        {
            await output.WriteLineAsync(
                "Do not retry. Restore the Auto UI state through Synapse; if the pad is flashing red or asleep, disconnect USB and 12 V power before reconnecting.")
                .ConfigureAwait(false);
        }
        return result.Success ? 0 : 2;
    }

    private async Task<bool> ConfirmAsync(
        CoolingPadFanValidationStage stage,
        CancellationToken cancellationToken)
    {
        string expected = stage switch
        {
            CoolingPadFanValidationStage.Minimum500 => "FAN-500-CONFIRMED",
            CoolingPadFanValidationStage.Maximum3200 => "FAN-3200-CONFIRMED",
            CoolingPadFanValidationStage.Update2200 => "FAN-2200-CONFIRMED",
            CoolingPadFanValidationStage.Release => "FAN-RELEASE-CONFIRMED",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        await output.WriteLineAsync(
            $"Type {expected} after physical confirmation.").ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        return response?.Equals(expected, StringComparison.Ordinal) == true;
    }
}
