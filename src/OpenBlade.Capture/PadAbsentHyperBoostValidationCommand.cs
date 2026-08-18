using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public sealed class PadAbsentHyperBoostValidationCommand
{
    public const string ExactTargetConfirmation =
        "RZ09-0581:1532:02E0:4.00:PAD-ABSENT";
    public const string InteractiveApproval =
        "RZ09-0581:BIOS-4.00:PAD-PHYSICALLY-ABSENT:FULL-POWER:" +
        "BALANCED-AUTO:APPLY-ONCE:RESTORE";
    public const int UsageExitCode = 1;
    public const int ValidationFailureExitCode = 2;
    public const int ApprovalRejectedExitCode = 3;
    public const int CanceledExitCode = 4;

    private const string Usage =
        "Usage: OpenBlade.Capture " +
        "validate-rz09-0581-02e0-bios-4.00-hyperboost-pad-absent " +
        "--confirm-target " + ExactTargetConfirmation;
    private static readonly TimeSpan AbsenceSampleInterval =
        TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Func<CoolingPadPhysicalPresenceObservation> observePad;
    private readonly Func<PerformanceFanValidationTarget, CancellationToken,
        Task<PerformanceFanSetterValidationResult>> run;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly Func<Action, IDisposable> registerCancellation;

    internal PadAbsentHyperBoostValidationCommand(
        Func<CoolingPadPhysicalPresenceObservation> observePad,
        Func<PerformanceFanValidationTarget, CancellationToken,
            Task<PerformanceFanSetterValidationResult>> run,
        TextReader input,
        TextWriter output,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<Action, IDisposable>? registerCancellation = null)
    {
        this.observePad = observePad ?? throw new ArgumentNullException(nameof(observePad));
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        this.registerCancellation = registerCancellation ?? ConsoleCancellation.Register;
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 2
            || !arguments[0].Equals("--confirm-target", StringComparison.Ordinal)
            || !arguments[1].Equals(ExactTargetConfirmation, StringComparison.Ordinal))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
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
                "WARNING: Developer-only no-load validation. The cooling pad USB and " +
                "external power must both remain disconnected. Production support " +
                "will remain disabled.").ConfigureAwait(false);
            await output.WriteLineAsync(
                "Type this exact phrase while present at the laptop:").ConfigureAwait(false);
            await output.WriteLineAsync(InteractiveApproval).ConfigureAwait(false);
            string? approval = await input.ReadLineAsync(commandCancellation.Token)
                .ConfigureAwait(false);
            if (!string.Equals(approval, InteractiveApproval, StringComparison.Ordinal))
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(
                    new ApprovalDocument(
                        SchemaVersion: 1,
                        ProductionAdmitted: false,
                        PadlessProductionAdmitted: false,
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
                new CaptureDocument(
                    SchemaVersion: 1,
                    ProductionAdmitted: false,
                    PadlessProductionAdmitted: false,
                    FirstAbsenceConfirmed: true,
                    SecondAbsenceConfirmed: true,
                    InteractiveApprovalConfirmed: true,
                    Result: SemanticResult.From(result)),
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
        && result.RestoreAttempted
        && result.RestoreAcknowledged
        && result.Restored
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
                FirstObservation: first,
                SecondObservation: second,
                ValidationAttempted: false),
            JsonOptions));

    private sealed record CaptureDocument(
        int SchemaVersion,
        bool ProductionAdmitted,
        bool PadlessProductionAdmitted,
        bool FirstAbsenceConfirmed,
        bool SecondAbsenceConfirmed,
        bool InteractiveApprovalConfirmed,
        SemanticResult Result);

    private sealed record ApprovalDocument(
        int SchemaVersion,
        bool ProductionAdmitted,
        bool PadlessProductionAdmitted,
        bool FirstAbsenceConfirmed,
        bool SecondAbsenceConfirmed,
        bool InteractiveApprovalConfirmed);

    private sealed record PreflightFailureDocument(
        int SchemaVersion,
        bool ProductionAdmitted,
        bool PadlessProductionAdmitted,
        CoolingPadPhysicalPresenceObservation FirstObservation,
        CoolingPadPhysicalPresenceObservation? SecondObservation,
        bool ValidationAttempted);

    private sealed record SemanticResult(
        bool BaselineBalancedConfirmed,
        bool FullPowerAdapterConfirmed,
        AdapterPowerState InitialAdapterState,
        bool FinalPadAbsenceConfirmed,
        bool ApplyAttempted,
        bool ApplyAcknowledged,
        bool HyperBoostReadbackConfirmed,
        bool RestoreAttempted,
        bool RestoreAcknowledged,
        bool BalancedRestorationConfirmed,
        bool IndeterminatePartialWrite,
        bool ManualRecoveryRequired,
        PerformanceFanValidationSafetyFailure SafetyFailure,
        PerformanceFanValidationFailure Failure,
        bool Success)
    {
        internal static SemanticResult From(
            PerformanceFanSetterValidationResult result) => new(
            result.BaselineBalancedConfirmed,
            result.FullPowerAdapterConfirmed,
            result.InitialAdapterPowerState,
            FinalPadAbsenceConfirmed: result.CoolingPadAbsentConfirmed,
            result.ApplyAttempted,
            result.ApplyAcknowledged,
            HyperBoostReadbackConfirmed: result.CandidateConfirmed,
            result.RestoreAttempted,
            result.RestoreAcknowledged,
            BalancedRestorationConfirmed: result.Restored,
            result.IndeterminatePartialWrite,
            result.ManualRecoveryRequired,
            result.SafetyFailure,
            result.Failure,
            IsSuccessfulResult(result));
    }
}
