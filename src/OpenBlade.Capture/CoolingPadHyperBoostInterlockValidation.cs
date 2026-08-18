using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed class CoolingPadHyperBoostInterlockValidationCommand(
    Func<Func<CancellationToken, Task<bool>>, CancellationToken,
        Task<CoolingPadFanCrashArmResult>> acquirePadControl,
    Func<Func<CancellationToken, Task<bool>>,
        Func<CancellationToken, Task<bool>>, CancellationToken,
        Task<CoolingPadFanCrashRecoveryResult>> handbackPadControl,
    Func<PerformanceFanValidationTarget, CancellationToken,
        Task<PerformanceFanSetterValidationResult>> validateHyperBoost,
    TextReader input,
    TextWriter output)
{
    internal const string Confirmation =
        "RZ09-0581:4.00:1532:0F43:FAN-2200:HANDBACK:" +
        "HYPERBOOST:READBACK:BALANCED-RESTORE";
    internal const string Fixed2200Confirmation = "FAN-2200-CONFIRMED";
    internal const string Retained2200Confirmation = "FAN-2200-STILL-ACTIVE";
    internal const string ReleaseConfirmation = "FAN-RELEASE-CONFIRMED";
    internal const int UsageExitCode = 1;
    internal const int ValidationFailureExitCode = 2;
    internal const int ApprovalRejectedExitCode = 3;

    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);

    internal async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments is not ["--confirm-target", Confirmation])
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture validate-cooling-pad-fan-hyperboost-" +
                "interlock --confirm-target " + Confirmation).ConfigureAwait(false);
            return UsageExitCode;
        }

        await output.WriteLineAsync(
            "WARNING: This developer-only route acquires Fixed 2200, hands pad " +
            "control back once, then runs the existing guarded HyperBoost " +
            "apply/readback/Balanced-restoration validation. No retry is allowed.")
            .ConfigureAwait(false);
        await output.WriteLineAsync("Type:").ConfigureAwait(false);
        await output.WriteLineAsync(Confirmation).ConfigureAwait(false);
        string? approval = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(approval, Confirmation, StringComparison.Ordinal))
        {
            return ApprovalRejectedExitCode;
        }

        CoolingPadFanCrashArmResult acquire = await acquirePadControl(
                token => ConfirmAsync(Fixed2200Confirmation, token),
                cancellationToken)
            .ConfigureAwait(false);
        CoolingPadFanCrashRecoveryResult? handback = null;
        if (acquire.ApplyAcknowledged)
        {
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            try
            {
                handback = await handbackPadControl(
                        token => ConfirmAsync(Retained2200Confirmation, token),
                        token => ConfirmAsync(ReleaseConfirmation, token),
                        cleanup.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                handback = new CoolingPadFanCrashRecoveryResult(
                    false, false, false, false, false, false, true, false,
                    CoolingPadFanCrashFailure.UnexpectedFailure);
            }
        }

        PerformanceFanSetterValidationResult? hyperBoost = null;
        if (acquire.Success && handback?.Success == true)
        {
            hyperBoost = await validateHyperBoost(
                    PerformanceFanValidationTarget.HyperBoost,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        bool success = acquire.Success
            && handback?.Success == true
            && hyperBoost is
            {
                Target: PerformanceFanValidationTarget.HyperBoost,
                ApplyAcknowledged: true,
                CandidateConfirmed: true,
                RestoreAcknowledged: true,
                Restored: true,
                IndeterminatePartialWrite: false,
                ManualRecoveryRequired: false,
                Failure: PerformanceFanValidationFailure.None,
            };
        await output.WriteLineAsync(JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                device = new
                {
                    model = TargetBladeDevice.Model,
                    laptopVendorId = "1532",
                    laptopProductId = "02E0",
                    coolingPadProductId = "0F43",
                    coolingPadRevision = "0200",
                    biosVersion = "4.00",
                },
                productionAdmitted = false,
                hyperBoostInterlockValidated = success,
                activePadModeReadbackConfirmed = false,
                physicalPadRpmReadbackConfirmed = false,
                balancedIntermediaryObserved = false,
                retryAttempted = false,
                acquire,
                handback,
                hyperBoost,
                success,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            })).ConfigureAwait(false);
        if (acquire.ApplyAcknowledged && handback?.Success != true)
        {
            await output.WriteLineAsync(
                "Do not retry. Restore the Auto UI state through Synapse; if " +
                "the pad is flashing red or asleep, disconnect USB and 12 V " +
                "power before reconnecting.").ConfigureAwait(false);
        }
        return success ? 0 : ValidationFailureExitCode;
    }

    private async Task<bool> ConfirmAsync(
        string expected,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(
            $"Type {expected} after physical confirmation.").ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        return response?.Equals(expected, StringComparison.Ordinal) == true;
    }
}
