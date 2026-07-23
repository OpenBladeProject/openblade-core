using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Capture;

internal sealed class DeviceModeValidationCommand(
    Func<Func<CancellationToken, Task<bool>>, CancellationToken,
        Task<DeviceModeValidationResult>> run,
    TextReader input,
    TextWriter output,
    Func<Action, IDisposable>? registerCancellation = null)
{
    internal const string ExactTargetConfirmation = "RZ09-0581:1532:02E0:3.01";
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
        if (arguments.Count != 2
            || !arguments[0].Equals("--confirm-target", StringComparison.Ordinal)
            || !arguments[1].Equals(ExactTargetConfirmation, StringComparison.Ordinal))
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture validate-device-mode --confirm-target " +
                ExactTargetConfirmation).ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = (registerCancellation
            ?? RegisterConsoleCancellation)(() => Cancel(commandCancellation));
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
                        model = OpenBlade.Protocol.TargetBladeDevice.Model,
                        vendorId = "1532",
                        productId = "02E0",
                        biosVersion = OpenBlade.Protocol.TargetBladeDevice.BiosVersion,
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

    private static IDisposable RegisterConsoleCancellation(Action cancel)
    {
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancel();
        };
        Console.CancelKeyPress += handler;
        return new CancellationRegistration(handler);
    }

    private static void Cancel(CancellationTokenSource source)
    {
        try { source.Cancel(); } catch (ObjectDisposedException) { }
    }

    private sealed class CancellationRegistration(ConsoleCancelEventHandler handler) : IDisposable
    {
        private ConsoleCancelEventHandler? handler = handler;

        public void Dispose()
        {
            ConsoleCancelEventHandler? current = Interlocked.Exchange(ref handler, null);
            if (current is not null)
            {
                Console.CancelKeyPress -= current;
            }
        }
    }
}
