using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal enum CoolingPadFanCadenceValidationStage
{
    BaselineAuto,
    CadenceObserved,
    Release,
}

internal enum CoolingPadFanCadenceValidationFailure
{
    None,
    Canceled,
    BaselineNotConfirmed,
    OpenFailed,
    AcquireKnownNotApplied,
    AcquireIndeterminate,
    UpdateKnownNotApplied,
    UpdateIndeterminate,
    CadenceNotConfirmed,
    ReleaseKnownNotApplied,
    ReleaseIndeterminate,
    ReleaseNotConfirmed,
    UnexpectedFailure,
}

internal sealed record CoolingPadFanCadenceValidationResult(
    bool BaselineAutoPhysicallyConfirmed,
    bool AcquireAcknowledged,
    int RequestedUpdateCount,
    int AcknowledgedUpdateCount,
    bool OneHertzCadencePhysicallyConfirmed,
    bool ReleaseAttempted,
    bool ReleaseAcknowledged,
    bool ReleasePhysicallyConfirmed,
    bool ManualSynapseRecoveryRequired,
    bool ManualPowerCycleMayBeRequired,
    CoolingPadFanCadenceValidationFailure Failure)
{
    internal bool Success => Failure == CoolingPadFanCadenceValidationFailure.None
        && BaselineAutoPhysicallyConfirmed
        && AcquireAcknowledged
        && RequestedUpdateCount == AcknowledgedUpdateCount
        && OneHertzCadencePhysicallyConfirmed
        && ReleaseAcknowledged
        && ReleasePhysicallyConfirmed;
}

internal sealed class CoolingPadFanCadenceValidationRunner
{
    internal static readonly IReadOnlyList<CoolingPadFanTarget> UpdateTargets =
        Array.AsReadOnly(
        [
            new CoolingPadFanTarget(1_000),
            new CoolingPadFanTarget(1_500),
            new CoolingPadFanTarget(2_200),
            new CoolingPadFanTarget(3_200),
            new CoolingPadFanTarget(2_200),
            new CoolingPadFanTarget(500),
            new CoolingPadFanTarget(2_200),
        ]);

    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeProvider timeProvider;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int used;

    internal CoolingPadFanCadenceValidationRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.createContext = createContext
            ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory
            ?? throw new ArgumentNullException(nameof(actorFactory));
        this.delay = delay ?? Task.Delay;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    internal static CoolingPadFanCadenceValidationRunner CreateWindows() => new(
        static () => new ValidationRunContext(),
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400CoolingPadFanGetterActorFactory());

