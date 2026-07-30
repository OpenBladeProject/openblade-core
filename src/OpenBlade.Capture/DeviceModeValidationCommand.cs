using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record DeviceModeValidationTarget(
    string Confirmation,
    DeviceIdentity Device)
{
    internal static DeviceModeValidationTarget Rz090581 { get; } = new(
        DeviceModeValidationCommand.ExactTargetConfirmation,
        new DeviceIdentity("RZ09-0581", 0x1532, 0x02E0, "3.01"));

    internal static DeviceModeValidationTarget Rz090528 { get; } = new(
        DeviceModeValidationCommand.Rz090528ExactTargetConfirmation,
        new DeviceIdentity("RZ09-0528", 0x1532, 0x02C6, "2.02"));
}

internal sealed class DeviceModeValidationCommand(
    Func<Func<CancellationToken, Task<bool>>, CancellationToken,
        Task<DeviceModeValidationResult>> run,
    TextReader input,
    TextWriter output,
    Func<Action, IDisposable>? registerCancellation = null,
    DeviceModeValidationTarget? target = null)
{
    internal const string ExactTargetConfirmation = "RZ09-0581:1532:02E0:3.01";
    internal const string Rz090528ExactTargetConfirmation =
        "RZ09-0528:1532:02C6:2.02";
    internal const int UsageExitCode = 1;
    internal const int ValidationFailureExitCode = 2;
    internal const int CanceledExitCode = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        DeviceModeValidationTarget resolvedTarget =
            target ?? DeviceModeValidationTarget.Rz090581;
        if (arguments.Count != 2
            || !arguments[0].Equals("--confirm-target", StringComparison.Ordinal)
            || !arguments[1].Equals(
                resolvedTarget.Confirmation,
                StringComparison.Ordinal))
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture validate-device-mode --confirm-target " +
                resolvedTarget.Confirmation).ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = (registerCancellation
            ?? ConsoleCancellation.Register)(
                () => ConsoleCancellation.Cancel(commandCancellation));
        try
        {
            DeviceModeValidationResult result = await run(
                ConfirmVisualBehaviorAsync,
                commandCancellation.Token).ConfigureAwait(false);
            await output.WriteLineAsync(JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    device = new
                    {
                        model = resolvedTarget.Device.Model,
                        vendorId = resolvedTarget.Device.VendorId.ToString("X4"),
                        productId = resolvedTarget.Device.ProductId.ToString("X4"),
                        biosVersion = resolvedTarget.Device.BiosVersion,
                    },
                    readOnly = false,
                    productionAdmitted = false,
                    result,
                },
                JsonOptions)).ConfigureAwait(false);
            return result.Success
                ? 0
                : result.Failure == DeviceModeValidationFailure.Canceled
                    ? CanceledExitCode
                    : ValidationFailureExitCode;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await output.WriteLineAsync("{\"canceled\":true}").ConfigureAwait(false);
            return CanceledExitCode;
        }
    }

    private async Task<bool> ConfirmVisualBehaviorAsync(
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(
            "Driver mode is confirmed. The normal keyboard effect may be temporarily blank.")
            .ConfigureAwait(false);
        await output.WriteLineAsync(
            "Hold Fn and check the extended function layer. Type YES only if it behaves as expected.")
            .ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(response?.Trim(), "YES", StringComparison.Ordinal);
    }

}
