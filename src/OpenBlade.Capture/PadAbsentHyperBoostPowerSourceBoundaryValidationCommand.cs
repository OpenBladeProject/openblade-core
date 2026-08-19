using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed class PadAbsentHyperBoostPowerSourceBoundaryValidationCommand
{
    internal const string UsbCTargetConfirmation =
        "RZ09-0581:1532:02E0:4.00:PAD-ABSENT:HYPERBOOST-AUTO-EXIT:USB-C-100W";
    internal const string BatteryTargetConfirmation =
        "RZ09-0581:1532:02E0:4.00:PAD-ABSENT:HYPERBOOST-AUTO-EXIT:BATTERY";
    internal const string InitialApproval =
        "RZ09-0581:BIOS-4.00:PAD-PHYSICALLY-ABSENT:ADMITTED-FULL-POWER:" +
        "BALANCED-AUTO:APPLY-HYPERBOOST-ONCE:OBSERVE-FIRMWARE-AUTO-EXIT";
    internal const string ReconnectApproval =
        "RZ09-0581:OBSERVATION-COMPLETE:RECONNECTED-ADMITTED-FULL-POWER:" +
        "ALLOW-ONE-BALANCED-CLEANUP-IF-REQUIRED";
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

    private readonly ReviewedHyperBoostPowerSourceBoundaryPlan plan;
    private readonly AdapterPowerState destinationAdapterState;
    private readonly string targetConfirmation;
    private readonly string transitionApproval;
    private readonly Func<CoolingPadPhysicalPresenceObservation> observePad;
    private readonly Func<
        AdapterPowerState,
        Func<CancellationToken, Task<HyperBoostPowerSourceBoundaryAuthorization>>,
        Func<CancellationToken, Task<HyperBoostPowerSourceBoundaryAuthorization>>,
        CancellationToken,
        Task<PerformanceFanSetterValidationResult>> run;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly Func<Action, IDisposable> registerCancellation;

    internal PadAbsentHyperBoostPowerSourceBoundaryValidationCommand(
        ReviewedHyperBoostPowerSourceBoundaryPlan plan,
        AdapterPowerState destinationAdapterState,
        Func<CoolingPadPhysicalPresenceObservation> observePad,
        Func<
            AdapterPowerState,
            Func<CancellationToken, Task<HyperBoostPowerSourceBoundaryAuthorization>>,
            Func<CancellationToken, Task<HyperBoostPowerSourceBoundaryAuthorization>>,
            CancellationToken,
            Task<PerformanceFanSetterValidationResult>> run,
        TextReader input,
        TextWriter output,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<Action, IDisposable>? registerCancellation = null)
    {
        this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (!plan.DestinationAdapterStates.Contains(destinationAdapterState))
        {
            throw new ArgumentOutOfRangeException(nameof(destinationAdapterState));
        }

        this.destinationAdapterState = destinationAdapterState;
        targetConfirmation = destinationAdapterState switch
        {
            AdapterPowerState.UsbCPower100W => UsbCTargetConfirmation,
            AdapterPowerState.Battery => BatteryTargetConfirmation,
            _ => throw new ArgumentOutOfRangeException(nameof(destinationAdapterState)),
        };
        transitionApproval = TransitionApprovalFor(destinationAdapterState);
        this.observePad = observePad ?? throw new ArgumentNullException(nameof(observePad));
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        this.registerCancellation = registerCancellation ?? ConsoleCancellation.Register;
    }

    internal string TransitionApproval => transitionApproval;

    internal static string TransitionApprovalFor(
        AdapterPowerState destinationAdapterState) => destinationAdapterState switch
        {
            AdapterPowerState.UsbCPower100W =>
                "RZ09-0581:MODE-7-CONFIRMED:SWITCHED-TO-USB-C-100W:" +
                "OBSERVE-WITHOUT-OPENBLADE-DISABLE",
            AdapterPowerState.Battery =>
                "RZ09-0581:MODE-7-CONFIRMED:SWITCHED-TO-BATTERY:" +
                "OBSERVE-WITHOUT-OPENBLADE-DISABLE",
            _ => throw new ArgumentOutOfRangeException(nameof(destinationAdapterState)),
        };

    internal async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 2
            || !arguments[0].Equals("--confirm-target", StringComparison.Ordinal)
            || !arguments[1].Equals(targetConfirmation, StringComparison.Ordinal))
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture <power-source-boundary-command> " +
                $"--confirm-target {targetConfirmation}").ConfigureAwait(false);
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
                "WARNING: Developer-only firmware power-source boundary validation. " +
                "OpenBlade will not issue a HyperBoost disable during the observation window.")
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                $"Destination={destinationAdapterState} Plan={plan.PlanId} " +
                $"SHA256={plan.SourceSha256}").ConfigureAwait(false);
            await output.WriteLineAsync(
                "Start on an admitted full-power adapter in Balanced with Automatic " +
                "laptop fans. Type this exact phrase:").ConfigureAwait(false);
            await output.WriteLineAsync(InitialApproval).ConfigureAwait(false);
            string? approval = await input.ReadLineAsync(commandCancellation.Token)
                .ConfigureAwait(false);
            if (!string.Equals(approval, InitialApproval, StringComparison.Ordinal))
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(
                    new ApprovalDocument(
                        SchemaVersion: 1,
                        ProductionBehaviorChanged: false,
                        PlanId: plan.PlanId,
                        PlanSha256: plan.SourceSha256,
                        DestinationAdapterState: destinationAdapterState,
                        InitialApprovalConfirmed: false),
                    JsonOptions)).ConfigureAwait(false);
                return ApprovalRejectedExitCode;
            }

            PerformanceFanSetterValidationResult result = await run(
                destinationAdapterState,
                AuthorizeTransitionAsync,
                AuthorizeReconnectAsync,
                commandCancellation.Token).ConfigureAwait(false);
            bool postRunPadAbsent =
                observePad() == CoolingPadPhysicalPresenceObservation.Absent;
            await output.WriteLineAsync(JsonSerializer.Serialize(
                CaptureDocument.From(plan, result, postRunPadAbsent),
                JsonOptions)).ConfigureAwait(false);
            return IsSuccessfulResult(result, postRunPadAbsent)
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

    private async Task<HyperBoostPowerSourceBoundaryAuthorization>
        AuthorizeTransitionAsync(CancellationToken cancellationToken)
    {
        string instruction = destinationAdapterState switch
        {
            AdapterPowerState.UsbCPower100W =>
                "Connect the reviewed USB-C 100 W source and disconnect the full-power adapter.",
            AdapterPowerState.Battery =>
                "Disconnect every external power source so the laptop is on battery.",
            _ => throw new InvalidOperationException(),
        };
        await output.WriteLineAsync(
            $"Paired mode-7 readback is confirmed. {instruction} " +
            "OpenBlade will observe adapter and paired thermal state only. Type:")
            .ConfigureAwait(false);
        await output.WriteLineAsync(transitionApproval).ConfigureAwait(false);
        string? approval = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        return new(
            string.Equals(approval, transitionApproval, StringComparison.Ordinal),
            observePad());
    }

    private async Task<HyperBoostPowerSourceBoundaryAuthorization>
        AuthorizeReconnectAsync(CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(
            "Observation is complete. Reconnect the admitted full-power adapter. " +
            "No cleanup write can occur before a fresh full-power query. Type:")
            .ConfigureAwait(false);
        await output.WriteLineAsync(ReconnectApproval).ConfigureAwait(false);
        string? approval = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        return new(
            string.Equals(approval, ReconnectApproval, StringComparison.Ordinal),
            observePad());
    }

    private static bool IsSuccessfulResult(
        PerformanceFanSetterValidationResult result,
        bool postRunPadAbsent) =>
        result.Target == PerformanceFanValidationTarget.HyperBoost
        && result.Success
        && result.BaselineBalancedConfirmed
        && result.FullPowerAdapterConfirmed
        && result.CoolingPadAbsentConfirmed
        && result.PowerSourceBoundaryValidation is
        {
            ObservationSucceeded: true,
            FullPowerReconnectConfirmed: true,
        }
        && result.Restored
        && postRunPadAbsent
        && !result.IndeterminatePartialWrite
        && !result.ManualRecoveryRequired;

    private Task WritePreflightFailureAsync(
        CoolingPadPhysicalPresenceObservation first,
        CoolingPadPhysicalPresenceObservation? second) => output.WriteLineAsync(
            JsonSerializer.Serialize(
                new PreflightFailureDocument(
                    SchemaVersion: 1,
                    ProductionBehaviorChanged: false,
                    PlanId: plan.PlanId,
                    PlanSha256: plan.SourceSha256,
                    destinationAdapterState,
                    first,
                    second,
                    ValidationAttempted: false),
                JsonOptions));

    private sealed record ApprovalDocument(
        int SchemaVersion,
        bool ProductionBehaviorChanged,
        string PlanId,
        string PlanSha256,
        AdapterPowerState DestinationAdapterState,
        bool InitialApprovalConfirmed);

    private sealed record PreflightFailureDocument(
        int SchemaVersion,
        bool ProductionBehaviorChanged,
        string PlanId,
        string PlanSha256,
        AdapterPowerState DestinationAdapterState,
        CoolingPadPhysicalPresenceObservation FirstObservation,
        CoolingPadPhysicalPresenceObservation? SecondObservation,
        bool ValidationAttempted);

    private sealed record CaptureDocument(
        int SchemaVersion,
        bool ProductionBehaviorChanged,
        bool HyperBoostPowerTransitionPolicyChanged,
        string PlanId,
        string PlanSha256,
        bool FirstAbsenceConfirmed,
        bool SecondAbsenceConfirmed,
        bool InitialApprovalConfirmed,
        bool PostRunPadAbsentConfirmed,
        SemanticResult Result)
    {
        internal static CaptureDocument From(
            ReviewedHyperBoostPowerSourceBoundaryPlan plan,
            PerformanceFanSetterValidationResult result,
            bool postRunPadAbsent) => new(
                SchemaVersion: 1,
                ProductionBehaviorChanged: false,
                HyperBoostPowerTransitionPolicyChanged: false,
                plan.PlanId,
                plan.SourceSha256,
                FirstAbsenceConfirmed: true,
                SecondAbsenceConfirmed: true,
                InitialApprovalConfirmed: true,
                postRunPadAbsent,
                SemanticResult.From(result, postRunPadAbsent));
    }

    private sealed record SemanticResult(
        bool BaselineBalancedConfirmed,
        bool InitialFullPowerAdapterConfirmed,
        bool PreApplyPadAbsenceConfirmed,
        AdapterPowerState DestinationAdapterState,
        bool DestinationAdapterObserved,
        bool DestinationAdapterMaintained,
        bool CompletedFullObservation,
        bool FirmwareAutoExitConfirmed,
        ThermalModeState? FirmwareExitState,
        bool HyperBoostReappearedAfterExit,
        bool OpenBladeDisableIssuedDuringObservation,
        int RequiredConsecutiveAutomaticSamples,
        int CompletedSampleCount,
        IReadOnlyList<HyperBoostPowerSourceBoundarySample> Samples,
        bool ReconnectPromptConfirmed,
        bool ReconnectTimePadAbsentConfirmed,
        bool FullPowerReconnectConfirmed,
        AdapterPowerState ReconnectedAdapterState,
        bool BaselineReturnedWithoutCleanupWrite,
        int OpenBladeCleanupWriteCount,
        bool FinalBalancedRestorationConfirmed,
        bool PostRunPadAbsentConfirmed,
        bool IndeterminatePartialWrite,
        bool ManualRecoveryRequired,
        HyperBoostPowerSourceBoundaryValidationFailure BoundaryFailure,
        PerformanceFanValidationFailure Failure,
        bool Success)
    {
        internal static SemanticResult From(
            PerformanceFanSetterValidationResult result,
            bool postRunPadAbsent)
        {
            HyperBoostPowerSourceBoundaryValidationResult? boundary =
                result.PowerSourceBoundaryValidation;
            return new(
                result.BaselineBalancedConfirmed,
                result.FullPowerAdapterConfirmed,
                result.CoolingPadAbsentConfirmed,
                boundary?.DestinationAdapterState ?? AdapterPowerState.Unknown,
                boundary?.DestinationAdapterObserved == true,
                boundary?.DestinationAdapterMaintained == true,
                boundary?.CompletedFullObservation == true,
                boundary?.FirmwareAutoExitConfirmed == true,
                boundary?.FirmwareExitState,
                boundary?.HyperBoostReappearedAfterExit == true,
                boundary?.OpenBladeDisableIssuedDuringObservation == true,
                boundary?.RequiredConsecutiveAutomaticSamples ?? 0,
                boundary?.Samples.Count ?? 0,
                boundary?.Samples ?? [],
                boundary?.ReconnectPromptConfirmed == true,
                boundary?.ReconnectTimePadAbsentConfirmed == true,
                boundary?.FullPowerReconnectConfirmed == true,
                boundary?.ReconnectedAdapterState ?? AdapterPowerState.Unknown,
                boundary?.BaselineReturnedWithoutCleanupWrite == true,
                boundary?.OpenBladeCleanupWriteCount ?? 0,
                result.Restored,
                postRunPadAbsent,
                result.IndeterminatePartialWrite,
                result.ManualRecoveryRequired,
                boundary?.Failure
                    ?? HyperBoostPowerSourceBoundaryValidationFailure.UnexpectedFailure,
                result.Failure,
                result.Success
                    && boundary?.ObservationSucceeded == true
                    && postRunPadAbsent);
        }
    }
}
