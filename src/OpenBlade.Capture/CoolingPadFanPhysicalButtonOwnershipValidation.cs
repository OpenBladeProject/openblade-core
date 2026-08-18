using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed class CoolingPadFanPhysicalButtonOwnershipValidationCommand(
    Func<Func<CancellationToken, Task<bool>>, CancellationToken,
        Task<CoolingPadFanCrashArmResult>> acquirePadControl,
    Func<Func<CancellationToken, Task<bool>>,
        Func<CancellationToken, Task<bool>>, CancellationToken,
        Task<CoolingPadFanCrashRecoveryResult>> handbackPadControl,
    TextReader input,
    TextWriter output)
{
    internal const string Confirmation =
        "RZ09-0581:4.00:1532:0F43:FAN-2200:" +
        "PHYSICAL-BUTTON-ONCE:RELEASE-ONCE";
    internal const string Fixed2200Confirmation = "FAN-2200-CONFIRMED";
    internal const string PressButtonConfirmation = "FAN-BUTTON-PRESSED-ONCE";
    internal const string ButtonIgnoredConfirmation =
        "FAN-BUTTON-IGNORED-2200-RETAINED";
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
                "Usage: OpenBlade.Capture validate-cooling-pad-fan-physical-" +
                "button-ownership --confirm-target " + Confirmation)
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        await output.WriteLineAsync(
            "WARNING: This developer-only route acquires Fixed 2200 once, asks " +
            "you to press the physical fan button once, and only issues one " +
            "release if 2200 remains physically active. No retry is allowed.")
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
        bool buttonPressed = false;
        bool buttonIgnored = false;
        CoolingPadFanCrashRecoveryResult? handback = null;
        if (acquire.Success)
        {
            buttonPressed = await ConfirmAsync(
                    PressButtonConfirmation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (buttonPressed)
            {
                buttonIgnored = await ConfirmAsync(
                        ButtonIgnoredConfirmation,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (acquire.ApplyAcknowledged && (!buttonPressed || buttonIgnored))
        {
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            try
            {
                handback = await handbackPadControl(
                        token => buttonIgnored
                            ? Task.FromResult(true)
                            : ConfirmAsync(Retained2200Confirmation, token),
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

        bool success = acquire.Success
            && buttonPressed
            && buttonIgnored
            && handback?.Success == true;
        await output.WriteLineAsync(JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                device = new
                {
                    model = TargetBladeDevice.Model,
                    productId = "0F43",
                    revision = "0200",
                    biosVersion = "4.00",
                },
                productionAdmitted = false,
                physicalButtonOwnershipPolicyValidated = success,
                activeModeReadbackConfirmed = false,
                physicalRpmReadbackConfirmed = false,
                buttonPressed,
                buttonIgnoredWhileHostOwned = buttonIgnored,
                retryAttempted = false,
                acquire,
                handback,
                success,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            })).ConfigureAwait(false);
        if (acquire.ApplyAcknowledged && handback?.Success != true)
        {
            await output.WriteLineAsync(
                "Do not retry. The physical button outcome did not preserve a " +
                "safe OpenBlade release boundary. Restore the Auto UI state " +
                "through Synapse; power-cycle USB and 12 V if the pad is asleep " +
                "or flashing red.").ConfigureAwait(false);
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
