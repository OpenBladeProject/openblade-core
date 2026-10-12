using System.Text.Json;

namespace OpenBlade.Capture;

internal sealed class CoolingPadFanExploratoryTrialCommand(
    Func<Func<CancellationToken, Task<bool>>, Func<CancellationToken, Task<bool>>,
        Func<CancellationToken, Task<bool>>, CancellationToken,
        Task<CoolingPadFanExploratoryTrialResult>> run,
    TextReader input, TextWriter output, TextWriter prompts,
    Func<Action, IDisposable>? registerCancellation = null)
{
    internal const string Name = "validate-rz09-0528-cooling-pad-fan-exploratory-trial";
    internal const string ExactTargetConfirmation = "RZ09-05289EN4:1532:0F43:0200:Medium2200-Auto";
    internal const string ExactRecoveryConfirmation = "ManualPowerCycleAndVendorRecovery";
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 4
            || arguments[0] != "--confirm-target" || arguments[1] != ExactTargetConfirmation
            || arguments[2] != "--confirm-recovery" || arguments[3] != ExactRecoveryConfirmation)
        {
            await prompts.WriteLineAsync($"Usage: OpenBlade.Capture {Name} --confirm-target {ExactTargetConfirmation} --confirm-recovery {ExactRecoveryConfirmation}").ConfigureAwait(false);
            return 1;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = (registerCancellation ?? ConsoleCancellation.Register)(
            () => ConsoleCancellation.Cancel(cancellation));
        // The delegate constructs the native runner only after exact argument
        // admission. This command never performs manual or vendor recovery.
        var result = await run(
            token => ConfirmAsync("FIXED_CONFIRM",
                "Confirm the fan responded to vendor Medium 2200, with normal green lighting and no red sleep.", token),
            token => ConfirmAsync("AUTO_CONFIRM",
                "Confirm the fan returned to the quiet physical baseline, with normal green lighting and no red sleep.", token),
            token => ConfirmAsync("FINAL_CONFIRM",
                "Confirm the fan and full green lighting remain normal and unchanged, with no red sleep.", token),
            cancellation.Token).ConfigureAwait(false);

        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            command = Name,
            device = new
            {
                model = "RZ09-0528",
                sku = "RZ09-05289EN4",
                vendorId = "1532",
                productId = "0F43",
                revision = "0200",
                usagePage = "000C",
                usage = "0001",
                featureReportByteLength = 91,
                reportPrefix = "00",
            },
            biosIsCaptureProvenanceOnly = true,
            readOnly = false,
            exploratory = true,
            productionAdmitted = false,
            fanStateConfirmed = false,
            ownershipConfirmed = false,
            handbackConfirmed = false,
            recoveryAcknowledgement = ExactRecoveryConfirmation,
            recoveryPerformedByCommand = false,
            automaticRecovery = false,
            capturedSequence = new[]
            {
                "00/84:0000", "00/04:0300", "00/84:0000", "0D/10:01012C",
                "0D/10:000600", "00/04:0000", "00/84:0000",
            },
            result,
        }, JsonOptions)).ConfigureAwait(false);
        return result.Success ? 0 : result.Failure == "Canceled" ? 4 : 2;
    }

    private async Task<bool> ConfirmAsync(string stage, string observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await prompts.WriteLineAsync($"{stage}: {observation}").ConfigureAwait(false);
        await prompts.WriteLineAsync("Manual recovery may be needed; no automatic restart.").ConfigureAwait(false);
        await prompts.WriteLineAsync("Type YES only if confirmed; any other response or EOF stops further writes.").ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(response?.Trim(), "YES", StringComparison.Ordinal);
    }
}
