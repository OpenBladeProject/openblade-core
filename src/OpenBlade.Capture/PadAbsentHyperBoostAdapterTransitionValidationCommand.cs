using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed class PadAbsentHyperBoostAdapterTransitionValidationCommand
{
    internal const string ExactTargetConfirmation =
        "RZ09-0581:1532:02E0:4.00:PAD-ABSENT:ADAPTER-TRANSITION";
    internal const string InitialApproval =
        "RZ09-0581:BIOS-4.00:PAD-PHYSICALLY-ABSENT:ADMITTED-FULL-POWER:" +
        "BALANCED-AUTO:APPLY-ONCE:WAIT-FOR-USB-C-100W:RESTORE";
    internal const string TransitionApproval =
        "RZ09-0581:MODE-7-CONFIRMED:SWITCHED-FULL-POWER-TO-USB-C-100W:" +
        "QUERY-ONCE:RESTORE-BALANCED";
    internal const int UsageExitCode = 1;
    internal const int ValidationFailureExitCode = 2;
    internal const int ApprovalRejectedExitCode = 3;
    internal const int CanceledExitCode = 4;

    private static readonly TimeSpan AbsenceSampleInterval =
        TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ReviewedHyperBoostThermalPlan plan;
    private readonly Func<CoolingPadPhysicalPresenceObservation> observePad;
    private readonly Func<Func<CancellationToken, Task<bool>>, CancellationToken,
        Task<PerformanceFanSetterValidationResult>> run;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly Func<Action, IDisposable> registerCancellation;

    internal PadAbsentHyperBoostAdapterTransitionValidationCommand(
        ReviewedHyperBoostThermalPlan plan,
        Func<CoolingPadPhysicalPresenceObservation> observePad,
        Func<Func<CancellationToken, Task<bool>>, CancellationToken,
            Task<PerformanceFanSetterValidationResult>> run,
        TextReader input,
        TextWriter output,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<Action, IDisposable>? registerCancellation = null)
    {
        this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
        this.observePad = observePad ?? throw new ArgumentNullException(nameof(observePad));
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        this.registerCancellation = registerCancellation ?? ConsoleCancellation.Register;
    }

    internal async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 2
            || !arguments[0].Equals("--confirm-target", StringComparison.Ordinal)
            || !arguments[1].Equals(ExactTargetConfirmation, StringComparison.Ordinal))
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture validate-rz09-0581-02e0-bios-4.00-" +
                "hyperboost-pad-absent-adapter-transition --confirm-target " +
                ExactTargetConfirmation).ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellation(
            () => ConsoleCancellation.Cancel(commandCancellation));
        try
        {
            CoolingPadPhysicalPresenceObservation first = observePad();
            if (first != CoolingPadPhysicalPresenceObservation.Absent)
            {
                await WritePreflightFailureAsync(first, null).ConfigureAwait(false);
                return ValidationFailureExitCode;
            }

            await delay(AbsenceSampleInterval, commandCancellation.Token)
                .ConfigureAwait(false);
            CoolingPadPhysicalPresenceObservation second = observePad();
            if (second != CoolingPadPhysicalPresenceObservation.Absent)
            {
                await WritePreflightFailureAsync(first, second).ConfigureAwait(false);
                return ValidationFailureExitCode;
            }

            await output.WriteLineAsync(
                "WARNING: Developer-only pad-absent HyperBoost adapter-transition " +
                "validation. No workload is started and production support remains disabled.")
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                $"Plan={plan.PlanId} SHA256={plan.SourceSha256}")
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                "Keep the admitted full-power adapter connected until paired mode-7 " +
                "readback is confirmed. Type this exact phrase:")
                .ConfigureAwait(false);
            await output.WriteLineAsync(InitialApproval).ConfigureAwait(false);
            string? approval = await input.ReadLineAsync(commandCancellation.Token)
                .ConfigureAwait(false);
            if (!string.Equals(approval, InitialApproval, StringComparison.Ordinal))
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(
                    new ApprovalDocument(
                        SchemaVersion: 1,
                        ProductionAdmitted: false,
                        PadlessProductionAdmitted: false,
                        PlanId: plan.PlanId,
                        PlanSha256: plan.SourceSha256,
                        InitialApprovalConfirmed: false),
                    JsonOptions)).ConfigureAwait(false);
                return ApprovalRejectedExitCode;
            }

            PerformanceFanSetterValidationResult result = await run(
                ConfirmTransitionAsync,
                commandCancellation.Token).ConfigureAwait(false);
            bool postRestorationPadAbsent =
                observePad() == CoolingPadPhysicalPresenceObservation.Absent;
            await output.WriteLineAsync(JsonSerializer.Serialize(
                CaptureDocument.From(plan, result, postRestorationPadAbsent),
                JsonOptions)).ConfigureAwait(false);
            return IsSuccessfulResult(result, postRestorationPadAbsent)
                ? 0
                : result.Failure == PerformanceFanValidationFailure.Canceled
                    ? CanceledExitCode
                    : ValidationFailureExitCode;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await output.WriteLineAsync("{\"canceled\":true}").ConfigureAwait(false);
            return CanceledExitCode;
        }
    }

    private async Task<bool> ConfirmTransitionAsync(CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(
            "Paired mode-7 readback is confirmed. Connect the reviewed 100 W USB-C " +
            "power source, disconnect the full-power adapter, then type this exact phrase:")
            .ConfigureAwait(false);
        await output.WriteLineAsync(TransitionApproval).ConfigureAwait(false);
        string? approval = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(approval, TransitionApproval, StringComparison.Ordinal);
    }

    private static bool IsSuccessfulResult(
        PerformanceFanSetterValidationResult result,
        bool postRestorationPadAbsent) =>
        result.Target == PerformanceFanValidationTarget.HyperBoost
        && result.Success
        && result.BaselineBalancedConfirmed
        && result.FullPowerAdapterConfirmed
        && result.CoolingPadAbsentConfirmed
        && result.AdapterTransitionValidation is { Success: true }
        && result.RestoreAttempted
        && result.RestoreAcknowledged
        && result.Restored
        && postRestorationPadAbsent
        && !result.IndeterminatePartialWrite
        && !result.ManualRecoveryRequired;

    private Task WritePreflightFailureAsync(
        CoolingPadPhysicalPresenceObservation first,
        CoolingPadPhysicalPresenceObservation? second) => output.WriteLineAsync(
            JsonSerializer.Serialize(
                new PreflightFailureDocument(
                    SchemaVersion: 1,
                    ProductionAdmitted: false,
                    PadlessProductionAdmitted: false,
                    PlanId: plan.PlanId,
                    PlanSha256: plan.SourceSha256,
                    FirstObservation: first,
                    SecondObservation: second,
                    ValidationAttempted: false),
                JsonOptions));

    private sealed record ApprovalDocument(
        int SchemaVersion,
        bool ProductionAdmitted,
        bool PadlessProductionAdmitted,
        string PlanId,
        string PlanSha256,
        bool InitialApprovalConfirmed);

    private sealed record PreflightFailureDocument(
        int SchemaVersion,
        bool ProductionAdmitted,
        bool PadlessProductionAdmitted,
        string PlanId,
        string PlanSha256,
        CoolingPadPhysicalPresenceObservation FirstObservation,
        CoolingPadPhysicalPresenceObservation? SecondObservation,
        bool ValidationAttempted);

    private sealed record CaptureDocument(
        int SchemaVersion,
        bool ProductionAdmitted,
        bool PadlessProductionAdmitted,
        string PlanId,
        string PlanSha256,
        bool FirstAbsenceConfirmed,
        bool SecondAbsenceConfirmed,
        bool InitialApprovalConfirmed,
        bool PostRestorationPadAbsentConfirmed,
        SemanticResult Result)
    {
        internal static CaptureDocument From(
            ReviewedHyperBoostThermalPlan plan,
            PerformanceFanSetterValidationResult result,
            bool postRestorationPadAbsent) => new(
                SchemaVersion: 1,
                ProductionAdmitted: false,
                PadlessProductionAdmitted: false,
                plan.PlanId,
                plan.SourceSha256,
                FirstAbsenceConfirmed: true,
                SecondAbsenceConfirmed: true,
                InitialApprovalConfirmed: true,
                postRestorationPadAbsent,
                SemanticResult.From(result, postRestorationPadAbsent));
    }

    private sealed record SemanticResult(
        bool BaselineBalancedConfirmed,
        bool InitialFullPowerAdapterConfirmed,
        bool PreApplyPadAbsenceConfirmed,
        bool TransitionPromptConfirmed,
        bool FreshAdapterReadCompleted,
        AdapterPowerState InitialAdapterState,
        AdapterPowerState ObservedAdapterState,
        bool ReviewedAdapterTransitionConfirmed,
        bool HyperBoostReadbackConfirmed,
        bool BalancedRestorationConfirmed,
        bool IndeterminatePartialWrite,
        bool ManualRecoveryRequired,
        HyperBoostAdapterTransitionValidationFailure TransitionFailure,
        PerformanceFanValidationFailure Failure,
        bool Success)
    {
        internal static SemanticResult From(
            PerformanceFanSetterValidationResult result,
            bool postRestorationPadAbsent)
        {
            HyperBoostAdapterTransitionValidationResult? transition =
                result.AdapterTransitionValidation;
            return new(
                result.BaselineBalancedConfirmed,
                result.FullPowerAdapterConfirmed,
                result.CoolingPadAbsentConfirmed,
                transition?.TransitionPromptConfirmed == true,
                transition?.FreshAdapterReadCompleted == true,
                transition?.InitialAdapterState ?? AdapterPowerState.Unknown,
                transition?.ObservedAdapterState ?? AdapterPowerState.Unknown,
                transition?.TransitionConfirmed == true,
                result.CandidateConfirmed,
                result.Restored,
                result.IndeterminatePartialWrite,
                result.ManualRecoveryRequired,
                transition?.Failure
                    ?? HyperBoostAdapterTransitionValidationFailure.UnexpectedFailure,
                result.Failure,
                result.Success
                    && transition?.Success == true
                    && postRestorationPadAbsent);
        }
    }
}
