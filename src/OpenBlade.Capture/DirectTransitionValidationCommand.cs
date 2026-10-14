using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Capture;

// Developer-only named scenarios; no arbitrary command, value, or device path.
internal sealed class DirectTransitionValidationCommand(
    string commandName,
    IReadOnlySet<string> scenarios,
    string biosVersion,
    Func<string, CancellationToken, Task<(object Result, bool Success)>> run,
    TextWriter output,
    Func<Action, IDisposable>? registerCancellation = null)
{
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
        string confirmation = $"RZ09-0581:1532:02E0:{biosVersion}";
        if (arguments.Count != 3 || !scenarios.Contains(arguments[0])
            || arguments[1] != "--confirm-target" || arguments[2] != confirmation)
        {
            await output.WriteLineAsync($"Usage: OpenBlade.Capture {commandName} "
                + $"<{string.Join('|', scenarios.Order(StringComparer.Ordinal))}> "
                + $"--confirm-target {confirmation}").ConfigureAwait(false);
            return 1;
        }

        using CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = (registerCancellation ?? ConsoleCancellation.Register)(
            () => ConsoleCancellation.Cancel(cancellation));
        (object result, bool success) = await run(arguments[0], cancellation.Token)
            .ConfigureAwait(false);
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            device = new { model = "RZ09-0581", vendorId = "1532", productId = "02E0", biosVersion },
            readOnly = false,
            productionAdmitted = false,
            success,
            scenario = arguments[0],
            result,
        }, JsonOptions)).ConfigureAwait(false);
        return success ? 0 : 2;
    }
}
