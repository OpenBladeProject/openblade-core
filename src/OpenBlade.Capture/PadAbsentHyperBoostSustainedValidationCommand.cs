using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public sealed class PadAbsentHyperBoostSustainedValidationCommand
{
    public const string ExactTargetConfirmation =
        "RZ09-0581:1532:02E0:4.00:PAD-ABSENT:SUSTAINED-NO-LOAD";
    public const string ReviewedLoadTargetConfirmation =
        "RZ09-0581:1532:02E0:4.00:PAD-ABSENT:REVIEWED-EXTERNAL-LOAD";
    public const int UsageExitCode = 1;
    public const int ValidationFailureExitCode = 2;
    public const int ApprovalRejectedExitCode = 3;
    public const int CanceledExitCode = 4;

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
    private readonly Func<PerformanceFanValidationTarget, CancellationToken,
        Task<PerformanceFanSetterValidationResult>> run;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly Func<Action, IDisposable> registerCancellation;

    internal PadAbsentHyperBoostSustainedValidationCommand(
        ReviewedHyperBoostThermalPlan plan,
        Func<CoolingPadPhysicalPresenceObservation> observePad,
        Func<PerformanceFanValidationTarget, CancellationToken,
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

    internal string InteractiveApproval => BuildInteractiveApproval(plan);

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        bool externalLoad = plan.WorkloadKind ==
            HyperBoostValidationWorkloadKind.ExternalReviewedLoad;
        string expectedTarget = externalLoad
            ? ReviewedLoadTargetConfirmation
            : ExactTargetConfirmation;
        bool argumentsValid = arguments.Count == (externalLoad ? 4 : 2)
            && arguments[0].Equals("--confirm-target", StringComparison.Ordinal)
            && arguments[1].Equals(expectedTarget, StringComparison.Ordinal);
        if (argumentsValid && externalLoad)
        {
            argumentsValid = arguments[2].Equals(
                    "--workload-pid",
                    StringComparison.Ordinal)
                && int.TryParse(arguments[3], out int processId)
                && processId > 0;
        }
        if (!argumentsValid)
        {
            await output.WriteLineAsync(
                externalLoad
                    ? "Usage: OpenBlade.Capture validate-rz09-0581-02e0-bios-4.00-" +
                        "hyperboost-pad-absent-reviewed-load --confirm-target " +
                        ReviewedLoadTargetConfirmation + " --workload-pid <pid>"
                    : "Usage: OpenBlade.Capture validate-rz09-0581-02e0-bios-4.00-" +
                        "hyperboost-pad-absent-sustained --confirm-target " +
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
                externalLoad
                    ? "WARNING: Developer-only pad-absent HyperBoost reviewed-load " +
                        "validation. OpenBlade observes but never launches or stops " +
                        "the exact-hash workload. Production support remains disabled."
                    : "WARNING: Developer-only pad-absent HyperBoost no-load dwell. " +
                        "Production support remains disabled.").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"Plan={plan.PlanId} SHA256={plan.SourceSha256}")
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                $"Duration={plan.DurationSeconds}s Cadence=" +
                $"{plan.SampleIntervalMilliseconds}ms CPU<={plan.MaximumCpuTemperatureCelsius}C " +
                $"GPU<={plan.MaximumGpuTemperatureCelsius}C GPU={plan.GpuSensorPolicy}")
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                "The cooling pad USB and external power must remain disconnected. " +
                (externalLoad
                    ? $"Keep the externally launched exact {plan.WorkloadName} process " +
                        "running for the complete bounded validation. "
                    : "Do not start a workload. ") +
                "Type this exact phrase while present:")
                .ConfigureAwait(false);
            await output.WriteLineAsync(InteractiveApproval).ConfigureAwait(false);
            string? approval = await input.ReadLineAsync(commandCancellation.Token)
                .ConfigureAwait(false);
            if (!string.Equals(approval, InteractiveApproval, StringComparison.Ordinal))
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(
                    new ApprovalDocument(
                        SchemaVersion: 2,
                        ProductionAdmitted: false,
                        PadlessProductionAdmitted: false,
                        PlanId: plan.PlanId,
                        PlanSha256: plan.SourceSha256,
                        FirstAbsenceConfirmed: true,
                        SecondAbsenceConfirmed: true,
                        InteractiveApprovalConfirmed: false),
                    JsonOptions)).ConfigureAwait(false);
                return ApprovalRejectedExitCode;
            }

            PerformanceFanSetterValidationResult result = await run(
                PerformanceFanValidationTarget.HyperBoost,
                commandCancellation.Token).ConfigureAwait(false);
            await output.WriteLineAsync(JsonSerializer.Serialize(
                CaptureDocument.From(plan, result),
                JsonOptions)).ConfigureAwait(false);
            return IsSuccessfulResult(result)
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

    internal static string BuildInteractiveApproval(
        ReviewedHyperBoostThermalPlan plan) =>
        $"RZ09-0581:BIOS-4.00:PAD-PHYSICALLY-ABSENT:" +
        (plan.WorkloadKind == HyperBoostValidationWorkloadKind.ExternalReviewedLoad
            ? $"EXTERNAL-LOAD-{plan.WorkloadName}:" +
                $"WORKLOAD-SHA256-{plan.WorkloadSha256}:"
            : "NO-LOAD:") +
        $"{plan.DurationSeconds}S:CPU-MAX-{plan.MaximumCpuTemperatureCelsius}C:" +
        $"GPU-MAX-{plan.MaximumGpuTemperatureCelsius}C:PLAN-{plan.PlanId}:" +
        $"SHA256-{plan.SourceSha256}:APPLY-MONITOR-RESTORE";

    private static bool IsSuccessfulResult(
        PerformanceFanSetterValidationResult result) =>
        result.Target == PerformanceFanValidationTarget.HyperBoost
        && result.Success
        && result.BaselineBalancedConfirmed
        && result.FullPowerAdapterConfirmed
        && result.CoolingPadAbsentConfirmed
        && result.SafetyFailure == PerformanceFanValidationSafetyFailure.None
        && result.ApplyAttempted
        && result.ApplyAcknowledged
        && result.CandidateConfirmed
        && result.SustainedValidation is { Success: true }
        && result.RestoreAttempted
        && result.RestoreAcknowledged
        && result.Restored
        && result.PostRestorationFullPowerAdapterConfirmed
        && result.PostRestorationCoolingPadAbsentConfirmed
        && result.PostRestorationSafetyFailure ==
            PerformanceFanValidationSafetyFailure.None
        && !result.IndeterminatePartialWrite
        && !result.ManualRecoveryRequired;

    private Task WritePreflightFailureAsync(
        CoolingPadPhysicalPresenceObservation first,
        CoolingPadPhysicalPresenceObservation? second) => output.WriteLineAsync(
        JsonSerializer.Serialize(
            new PreflightFailureDocument(
                SchemaVersion: 2,
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
        bool FirstAbsenceConfirmed,
        bool SecondAbsenceConfirmed,
        bool InteractiveApprovalConfirmed);

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
        bool FirstAbsenceConfirmed,
        bool SecondAbsenceConfirmed,
        bool InteractiveApprovalConfirmed,
        string PlanId,
        string PlanSha256,
        int DurationSeconds,
        int SampleIntervalMilliseconds,
        double MaximumCpuTemperatureCelsius,
        double MaximumGpuTemperatureCelsius,
        HyperBoostGpuSensorPolicy GpuSensorPolicy,
        HyperBoostValidationWorkloadKind WorkloadKind,
        string? WorkloadName,
        string? WorkloadSha256,
        SemanticResult Result)
    {
        internal static CaptureDocument From(
            ReviewedHyperBoostThermalPlan plan,
            PerformanceFanSetterValidationResult result) => new(
                SchemaVersion: 2,
                ProductionAdmitted: false,
                PadlessProductionAdmitted: false,
                FirstAbsenceConfirmed: true,
                SecondAbsenceConfirmed: true,
                InteractiveApprovalConfirmed: true,
                plan.PlanId,
                plan.SourceSha256,
                plan.DurationSeconds,
                plan.SampleIntervalMilliseconds,
                plan.MaximumCpuTemperatureCelsius,
                plan.MaximumGpuTemperatureCelsius,
                plan.GpuSensorPolicy,
                plan.WorkloadKind,
                plan.WorkloadName,
                plan.WorkloadSha256,
                SemanticResult.From(result));
    }

    private sealed record SemanticResult(
        bool BaselineBalancedConfirmed,
        bool FullPowerAdapterConfirmed,
        AdapterPowerState InitialAdapterState,
        bool PreApplyPadAbsenceConfirmed,
        bool ContinuousPadAbsenceConfirmed,
        bool PostRestorationPadAbsenceConfirmed,
        bool HyperBoostReadbackConfirmed,
        bool BalancedRestorationConfirmed,
        bool IndeterminatePartialWrite,
        bool ManualRecoveryRequired,
        PerformanceFanValidationSafetyFailure SafetyFailure,
        PerformanceFanValidationFailure Failure,
        SustainedResult? Sustained,
        bool Success)
    {
        internal static SemanticResult From(
            PerformanceFanSetterValidationResult result)
        {
            return new(
                result.BaselineBalancedConfirmed,
                result.FullPowerAdapterConfirmed,
                result.InitialAdapterPowerState,
                PreApplyPadAbsenceConfirmed: result.CoolingPadAbsentConfirmed,
                ContinuousPadAbsenceConfirmed:
                    result.SustainedValidation?.ContinuousPadAbsenceConfirmed
                        == true,
                PostRestorationPadAbsenceConfirmed:
                    result.PostRestorationCoolingPadAbsentConfirmed,
                HyperBoostReadbackConfirmed: result.CandidateConfirmed,
                BalancedRestorationConfirmed: result.Restored,
                result.IndeterminatePartialWrite,
                result.ManualRecoveryRequired,
                result.SafetyFailure,
                result.Failure,
                result.SustainedValidation is { } sustained
                    ? SustainedResult.From(sustained)
                    : null,
                IsSuccessfulResult(result));
        }
    }

    private sealed record SustainedResult(
        int PlannedSampleCount,
        int CompletedSampleCount,
        bool CompletedFullDuration,
        bool FullPowerAdapterMaintained,
        bool HyperBoostStateMaintained,
        bool RequiredSensorsMaintained,
        bool ExternalWorkloadMaintained,
        HyperBoostSustainedValidationFailure Failure,
        IReadOnlyList<Sample> Samples)
    {
        internal static SustainedResult From(
            HyperBoostSustainedValidationResult result) => new(
                result.PlannedSampleCount,
                result.CompletedSampleCount,
                result.CompletedFullDuration,
                result.FullPowerAdapterMaintained,
                result.HyperBoostStateMaintained,
                result.RequiredSensorsMaintained,
                result.ExternalWorkloadMaintained,
                result.Failure,
                result.Samples.Select(Sample.From).ToArray());
    }

    private sealed record Sample(
        int SampleNumber,
        double ElapsedSeconds,
        int CpuCelsius,
        int? GpuCelsius,
        bool GpuInactive,
        int Fan1Rpm,
        int Fan2Rpm)
    {
        internal static Sample From(HyperBoostThermalValidationSample sample) => new(
            sample.SampleNumber,
            sample.Elapsed.TotalSeconds,
            sample.CpuCelsius,
            sample.GpuCelsius,
            sample.GpuInactive,
            sample.Fan1Rpm,
            sample.Fan2Rpm);
    }
}
