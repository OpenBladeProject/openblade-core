using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Service;

namespace OpenBlade.Capture;

internal static class CoolingPadCounterValidationCommand
{
    internal const string Name = "validate-rz09-0528-cooling-pad-counter-reuse";
    internal const string Target = "RZ09-05289EN4:1532:0F43:0200:CounterReuse1000-2200-Medium-PreferredQuiet";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static async Task<int> RunAsync(string[] args,
        Func<Func<string, CancellationToken, Task<bool>>, CancellationToken, Task<CoolingPadCounterValidationResult>> run,
        Func<string, CancellationToken, Task<bool>> confirm, TextWriter output, CancellationToken token,
        TimeSpan? initialTimeout = null)
    {
        bool invoked = false, initialConfirmed = false;
        CoolingPadCounterValidationResult? result = null;
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
            transactionPolicy = "Developer-only sequential 01..1E,00..1E; fixed capacity, no retries",
            ordinaryAllocatorUnchanged = true,
            firstConfiguredDemandRpm = 2200,
            alternatingUpdates = 33,
            updateIntervalSeconds = 2,
            operatorWaitSeconds = 600,
            reservedRestorationOperations = 4,
            recoveryPolicy = "ManualPowerCycleOnly; does not guarantee quiet behavior",
            exchangeCountEvidence = "Actual actor-assigned IDs and validated echoed responses; not an independent packet trace",
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
            "initialPhysicalConfirmation" => "Confirm fresh preferred quiet fan behavior, normal green lighting, no red/sleep indication, and HyperBoost inactive. Confirm the installed OpenBlade service and all other controllers are stopped and remain stopped throughout this attended developer trial. Manual power cycling does not guarantee quiet recovery.",
            "firstDemandPhysicalConfirmation" => "Confirm normal fan and green lighting at the first configured 2200 demand, no red/sleep indication. This is perception, not measured RPM. Next: 33 alternating configured 1000/2200 updates every two seconds including repeated wire IDs; watch throughout and cancel immediately on abnormal behavior.",
            "counterIntervalPhysicalConfirmation" => "Confirm normal fan behavior and unchanged green lighting throughout the repeated-ID interval, no red/sleep indication; explicitly proceed to Medium then preferred quiet restoration.",
            "mediumPhysicalConfirmation" => "Confirm normal Medium fan behavior and unchanged green lighting, no red/sleep indication.",
            "quietPhysicalConfirmation" => "Confirm preferred quiet fan behavior and unchanged green lighting, no red/sleep indication.",
            "finalPhysicalConfirmation" => "After raw0000 verification, confirm preferred quiet fan behavior and unchanged green lighting, no red/sleep indication. Raw0000 is not a fan-state getter.",
            _ => throw new InvalidOperationException("UnknownPhysicalStage"),
        };
        await Console.Error.WriteLineAsync($"{stage}: {prompt} HyperBoost must remain inactive throughout; confirm it is still inactive. Type YES within 10 minutes to continue; any other response stops further writes.").ConfigureAwait(false);
        return string.Equals(await Console.In.ReadLineAsync(token).ConfigureAwait(false), "YES", StringComparison.Ordinal);
    }
}
