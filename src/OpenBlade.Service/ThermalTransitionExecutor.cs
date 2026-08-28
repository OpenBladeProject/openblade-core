using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal enum ThermalTransitionStage
{
    Preflight = 0,
    Submission = 1,
    Acknowledgement = 2,
    Confirmation = 3,
}

internal enum ThermalSubmissionProgress
{
    NotSubmitted = 0,
    Submitted = 1,
    Acknowledged = 2,
}

internal enum ThermalTransitionDisposition
{
    Confirmed = 0,
    Acknowledged = 1,
    External = 2,
    ReconciliationUnavailable = 3,
    Blocked = 4,
    Failed = 5,
}

internal enum ThermalTransitionStepPurpose
{
    Requested = 0,
    AutomaticBridge = 1,
    BalancedBridge = 2,
    Restoration = 3,
    ManualTargetContinuation = 4,
}

internal sealed record ThermalTransitionStepAttempt(
    ScopedApplyOutcome Outcome,
    ThermalTransitionStage Stage,
    ThermalSubmissionProgress SubmissionProgress,
    ThermalTransitionDisposition Disposition);

internal sealed record ThermalTransitionStepTrace(
    DeviceSubsystem Component,
    ThermalTransitionStepPurpose Purpose,
    IReadOnlyList<ThermalTransitionStepAttempt> Attempts);

internal sealed record ThermalTransitionTrace(
    Guid AttemptId,
    IReadOnlyList<DeviceSubsystem> Components,
    ThermalTransitionStage Stage,
    ThermalSubmissionProgress SubmissionProgress,
    WriteSubmissionState? Certainty,
    ThermalTransitionDisposition Disposition,
    int AttemptCount,
    bool RetryConsumed,
    string? RetryBlockReason,
    IReadOnlyList<ThermalTransitionStepTrace>? Steps = null);

internal sealed record ThermalTransitionExecution(
    ScopedApplyOutcome Outcome,
    ThermalTransitionTrace Trace);

internal sealed record ThermalFullTransitionExecution(
    FullTransitionApplyOutcome Outcome,
    ThermalTransitionTrace Trace,
    IBladeDevice Device);

internal sealed record ThermalRetryAdmission(
    bool Allowed,
    IBladeDevice? Device,
    string? BlockReason)
{
    public static ThermalRetryAdmission Allow(IBladeDevice device) => new(
        Allowed: true,
        Device: device ?? throw new ArgumentNullException(nameof(device)),
        BlockReason: null);

    public static ThermalRetryAdmission Block(string reason) => new(
        Allowed: false,
        Device: null,
        BlockReason: string.IsNullOrWhiteSpace(reason)
            ? "Thermal retry admission was not renewed."
            : reason);
}

internal sealed class ThermalTransitionStepRunner
{
    private readonly Guid attemptId;
    private readonly IReadOnlyList<DeviceSubsystem> declaredComponents;
    private readonly HashSet<DeviceSubsystem> declaredComponentSet;
    private readonly Func<IBladeDevice, ThermalTransitionStepRunner, ControlCoordinator>
        coordinatorFactory;
    private readonly Func<Guid, CancellationToken, Task<ThermalRetryAdmission>>
        revalidateRecovery;
    private readonly List<ThermalTransitionStepTrace> steps = [];
    private IBladeDevice currentDevice;
    private bool recoveryLeaseConsumed;
    private string? recoveryBlockReason;

    public ThermalTransitionStepRunner(
        Guid attemptId,
        IReadOnlyList<DeviceSubsystem> declaredComponents,
        IBladeDevice initialDevice,
        Func<IBladeDevice, ThermalTransitionStepRunner, ControlCoordinator>
            coordinatorFactory,
        Func<Guid, CancellationToken, Task<ThermalRetryAdmission>> revalidateRecovery)
    {
        if (declaredComponents is null || declaredComponents.Count == 0)
        {
            throw new ArgumentException(
                "A thermal transition must declare at least one component.",
                nameof(declaredComponents));
        }

        if (declaredComponents.Any(component => component is not (
                DeviceSubsystem.Performance or DeviceSubsystem.Fans)))
        {
            throw new ArgumentException(
                "A thermal transition may declare only performance and fan components.",
                nameof(declaredComponents));
        }

        declaredComponentSet = declaredComponents.ToHashSet();
        if (declaredComponentSet.Count != declaredComponents.Count)
        {
            throw new ArgumentException(
                "A thermal transition cannot declare a component more than once.",
                nameof(declaredComponents));
        }

        this.attemptId = attemptId;
        this.declaredComponents = [.. declaredComponents];
        currentDevice = initialDevice ?? throw new ArgumentNullException(nameof(initialDevice));
        this.coordinatorFactory = coordinatorFactory
            ?? throw new ArgumentNullException(nameof(coordinatorFactory));
        this.revalidateRecovery = revalidateRecovery
            ?? throw new ArgumentNullException(nameof(revalidateRecovery));
    }

