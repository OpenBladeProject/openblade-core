using System.Text.Json;

namespace OpenBlade.Capture;

// Reuses the verified single-session sequence. Starting physical state and
// requested quiet Auto are separate observations; raw 0000 is not a fan getter.
internal sealed class CoolingPadPreferredAutoRecoveryCommand(
    Func<Func<CancellationToken, Task<bool>>, Func<CancellationToken, Task<bool>>,
        Func<CancellationToken, Task<bool>>, CancellationToken,
        Task<CoolingPadFanExploratoryTrialResult>> run,
    TextReader input, TextWriter output, TextWriter prompts,
    Func<Action, IDisposable>? registerCancellation = null,
    TimeSpan? initialConfirmationTimeout = null)
{
    internal const string Name = "recover-rz09-0528-cooling-pad-preferred-auto";
    internal const string ExactTargetConfirmation = "RZ09-05289EN4:1532:0F43:0200:CurrentMedium-ReassertPreferredQuietAuto";
    internal const string ExactHostDemand1500TargetConfirmation = "RZ09-05289EN4:1532:0F43:0200:CurrentHostDemand1500-ReassertPreferredQuietAuto";
    internal const string ExactRecoveryConfirmation = "ManualPowerCycleOnly";
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count != 4 || arguments[0] != "--confirm-target" || (arguments[1] != ExactTargetConfirmation && arguments[1] != ExactHostDemand1500TargetConfirmation)
            || arguments[2] != "--confirm-recovery" || arguments[3] != ExactRecoveryConfirmation)
        {
            await prompts.WriteLineAsync($"Usage: OpenBlade.Capture {Name} --confirm-target <{ExactTargetConfirmation}|{ExactHostDemand1500TargetConfirmation}> --confirm-recovery {ExactRecoveryConfirmation}").ConfigureAwait(false);
            return 1;
        }
        bool fromHostDemand1500 = arguments[1] == ExactHostDemand1500TargetConfirmation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = (registerCancellation ?? ConsoleCancellation.Register)(() => ConsoleCancellation.Cancel(cancellation));
        bool initialConfirmed = false;
        bool runnerInvoked = false;
        bool runnerResultAvailable = false;
        var result = new CoolingPadFanExploratoryTrialResult();
        try
        {
            // Reuse the existing isolated, bounded physical-observer helper.
            // The native runner delegate is not invoked until this succeeds.
            initialConfirmed = await CoolingPadFanProcessContinuityCommand.ConfirmAsync(
                (_, token) => ConfirmAsync(fromHostDemand1500 ? "INITIAL_CURRENT_FAN_CONFIRM" : "INITIAL_MEDIUM_CONFIRM",
                    fromHostDemand1500
                        ? "Following the recorded acknowledged configured 1500 demand and raw0000 recovery, confirm the current fan behaves normally with full normal green lighting, no red sleep and HyperBoost inactive. This history does not measure current RPM or establish Medium. The requested destination is preferred quiet Auto; the captured sequence first reasserts Medium."
                        : "Confirm the pad currently runs Medium with full normal green lighting and no red sleep. The requested destination is your preferred quiet Auto; this command will reassert the captured Medium-to-Auto sequence.", token),
                fromHostDemand1500 ? "initialCurrentFan" : "initialMedium", cancellation.Token, initialConfirmationTimeout).ConfigureAwait(false);
            if (!initialConfirmed)
            {
                result = result with { FailurePhase = "initialPhysicalConfirmation", Failure = "OperatorRejected" };
            }
            else
            {
                cancellation.Token.ThrowIfCancellationRequested();
                runnerInvoked = true;
                result = await run(
                    token => ConfirmAsync("FIXED_CONFIRM",
                        "Confirm the reasserted Medium still behaves normally with full normal green lighting and no red sleep.", token),
                    token => ConfirmAsync("PREFERRED_AUTO_CONFIRM",
                        "Confirm the requested preferred quiet Auto has been achieved, with full normal green lighting and no red sleep.", token),
                    token => ConfirmAsync("FINAL_PREFERRED_AUTO_CONFIRM",
                        "Confirm preferred quiet Auto and full normal green lighting remain normal, with no red sleep.", token),
                    cancellation.Token).ConfigureAwait(false);
                runnerResultAvailable = true;
            }
        }
        catch (OperationCanceledException)
        { result = result with { FailurePhase = runnerInvoked ? "runnerResultUnavailable" : "initialPhysicalConfirmation", Failure = "Canceled" }; }
        catch (TimeoutException)
        { result = result with { FailurePhase = runnerInvoked ? "runnerResultUnavailable" : "initialPhysicalConfirmation", Failure = runnerInvoked ? "NativeIoFailure" : "OperatorTimeout" }; }
        catch
        { result = result with { FailurePhase = runnerInvoked ? "runnerResultUnavailable" : "initialPhysicalConfirmation", Failure = runnerInvoked ? "NativeIoFailure" : "OperatorException" }; }

        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            command = Name,
            purpose = "reassert-requested-preferred-Auto",
            device = new { model = "RZ09-0528", sku = "RZ09-05289EN4", vendorId = "1532", productId = "0F43", revision = "0200", interfaceNumber = "00", usagePage = "000C", usage = "0001", featureReportByteLength = 91, reportPrefix = "00" },
            biosIsCaptureProvenanceOnly = true,
            initialPhysicalState = initialConfirmed
                ? fromHostDemand1500 ? "operator-confirmed-normal-current-fan-green-no-red-sleep-HyperBoost-inactive"
                    : "operator-confirmed-Medium-normal-green-no-red-sleep"
                : "unconfirmed",
            previousConfiguredDemandRpm = fromHostDemand1500 ? (int?)1500 : null,
            previousConfiguredDemandIsFirmwareReadback = false,
            initialStateProvenance = "operator-confirmation-and-selected-recovery-history",
            measuredRpmConfirmed = false,
            requestedPhysicalTarget = "preferred-quiet-Auto-normal-green-no-red-sleep",
            initialPhysicalObservationConfirmed = initialConfirmed,
            nativeRunnerInvoked = runnerInvoked,
            runnerResultUnavailable = runnerInvoked && !runnerResultAvailable,
            submissionStateUnknown = runnerInvoked && !runnerResultAvailable
                || result.ModeAcquireAttempted && !result.ModeAcquireAcknowledged
                || result.FixedAttempted && !result.FixedAcknowledged
                || result.AutoAttempted && !result.AutoAcknowledged
                || result.ModeRestoreAttempted && !result.ModeRestoreAcknowledged,
            manualRecoveryRequired = runnerInvoked && !runnerResultAvailable || result.ManualRecoveryRequired,
            failurePhase = result.FailurePhase,
            failure = result.Failure,
            preferredAutoPhysicalConfirmed = result.AutoPhysicalConfirmed,
            preferredAutoFinalPhysicalConfirmed = result.FinalPhysicalConfirmed,
            requestedRecoverySequenceCompleted = result.Success,
            rawControlStateRestored = result.RawRestorationConfirmed,
            rawControlStateIsFanReadback = false,
            readOnly = false,
            exploratory = true,
            productionAdmitted = false,
            fanStateConfirmed = false,
            ownershipConfirmed = false,
            handbackConfirmed = false,
            automaticRecovery = false,
            recoveryAcknowledgement = ExactRecoveryConfirmation,
            capturedSequence = new[]
            {
                "00/84:0000", "00/04:0300", "00/84:0000", "0D/10:01012C",
                "0D/10:000600", "00/04:0000", "00/84:0000",
            },
            // An invoked delegate that returns no result provides no per-command
            // evidence. Do not serialize default attempt flags as negative facts.
            result = runnerInvoked && !runnerResultAvailable ? null : result,
        }, JsonOptions)).ConfigureAwait(false);
        return result.Success ? 0 : result.Failure == "Canceled" ? 4 : 2;
    }

    private async Task<bool> ConfirmAsync(string stage, string observation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await prompts.WriteLineAsync($"{stage}: {observation}").ConfigureAwait(false);
        await prompts.WriteLineAsync($"Operator confirmation wait is finite: {(initialConfirmationTimeout ?? TimeSpan.FromSeconds(180)).TotalMinutes} minutes.").ConfigureAwait(false);
        await prompts.WriteLineAsync("Type YES only if confirmed; any other answer or EOF stops further writes. Manual power cycling may still be needed; no automatic or vendor recovery.").ConfigureAwait(false);
        return string.Equals((await input.ReadLineAsync(token).ConfigureAwait(false))?.Trim(), "YES", StringComparison.Ordinal);
    }
}