    internal async Task<CoolingPadFanCadenceValidationResult> RunAsync(
        Func<CoolingPadFanCadenceValidationStage, CancellationToken, Task<bool>>
            confirm,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        var state = new MutableResult
        {
            RequestedUpdateCount = UpdateTargets.Count,
        };
        if (Interlocked.Exchange(ref used, 1) != 0)
        {
            state.Failure = CoolingPadFanCadenceValidationFailure.UnexpectedFailure;
            return state.ToResult();
        }
        if (!await ConfirmAsync(
                confirm,
                CoolingPadFanCadenceValidationStage.BaselineAuto,
                cancellationToken).ConfigureAwait(false))
        {
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadFanCadenceValidationFailure.Canceled
                : CoolingPadFanCadenceValidationFailure.BaselineNotConfirmed;
            return state.ToResult();
        }
        state.BaselineAutoPhysicallyConfirmed = true;

        ValidationRunContext context;
        try
        {
            context = createContext();
        }
        catch
        {
            state.Failure = CoolingPadFanCadenceValidationFailure.UnexpectedFailure;
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
                ? CoolingPadFanCadenceValidationFailure.Canceled
                : CoolingPadFanCadenceValidationFailure.OpenFailed;
            return state.ToResult();
        }
        if (opened.Failure is not null || opened.Value is null)
        {
            state.Failure = cancellationToken.IsCancellationRequested
                ? CoolingPadFanCadenceValidationFailure.Canceled
                : CoolingPadFanCadenceValidationFailure.OpenFailed;
            return state.ToResult();
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
        try
        {
            try
            {
                await executor.AcquireAsync(
                        new CoolingPadFanTarget(500),
                        cancellationToken)
                    .ConfigureAwait(false);
                state.AcquireAcknowledged = true;

                foreach (CoolingPadFanTarget target in UpdateTargets)
                {
                    await delay(UpdateInterval, cancellationToken)
                        .ConfigureAwait(false);
                    try
                    {
                        await executor.UpdateAsync(target, cancellationToken)
                            .ConfigureAwait(false);
                        state.AcknowledgedUpdateCount++;
                    }
                    catch (BladeWriteException exception)
                    {
                        bool known = exception.SubmissionState
                            == WriteSubmissionState.KnownNotApplied;
                        state.Failure = known
                            ? CoolingPadFanCadenceValidationFailure
                                .UpdateKnownNotApplied
                            : CoolingPadFanCadenceValidationFailure
                                .UpdateIndeterminate;
                        state.ManualPowerCycleMayBeRequired |= !known;
                        break;
                    }
                }

                if (state.Failure == CoolingPadFanCadenceValidationFailure.None)
                {
                    state.OneHertzCadencePhysicallyConfirmed = await ConfirmAsync(
                            confirm,
                            CoolingPadFanCadenceValidationStage.CadenceObserved,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!state.OneHertzCadencePhysicallyConfirmed)
                    {
                        state.Failure = cancellationToken.IsCancellationRequested
                            ? CoolingPadFanCadenceValidationFailure.Canceled
                            : CoolingPadFanCadenceValidationFailure
                                .CadenceNotConfirmed;
                    }
                }
            }
            catch (BladeWriteException exception)
            {
                bool known = exception.SubmissionState
                    == WriteSubmissionState.KnownNotApplied;
                state.Failure = known
                    ? CoolingPadFanCadenceValidationFailure.AcquireKnownNotApplied
                    : CoolingPadFanCadenceValidationFailure.AcquireIndeterminate;
                state.ManualPowerCycleMayBeRequired |= !known;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                state.Failure = CoolingPadFanCadenceValidationFailure.Canceled;
            }

            bool canRelease = state.AcquireAcknowledged
                && state.Failure != CoolingPadFanCadenceValidationFailure
                    .UpdateIndeterminate
                && !poisoned;
            if (canRelease)
            {
                state.ReleaseAttempted = true;
                try
                {
                    using var cleanup = new CancellationTokenSource(CleanupTimeout);
                    await executor.ReleaseAsync(cleanup.Token).ConfigureAwait(false);
                    state.ReleaseAcknowledged = true;
                }
                catch (BladeWriteException exception)
                {
                    bool known = exception.SubmissionState
                        == WriteSubmissionState.KnownNotApplied;
                    state.Failure = known
                        ? CoolingPadFanCadenceValidationFailure
                            .ReleaseKnownNotApplied
                        : CoolingPadFanCadenceValidationFailure
                            .ReleaseIndeterminate;
                    state.ManualPowerCycleMayBeRequired |= !known;
                }
                catch
                {
                    state.Failure = CoolingPadFanCadenceValidationFailure
                        .ReleaseIndeterminate;
                    state.ManualPowerCycleMayBeRequired = true;
                }

                if (state.ReleaseAcknowledged
                    && !cancellationToken.IsCancellationRequested)
                {
                    state.ReleasePhysicallyConfirmed = await ConfirmAsync(
                            confirm,
                            CoolingPadFanCadenceValidationStage.Release,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!state.ReleasePhysicallyConfirmed
                        && state.Failure
                            == CoolingPadFanCadenceValidationFailure.None)
                    {
                        state.Failure = CoolingPadFanCadenceValidationFailure
                            .ReleaseNotConfirmed;
                    }
                }
            }

            state.ManualSynapseRecoveryRequired = state.AcquireAcknowledged
                && !state.ReleasePhysicallyConfirmed;
            return state.ToResult();
        }
        catch
        {
            state.Failure = CoolingPadFanCadenceValidationFailure.UnexpectedFailure;
            state.ManualSynapseRecoveryRequired |= state.AcquireAcknowledged;
            return state.ToResult();
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

    private static async Task<bool> ConfirmAsync(
        Func<CoolingPadFanCadenceValidationStage, CancellationToken, Task<bool>>
            confirm,
        CoolingPadFanCadenceValidationStage stage,
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

    private sealed class MutableResult
    {
        internal bool BaselineAutoPhysicallyConfirmed { get; set; }
        internal bool AcquireAcknowledged { get; set; }
        internal int RequestedUpdateCount { get; set; }
        internal int AcknowledgedUpdateCount { get; set; }
        internal bool OneHertzCadencePhysicallyConfirmed { get; set; }
        internal bool ReleaseAttempted { get; set; }
        internal bool ReleaseAcknowledged { get; set; }
        internal bool ReleasePhysicallyConfirmed { get; set; }
        internal bool ManualSynapseRecoveryRequired { get; set; }
        internal bool ManualPowerCycleMayBeRequired { get; set; }
        internal CoolingPadFanCadenceValidationFailure Failure { get; set; }

        internal CoolingPadFanCadenceValidationResult ToResult() => new(
            BaselineAutoPhysicallyConfirmed,
            AcquireAcknowledged,
            RequestedUpdateCount,
            AcknowledgedUpdateCount,
            OneHertzCadencePhysicallyConfirmed,
            ReleaseAttempted,
            ReleaseAcknowledged,
            ReleasePhysicallyConfirmed,
            ManualSynapseRecoveryRequired,
            ManualPowerCycleMayBeRequired,
            Failure);
    }
}

internal sealed class CoolingPadFanCadenceValidationCommand(
    Func<Func<CoolingPadFanCadenceValidationStage, CancellationToken, Task<bool>>,
        CancellationToken,
        Task<CoolingPadFanCadenceValidationResult>> run,
    TextReader input,
    TextWriter output)
{
    internal const string Confirmation =
        "RZ09-0581:4.00:1532:0F43:FAN-SMART-CURVE:1HZ:7-UPDATES:RELEASE:ONCE";

    internal async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args is not ["--confirm-target", Confirmation])
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture validate-cooling-pad-fan-cadence " +
                "--confirm-target " + Confirmation).ConfigureAwait(false);
            return 2;
        }

        CoolingPadFanCadenceValidationResult result = await run(
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
                intervalMilliseconds = 1_000,
                targetRpmSequence = CoolingPadFanCadenceValidationRunner
                    .UpdateTargets.Select(target => target.Rpm),
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
        CoolingPadFanCadenceValidationStage stage,
        CancellationToken cancellationToken)
    {
        string expected = stage switch
        {
            CoolingPadFanCadenceValidationStage.BaselineAuto =>
                "FAN-AUTO-BASELINE-CONFIRMED",
            CoolingPadFanCadenceValidationStage.CadenceObserved =>
                "FAN-1HZ-CADENCE-CONFIRMED",
            CoolingPadFanCadenceValidationStage.Release =>
                "FAN-RELEASE-CONFIRMED",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        await output.WriteLineAsync(
            $"Type {expected} after physical confirmation.").ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        return response?.Equals(expected, StringComparison.Ordinal) == true;
    }
}