    public IReadOnlyList<DeviceSubsystem> DeclaredComponents => declaredComponents;

    public IReadOnlyList<ThermalTransitionStepTrace> Steps => steps;

    public bool RecoveryLeaseConsumed => recoveryLeaseConsumed;

    public string? RecoveryBlockReason => recoveryBlockReason;

    public int AttemptCount => steps.Sum(step => step.Attempts.Count);

    public IBladeDevice CurrentDevice => currentDevice;

    public ControlCoordinator CreateRecipeCoordinator() =>
        coordinatorFactory(currentDevice, this);

    public async Task<bool> TryRecoverPreflightAsync(
        Func<Guid, CancellationToken, Task<ThermalRetryAdmission>>
            revalidatePreflight,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revalidatePreflight);
        if (steps.Count > 0 || recoveryLeaseConsumed)
        {
            return false;
        }

        ThermalRetryAdmission admission = await ConsumeAndRevalidateAsync(
                revalidatePreflight,
                cancellationToken)
            .ConfigureAwait(false);
        if (!admission.Allowed || admission.Device is null)
        {
            return false;
        }

        currentDevice = admission.Device;
        return true;
    }

    public async Task<ScopedApplyOutcome> ApplyStepAsync(
        DeviceSubsystem component,
        ThermalTransitionStepPurpose purpose,
        Func<ControlCoordinator, CancellationToken, Task<ScopedApplyOutcome>> applyOnce,
        CancellationToken cancellationToken)
    {
        EnsureDeclared(component);
        ArgumentNullException.ThrowIfNull(applyOnce);

        var attempts = new List<ThermalTransitionStepAttempt>();
        ScopedApplyOutcome outcome = await ApplyOnceAsync(
                currentDevice,
                applyOnce,
                cancellationToken)
            .ConfigureAwait(false);
        attempts.Add(CreateAttempt(outcome));

        if (IsEligibleForFreshSessionRetry(outcome))
        {
            if (recoveryLeaseConsumed)
            {
                outcome = AppendNoRecoveryMessage(
                    outcome,
                    "the transition recovery lease was already consumed");
                attempts[^1] = CreateAttempt(outcome);
            }
            else
            {
                ThermalRetryAdmission admission = await ConsumeAndRevalidateAsync(
                        revalidateRecovery,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!admission.Allowed || admission.Device is null)
                {
                    outcome = AppendNoRecoveryMessage(
                        outcome,
                        admission.BlockReason
                            ?? "thermal retry admission was not renewed");
                    attempts[^1] = CreateAttempt(
                        outcome,
                        ThermalTransitionDisposition.Blocked);
                }
                else
                {
                    currentDevice = admission.Device;
                    outcome = await ApplyOnceAsync(
                            currentDevice,
                            applyOnce,
                            cancellationToken)
                        .ConfigureAwait(false);
                    attempts.Add(CreateAttempt(outcome));
                }
            }
        }

        steps.Add(new ThermalTransitionStepTrace(component, purpose, attempts));
        return outcome;
    }

    public async Task<ScopedApplyOutcome> ApplyFreshContinuationAsync(
        DeviceSubsystem component,
        ThermalTransitionStepPurpose purpose,
        ScopedApplyOutcome possibleSubmission,
        Func<ControlCoordinator, CancellationToken, Task<ScopedApplyOutcome>> continueOnce,
        CancellationToken cancellationToken)
    {
        EnsureDeclared(component);
        ArgumentNullException.ThrowIfNull(possibleSubmission);
        ArgumentNullException.ThrowIfNull(continueOnce);
        if (possibleSubmission.WriteSubmissionState != WriteSubmissionState.Indeterminate)
        {
            throw new InvalidOperationException(
                "A post-submission continuation requires indeterminate submission certainty.");
        }

        var attempts = new List<ThermalTransitionStepAttempt>();
        if (recoveryLeaseConsumed)
        {
            ScopedApplyOutcome blocked = AppendNoRecoveryMessage(
                possibleSubmission,
                "the transition recovery lease was already consumed");
            attempts.Add(CreateAttempt(
                blocked,
                ThermalTransitionDisposition.ReconciliationUnavailable));
            steps.Add(new ThermalTransitionStepTrace(component, purpose, attempts));
            return blocked;
        }

        ThermalRetryAdmission admission = await ConsumeAndRevalidateAsync(
                revalidateRecovery,
                cancellationToken)
            .ConfigureAwait(false);
        if (!admission.Allowed || admission.Device is null)
        {
            ScopedApplyOutcome blocked = AppendNoRecoveryMessage(
                possibleSubmission,
                admission.BlockReason ?? "thermal continuation admission was not renewed");
            attempts.Add(CreateAttempt(
                blocked,
                ThermalTransitionDisposition.ReconciliationUnavailable));
            steps.Add(new ThermalTransitionStepTrace(component, purpose, attempts));
            return blocked;
        }

        currentDevice = admission.Device;
        ScopedApplyOutcome outcome = await ApplyOnceAsync(
                currentDevice,
                continueOnce,
                cancellationToken)
            .ConfigureAwait(false);
        attempts.Add(CreateAttempt(outcome));
        steps.Add(new ThermalTransitionStepTrace(component, purpose, attempts));
        return outcome;
    }

    private async Task<ThermalRetryAdmission> ConsumeAndRevalidateAsync(
        Func<Guid, CancellationToken, Task<ThermalRetryAdmission>> revalidate,
        CancellationToken cancellationToken)
    {
        recoveryLeaseConsumed = true;
        try
        {
            ThermalRetryAdmission admission = await revalidate(
                    attemptId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!admission.Allowed)
            {
                recoveryBlockReason = admission.BlockReason
                    ?? "Thermal recovery admission was not renewed.";
            }
            return admission;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            recoveryBlockReason =
                $"Fresh-session recovery admission failed: {exception.Message}";
            return ThermalRetryAdmission.Block(recoveryBlockReason);
        }
    }

    private Task<ScopedApplyOutcome> ApplyOnceAsync(
        IBladeDevice device,
        Func<ControlCoordinator, CancellationToken, Task<ScopedApplyOutcome>> applyOnce,
        CancellationToken cancellationToken) => applyOnce(
            coordinatorFactory(device, this),
            cancellationToken);

    private void EnsureDeclared(DeviceSubsystem component)
    {
        if (!declaredComponentSet.Contains(component))
        {
            throw new InvalidOperationException(
                $"Thermal component {component} was not declared before execution.");
        }
    }

    private static ScopedApplyOutcome AppendNoRecoveryMessage(
        ScopedApplyOutcome outcome,
        string reason) => outcome with
        {
            Result = outcome.Result with
            {
                Message = $"{outcome.Result.Message} No retry was submitted: {reason}.",
            },
        };

    private static ThermalTransitionStepAttempt CreateAttempt(
        ScopedApplyOutcome outcome,
        ThermalTransitionDisposition? disposition = null)
    {
        ThermalSubmissionProgress progress = ThermalTransitionExecutor
            .SubmissionProgress(outcome);
        return new ThermalTransitionStepAttempt(
            outcome,
            ThermalTransitionExecutor.Stage(outcome, progress),
            progress,
            disposition ?? ThermalTransitionExecutor.Disposition(outcome));
    }

    private static bool IsEligibleForFreshSessionRetry(ScopedApplyOutcome outcome) =>
        outcome.WriteSubmissionState == WriteSubmissionState.KnownNotApplied
        && outcome.WriteFailurePhase == BladeWriteFailurePhase.ThermalPreWriteRead;
}

