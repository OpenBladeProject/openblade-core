using System.Text.Json;

namespace OpenBlade.Capture;

// Attended compound experiment; a configured demand is not measured RPM.
// Exact admission and fresh physical baseline precede native construction.
internal sealed class CoolingPadHostDemand2200TrialCommand(
    Func<Func<CancellationToken, Task<bool>>, Func<CancellationToken, Task<bool>>,
        Func<CancellationToken, Task<bool>>, Func<CancellationToken, Task<bool>>, CancellationToken,
        Task<CoolingPadHostDemand2200TrialResult>> run,
    TextReader input, TextWriter output, TextWriter prompts,
    Func<Action, IDisposable>? registerCancellation = null,
    TimeSpan? initialConfirmationTimeout = null)
{
    internal const string Name = "validate-rz09-0528-cooling-pad-host-demand-2200-trial";
    internal const string ExactTargetConfirmation = "RZ09-05289EN4:1532:0F43:0200:HostDemand2200-Medium-PreferredQuiet";
    internal const string ExactRecoveryConfirmation = "ManualPowerCycleOnly";
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count != 4 || arguments[0] != "--confirm-target" || arguments[1] != ExactTargetConfirmation
            || arguments[2] != "--confirm-recovery" || arguments[3] != ExactRecoveryConfirmation)
        {
            await prompts.WriteLineAsync($"Usage: OpenBlade.Capture {Name} --confirm-target {ExactTargetConfirmation} --confirm-recovery {ExactRecoveryConfirmation}").ConfigureAwait(false);
            return 1;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = (registerCancellation ?? ConsoleCancellation.Register)(() => ConsoleCancellation.Cancel(cancellation));
        bool initialConfirmed = false;
        bool runnerInvoked = false;
        bool runnerResultAvailable = false;
        var result = new CoolingPadHostDemand2200TrialResult();
        try
        {
            // Reuse the existing isolated, bounded physical-observer helper.
            // The native runner delegate is not invoked until this succeeds.
            initialConfirmed = await CoolingPadFanProcessContinuityCommand.ConfirmAsync(
                (_, token) => ConfirmAsync("INITIAL_QUIET_CONFIRM",
                    "Confirm the pad currently has your preferred quiet Auto behavior, full normal green lighting, no red or sleep indication, and HyperBoost is inactive. This is an attended host-demand experiment; its return to quiet is not guaranteed.", token),
                "initialQuiet", cancellation.Token, initialConfirmationTimeout).ConfigureAwait(false);
            if (!initialConfirmed)
            {
                result = result with { FailurePhase = "initialPhysicalConfirmation", Failure = "OperatorRejected" };
            }
            else
            {
                cancellation.Token.ThrowIfCancellationRequested();
                runnerInvoked = true;
                result = await run(
                    token => ConfirmAsync("HOST_DEMAND_CONFIRM",
                        "Confirm a normal physical fan response to configured 2200 demand, with full normal green lighting and no red or sleep. This is perceived response, not measured RPM. YES permits experimental Medium-to-quiet restoration.", token),
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
            purpose = "attended-single-host-demand-2200-compound-experiment",
            device = new { model = "RZ09-0528", sku = "RZ09-05289EN4", vendorId = "1532", productId = "0F43", revision = "0200", interfaceNumber = "00", usagePage = "000C", usage = "0001", featureReportByteLength = 91, reportPrefix = "00" },
            biosIsCaptureProvenanceOnly = true,
            initialPhysicalState = initialConfirmed ? "operator-confirmed-quiet-normal-green-no-red-sleep-HyperBoost-inactive" : "unconfirmed",
            requestedPhysicalTarget = "preferred-quiet-Auto-normal-green-no-red-sleep",
            initialPhysicalObservationConfirmed = initialConfirmed,
            nativeRunnerInvoked = runnerInvoked,
            runnerResultUnavailable = runnerInvoked && !runnerResultAvailable,
            submissionStateUnknown = runnerInvoked && !runnerResultAvailable || result.SubmissionStateUnknown,
            manualRecoveryRequired = runnerInvoked && !runnerResultAvailable || result.ManualRecoveryRequired,
            failurePhase = result.FailurePhase,
            failure = result.Failure,
            preferredAutoPhysicalConfirmed = result.AutoPhysicalConfirmed,
            preferredAutoFinalPhysicalConfirmed = result.FinalPhysicalConfirmed,
            trialSequenceCompleted = result.Success,
            configuredHostDemandRpm = 2200,
            hostDemandPhysicalResponseConfirmed = result.HostDemandPhysicalConfirmed,
            measuredRpmConfirmed = false,
            compoundRestorationPreviouslyValidated = false,
            manualPowerCycleGuaranteesQuiet = false,
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
                "00/84:0000", "00/04:0300", "00/84:0000", "0D/10:000600",
                "0D/01:01052C", "0D/10:01012C",
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
        await prompts.WriteLineAsync("Type YES only if confirmed; any other answer or EOF stops further writes. Manual power cycling may still be needed and does not guarantee quietness; no automatic or vendor recovery.").ConfigureAwait(false);
        return string.Equals((await input.ReadLineAsync(token).ConfigureAwait(false))?.Trim(), "YES", StringComparison.Ordinal);
    }
}
