using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public sealed class PerformanceFanSetterValidationCommand
{
    public const string ExactTargetConfirmation = "RZ09-0581:1532:02E0:3.01";
    public const int UsageExitCode = 1;
    public const int ValidationFailureExitCode = 2;
    public const int CanceledExitCode = 4;

    private const string Usage =
        "Usage: OpenBlade.Capture validate-performance-fan " +
        "<balanced|silent|performance|fixed5400> --confirm-target " +
        ExactTargetConfirmation;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Func<PerformanceFanValidationTarget, CancellationToken,
        Task<PerformanceFanSetterValidationResult>> run;
    private readonly TextWriter output;
    private readonly Func<Action, IDisposable> registerCancellation;

    public PerformanceFanSetterValidationCommand(
        Func<PerformanceFanValidationTarget, CancellationToken,
            Task<PerformanceFanSetterValidationResult>> run,
        TextWriter output,
        Func<Action, IDisposable>? registerCancellation = null)
    {
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.registerCancellation = registerCancellation ?? RegisterConsoleCancellation;
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryParse(arguments, out PerformanceFanValidationTarget target))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellation(() => Cancel(commandCancellation));
        try
        {
            PerformanceFanSetterValidationResult result =
                await run(target, commandCancellation.Token).ConfigureAwait(false);
            await output.WriteLineAsync(JsonSerializer.Serialize(
                new CaptureDocument(
                    1,
                    new(
                        TargetBladeDevice.Model,
                        TargetBladeDevice.VendorId.ToString("X4"),
                        TargetBladeDevice.ProductId.ToString("X4"),
                        TargetBladeDevice.BiosVersion),
                    ReadOnly: false,
                    ProductionAdmitted: false,
                    result),
                JsonOptions)).ConfigureAwait(false);
            return result.Success
                ? 0
                : result.Failure == PerformanceFanValidationFailure.Canceled
                    ? CanceledExitCode
                    : ValidationFailureExitCode;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await output.WriteLineAsync("{\"canceled\":true}").ConfigureAwait(false);
            return CanceledExitCode;
        }
    }

    private static bool TryParse(
        IReadOnlyList<string> arguments,
        out PerformanceFanValidationTarget target)
    {
        target = default;
        if (arguments.Count != 3
            || !arguments[1].Equals("--confirm-target", StringComparison.Ordinal)
            || !arguments[2].Equals(ExactTargetConfirmation, StringComparison.Ordinal))
        {
            return false;
        }

        return arguments[0].ToLowerInvariant() switch
        {
            "balanced" => Assign(PerformanceFanValidationTarget.Balanced, out target),
            "silent" => Assign(PerformanceFanValidationTarget.Silent, out target),
            "performance" => Assign(PerformanceFanValidationTarget.Performance, out target),
            "fixed5400" => Assign(PerformanceFanValidationTarget.Fixed5400, out target),
            _ => false,
        };
    }

    private static bool Assign(
        PerformanceFanValidationTarget value,
        out PerformanceFanValidationTarget target)
    {
        target = value;
        return true;
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

    private sealed record CaptureDocument(
        int SchemaVersion,
        CaptureDevice Device,
        bool ReadOnly,
        bool ProductionAdmitted,
        PerformanceFanSetterValidationResult Result);

    private sealed record CaptureDevice(
        string Model,
        string VendorId,
        string ProductId,
        string BiosVersion);
}