internal static class ThermalTransitionExecutor
{
    private static readonly DeviceSubsystem[] ScopedPerformanceComponents =
        [DeviceSubsystem.Performance];

    public static Task<ThermalTransitionExecution> ExecuteScopedPerformanceAsync(
        IBladeDevice initialDevice,
        Func<IBladeDevice, CancellationToken, Task<ScopedApplyOutcome>> applyOnce,
        Func<Guid, CancellationToken, Task<ThermalRetryAdmission>> revalidateRetry,
        CancellationToken cancellationToken) => ExecuteAsync(
            ScopedPerformanceComponents,
            initialDevice,
            (device, _) => new ControlCoordinator(device),
            (runner, token) => runner.ApplyStepAsync(
                DeviceSubsystem.Performance,
                ThermalTransitionStepPurpose.Requested,
                (coordinator, stepToken) => applyOnce(
                    coordinator.ThermalDevice,
                    stepToken),
                token),
            revalidateRetry,
            cancellationToken);

    public static Task<ThermalTransitionExecution> ExecuteAsync(
        IReadOnlyList<DeviceSubsystem> declaredComponents,
        IBladeDevice initialDevice,
        Func<IBladeDevice, ThermalTransitionStepRunner, ControlCoordinator>
            coordinatorFactory,
        Func<ThermalTransitionStepRunner, CancellationToken, Task<ScopedApplyOutcome>>
            executeRecipe,
        Func<Guid, CancellationToken, Task<ThermalRetryAdmission>> revalidateRecovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialDevice);
        ArgumentNullException.ThrowIfNull(coordinatorFactory);
        ArgumentNullException.ThrowIfNull(executeRecipe);
        ArgumentNullException.ThrowIfNull(revalidateRecovery);
        return ExecuteCoreAsync(
            declaredComponents,
            initialDevice,
            coordinatorFactory,
            executeRecipe,
            revalidateRecovery,
            cancellationToken);
    }

    public static Task<ThermalFullTransitionExecution> ExecuteFullTransitionAsync(
        IReadOnlyList<DeviceSubsystem> declaredComponents,
        IBladeDevice initialDevice,
        Func<IBladeDevice, ThermalTransitionStepRunner, ControlCoordinator>
            coordinatorFactory,
        Func<ControlCoordinator, CancellationToken, Task<FullTransitionApplyOutcome>>
            executeRecipe,
        Func<Guid, CancellationToken, Task<ThermalRetryAdmission>>
            revalidatePreflight,
        Func<Guid, CancellationToken, Task<ThermalRetryAdmission>>
            revalidateThermalRecovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialDevice);
        ArgumentNullException.ThrowIfNull(coordinatorFactory);
        ArgumentNullException.ThrowIfNull(executeRecipe);
        ArgumentNullException.ThrowIfNull(revalidatePreflight);
        ArgumentNullException.ThrowIfNull(revalidateThermalRecovery);
        return ExecuteFullTransitionCoreAsync(
            declaredComponents,
            initialDevice,
            coordinatorFactory,
            executeRecipe,
            revalidatePreflight,
            revalidateThermalRecovery,
            cancellationToken);
    }

    private static async Task<ThermalFullTransitionExecution>
        ExecuteFullTransitionCoreAsync(
        IReadOnlyList<DeviceSubsystem> declaredComponents,
        IBladeDevice initialDevice,
        Func<IBladeDevice, ThermalTransitionStepRunner, ControlCoordinator>
            coordinatorFactory,
        Func<ControlCoordinator, CancellationToken, Task<FullTransitionApplyOutcome>>
            executeRecipe,
        Func<Guid, CancellationToken, Task<ThermalRetryAdmission>>
            revalidatePreflight,
        Func<Guid, CancellationToken, Task<ThermalRetryAdmission>>
            revalidateThermalRecovery,
        CancellationToken cancellationToken)
    {
        Guid attemptId = Guid.NewGuid();
        var runner = new ThermalTransitionStepRunner(
            attemptId,
            declaredComponents,
            initialDevice,
            coordinatorFactory,
            revalidateThermalRecovery);
        int preflightAttempts = 1;
        FullTransitionApplyOutcome outcome = await executeRecipe(
                runner.CreateRecipeCoordinator(),
                cancellationToken)
            .ConfigureAwait(false);
        if (IsEligibleForFullPreflightRetry(outcome)
            && await runner.TryRecoverPreflightAsync(
                    revalidatePreflight,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            preflightAttempts++;
            outcome = await executeRecipe(
                    runner.CreateRecipeCoordinator(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return CreateFullExecution(attemptId, outcome, runner, preflightAttempts);
    }

    private static async Task<ThermalTransitionExecution> ExecuteCoreAsync(
        IReadOnlyList<DeviceSubsystem> declaredComponents,
        IBladeDevice initialDevice,
        Func<IBladeDevice, ThermalTransitionStepRunner, ControlCoordinator>
            coordinatorFactory,
        Func<ThermalTransitionStepRunner, CancellationToken, Task<ScopedApplyOutcome>>
            executeRecipe,
        Func<Guid, CancellationToken, Task<ThermalRetryAdmission>> revalidateRecovery,
        CancellationToken cancellationToken)
    {
        Guid attemptId = Guid.NewGuid();
        var runner = new ThermalTransitionStepRunner(
            attemptId,
            declaredComponents,
            initialDevice,
            coordinatorFactory,
            revalidateRecovery);
        ScopedApplyOutcome outcome = await executeRecipe(runner, cancellationToken)
            .ConfigureAwait(false);
        return CreateExecution(attemptId, outcome, runner);
    }

    private static ThermalTransitionExecution CreateExecution(
        Guid attemptId,
        ScopedApplyOutcome outcome,
        ThermalTransitionStepRunner runner)
    {
        ThermalSubmissionProgress submissionProgress = SubmissionProgress(outcome);
        ThermalTransitionStage stage = Stage(outcome, submissionProgress);
        ThermalTransitionDisposition disposition = Disposition(outcome);
        ThermalTransitionStepAttempt? lastAttempt = runner.Steps
            .LastOrDefault()?.Attempts.LastOrDefault();
        if (lastAttempt is not null && lastAttempt.Outcome == outcome)
        {
            stage = lastAttempt.Stage;
            submissionProgress = lastAttempt.SubmissionProgress;
            disposition = lastAttempt.Disposition;
        }

        return new ThermalTransitionExecution(
            outcome,
            new ThermalTransitionTrace(
                attemptId,
                runner.DeclaredComponents,
                stage,
                submissionProgress,
                outcome.WriteSubmissionState,
                disposition,
                runner.AttemptCount,
                runner.RecoveryLeaseConsumed,
                runner.RecoveryBlockReason,
                runner.Steps));
    }

    private static ThermalFullTransitionExecution CreateFullExecution(
        Guid attemptId,
        FullTransitionApplyOutcome outcome,
        ThermalTransitionStepRunner runner,
        int preflightAttempts)
    {
        ThermalTransitionStepAttempt? lastAttempt = runner.Steps
            .LastOrDefault()?.Attempts.LastOrDefault();
        bool thermalAttempted = outcome.WriteAttempted.Any(
            component => component is DeviceSubsystem.Performance or DeviceSubsystem.Fans);
        bool thermalResultsSuccessful = runner.DeclaredComponents.All(component =>
            component switch
            {
                DeviceSubsystem.Performance => outcome.Results.Performance.Status,
                DeviceSubsystem.Fans => outcome.Results.Fans.Status,
                _ => ControlResultStatus.Failed,
            } is ControlResultStatus.Applied or ControlResultStatus.Unchanged);
        bool thermalConfirmed = thermalAttempted
            && thermalResultsSuccessful
            && outcome.WriteAttempted
                .Where(component => component is
                    DeviceSubsystem.Performance or DeviceSubsystem.Fans)
                .All(outcome.ReadbackMatched.Contains);
        ThermalTransitionStage stage = thermalConfirmed
            ? ThermalTransitionStage.Confirmation
            : lastAttempt?.Stage
                ?? ThermalTransitionStage.Preflight;
        ThermalSubmissionProgress submissionProgress = thermalConfirmed
            ? ThermalSubmissionProgress.Acknowledged
            : lastAttempt?.SubmissionProgress
                ?? ThermalSubmissionProgress.NotSubmitted;
        ThermalTransitionDisposition disposition = thermalConfirmed
            ? ThermalTransitionDisposition.Confirmed
            : outcome.Readback is null && thermalAttempted
                ? ThermalTransitionDisposition.ReconciliationUnavailable
                : lastAttempt?.Disposition
                    ?? ZeroStepDisposition(outcome, runner.DeclaredComponents);

        int attemptCount = Math.Max(
            1,
            runner.AttemptCount) + preflightAttempts - 1;

        var trace = new ThermalTransitionTrace(
            attemptId,
            runner.DeclaredComponents,
            stage,
            submissionProgress,
            lastAttempt?.Outcome.WriteSubmissionState,
            disposition,
            attemptCount,
            runner.RecoveryLeaseConsumed,
            runner.RecoveryBlockReason,
            runner.Steps);
        return new ThermalFullTransitionExecution(
            outcome,
            trace,
            runner.CurrentDevice);
    }

    private static ThermalTransitionDisposition ZeroStepDisposition(
        FullTransitionApplyOutcome outcome,
        IReadOnlyList<DeviceSubsystem> declaredComponents)
    {
        if (outcome.DeviceAccessFailed)
        {
            return ThermalTransitionDisposition.Failed;
        }

        IEnumerable<ControlResultStatus> statuses = declaredComponents.Select(
            component => component switch
            {
                DeviceSubsystem.Performance => outcome.Results.Performance.Status,
                DeviceSubsystem.Fans => outcome.Results.Fans.Status,
                _ => ControlResultStatus.Failed,
            });
        if (statuses.Any(status => status == ControlResultStatus.Failed))
        {
            return ThermalTransitionDisposition.Failed;
        }
        if (statuses.Any(status => status == ControlResultStatus.Blocked))
        {
            return ThermalTransitionDisposition.Blocked;
        }
        return ThermalTransitionDisposition.Confirmed;
    }

    private static bool IsEligibleForFullPreflightRetry(
        FullTransitionApplyOutcome outcome) =>
        outcome.DeviceAccessFailed
        && outcome.WriteAttempted.Count == 0
        && outcome.Readback is null;

    internal static ThermalSubmissionProgress SubmissionProgress(
        ScopedApplyOutcome outcome)
    {
        if (outcome.WriteSubmissionState == WriteSubmissionState.KnownNotApplied)
        {
            return ThermalSubmissionProgress.NotSubmitted;
        }
        if (outcome.WriteSubmissionState == WriteSubmissionState.Indeterminate)
        {
            return ThermalSubmissionProgress.Submitted;
        }
        if (!outcome.WriteAttempted)
        {
            return ThermalSubmissionProgress.NotSubmitted;
        }
        return outcome.Readback is not null
            || outcome.Result.Status == ControlResultStatus.Applied
            ? ThermalSubmissionProgress.Acknowledged
            : ThermalSubmissionProgress.Submitted;
    }

    internal static ThermalTransitionStage Stage(
        ScopedApplyOutcome outcome,
        ThermalSubmissionProgress submissionProgress)
    {
        if (outcome.WriteSubmissionState == WriteSubmissionState.KnownNotApplied
            || !outcome.WriteAttempted
                && outcome.Result.Status == ControlResultStatus.Blocked)
        {
            return ThermalTransitionStage.Preflight;
        }
        if (outcome.ReadbackMatched)
        {
            return ThermalTransitionStage.Confirmation;
        }
        if (outcome.WriteSubmissionState == WriteSubmissionState.Indeterminate
            && outcome.Readback is not null)
        {
            return ThermalTransitionStage.Confirmation;
        }
        return submissionProgress switch
        {
            ThermalSubmissionProgress.Acknowledged =>
                ThermalTransitionStage.Acknowledgement,
            ThermalSubmissionProgress.Submitted => ThermalTransitionStage.Submission,
            _ when outcome.WriteSubmissionState is not null =>
                ThermalTransitionStage.Submission,
            _ => ThermalTransitionStage.Preflight,
        };
    }

    internal static ThermalTransitionDisposition Disposition(
        ScopedApplyOutcome outcome)
    {
        if (outcome.WriteSubmissionState == WriteSubmissionState.KnownNotApplied)
        {
            return outcome.Result.Status == ControlResultStatus.Blocked
                ? ThermalTransitionDisposition.Blocked
                : ThermalTransitionDisposition.Failed;
        }
        if (outcome.ReadbackMatched
            && outcome.Result.Status is
                ControlResultStatus.Applied or ControlResultStatus.Unchanged)
        {
            return ThermalTransitionDisposition.Confirmed;
        }
        if (outcome.Result.Status == ControlResultStatus.Applied
            && outcome.WriteAttempted)
        {
            return ThermalTransitionDisposition.Acknowledged;
        }
        if (outcome.ConfirmationUnavailable)
        {
            return ThermalTransitionDisposition.ReconciliationUnavailable;
        }
        if (outcome.Readback is not null
            && (outcome.WriteAttempted
                || outcome.WriteSubmissionState == WriteSubmissionState.Indeterminate))
        {
            return ThermalTransitionDisposition.External;
        }
        if (outcome.Result.Status == ControlResultStatus.Blocked)
        {
            return ThermalTransitionDisposition.Blocked;
        }
        return ThermalTransitionDisposition.Failed;
    }
}
