using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Service;

namespace OpenBlade.Capture;

internal static class CoolingPadServiceCurveTrialCommand
{
    internal const string Name = "validate-rz09-0528-cooling-pad-service-curve";
    internal const string Target = "RZ09-05289EN4:1532:0F43:0200:ServiceCurve1000-2200-Medium-PreferredQuiet";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static async Task<int> RunAsync(string[] args,
        Func<Func<string, CancellationToken, Task<bool>>, CancellationToken, Task<CoolingPadServiceCurveTrialResult>> run,
        Func<string, CancellationToken, Task<bool>> confirm, TextWriter output, CancellationToken token,
        TimeSpan? initialTimeout = null)
    {
        bool invoked = false, initialConfirmed = false;
        CoolingPadServiceCurveTrialResult? result = null;
        string? failure = null;
        try
        {
            if (args.Length != 4 || args[0] != "--confirm-target" || args[1] != Target
                || args[2] != "--confirm-recovery" || args[3] != "ManualPowerCycleOnly")
            { failure = "ExactTargetAndRecoveryAcknowledgmentRequired"; }
            else
            {
                initialConfirmed = await CoolingPadDeveloperObservation.ConfirmAsync(confirm,
                    "initialPhysicalConfirmation", token, initialTimeout ?? TimeSpan.FromMinutes(10), TimeProvider.System).ConfigureAwait(false);
                if (!initialConfirmed) { failure = "InitialOperatorRejected"; }
                else { invoked = true; result = await run(confirm, token).ConfigureAwait(false); }
            }
        }
        catch (Exception exception)
        {
            failure = exception is OperationCanceledException ? "Canceled" : exception is TimeoutException ? "OperatorTimeout" : "RunnerResultUnavailable";
        }
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            trial = Name,
            target = Target,
            developerOnly = true,
            sameServiceCoordinator = true,
            productionAdmission = false,
            outerServiceAdmissionExercised = false,
            hyperBoostInactiveEvidence = "Operator-attended confirmation; must remain inactive throughout",
            measuredRpmReadbackSupported = false,
            firmwareAutoRegulationProven = false,
            thermalTrackingProven = false,
            rawControlStateIsFanState = false,
            illustrativeOpenBladePolicy = CoolingPadServiceCurveTrialRunner.Settings,
            phaseSampleCount = 30,
            totalMaintenanceSamples = 60,
            sampleIntervalSeconds = 2,
            operatorWaitSeconds = 600,
            inactiveGpuHeuristicAcceptedAsTrustworthy = false,
            sensorProvenance = "CPU EC/TZRZ; active GPU NVML; read-duration is host observation, not source-origin age",
            recoveryPolicy = "ManualPowerCycleOnly; does not guarantee quiet behavior",
            exchangeCountEvidence = "Inferred from typed completed operations and reviewed source, not a packet trace; acquire includes its internal query plus coordinator verification",
            initialConfirmed,
            runnerInvoked = invoked,
            runnerResultUnavailable = invoked && result is null,
            submissionStateUnknown = invoked && result is null || result?.SubmissionStateUnknown == true,
            manualAttentionRequired = invoked && result is null || result?.ManualAttentionRequired == true,
            failure,
            result,
        }, Json)).ConfigureAwait(false);
        return result?.Completed == true ? 0 : token.IsCancellationRequested ? 4 : 2;
    }

    internal static async Task<bool> ConfirmConsoleAsync(string stage, CancellationToken token)
    {
        string prompt = stage switch
        {
            "initialPhysicalConfirmation" => "Confirm fresh preferred quiet fan behavior, normal green lighting, no red/sleep indication, and HyperBoost inactive. Manual power cycling does not guarantee quiet recovery.",
            "warmLoadReady" => "Start your chosen normal workload yourself, then confirm ready for the warm phase. This program starts no workload.",
            "firstDemandPhysicalConfirmation" => "Confirm normal fan and green lighting after the first configured demand, no red/sleep indication. This is perception, not measured RPM. The last demand remains held during this wait.",
            "coolPhaseReady" => "Warm phase complete. Stop or reduce your workload yourself, then confirm ready for the cool phase. The last demand remains held while waiting.",
            "intervalPhysicalConfirmation" => "Confirm normal fan and green lighting throughout both intervals, no red/sleep indication; proceed to explicit Medium then preferred quiet restoration.",
            "mediumPhysicalConfirmation" => "Confirm normal Medium fan behavior and unchanged green lighting, no red/sleep indication.",
            "quietPhysicalConfirmation" => "Confirm preferred quiet fan behavior and unchanged green lighting, no red/sleep indication.",
            "finalPhysicalConfirmation" => "After raw0000 verification, confirm preferred quiet fan behavior and unchanged green lighting, no red/sleep indication. Raw0000 is not a fan-state getter.",
            _ => throw new InvalidOperationException("UnknownPhysicalStage"),
        };
        await Console.Error.WriteLineAsync($"{stage}: {prompt} HyperBoost must remain inactive throughout; confirm it is still inactive. Type YES within 10 minutes to continue; any other response stops further writes.").ConfigureAwait(false);
        return string.Equals(await Console.In.ReadLineAsync(token).ConfigureAwait(false), "YES", StringComparison.Ordinal);
    }
}
