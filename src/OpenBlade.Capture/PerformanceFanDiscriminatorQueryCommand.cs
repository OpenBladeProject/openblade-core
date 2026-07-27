using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public sealed class PerformanceFanDiscriminatorQueryCommand
{
    public const int UsageExitCode = 1;
    public const int QueryFailureExitCode = 2;
    public const int CanceledExitCode = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Func<CancellationToken,
        Task<HidSessionResult<PerformanceFanDiscriminatorSnapshot>>> run;
    private readonly TextWriter output;
    private readonly Func<Action, IDisposable> registerCancellation;

    public PerformanceFanDiscriminatorQueryCommand(
        Func<CancellationToken,
            Task<HidSessionResult<PerformanceFanDiscriminatorSnapshot>>> run,
        TextWriter output,
        Func<Action, IDisposable>? registerCancellation = null)
    {
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.registerCancellation = registerCancellation ?? ConsoleCancellation.Register;
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 0)
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture query-performance-fan-discriminator")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellation(
            () => ConsoleCancellation.Cancel(commandCancellation));
        try
        {
            HidSessionResult<PerformanceFanDiscriminatorSnapshot> result =
                await run(commandCancellation.Token).ConfigureAwait(false);
            commandCancellation.Token.ThrowIfCancellationRequested();
            await output.WriteLineAsync(JsonSerializer.Serialize(
                CreateDocument(result),
                JsonOptions)).ConfigureAwait(false);
            return result.Failure is null && result.Value is not null
                ? 0
                : QueryFailureExitCode;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await output.WriteLineAsync("{\"canceled\":true}").ConfigureAwait(false);
            return CanceledExitCode;
        }
    }

    private static CaptureDocument CreateDocument(
        HidSessionResult<PerformanceFanDiscriminatorSnapshot> result) => new(
        SchemaVersion: 1,
        Device: new(
            TargetBladeDevice.Model,
            TargetBladeDevice.VendorId.ToString("X4"),
            TargetBladeDevice.ProductId.ToString("X4"),
            TargetBladeDevice.BiosVersion),
        ReadOnly: true,
        ProductionAdmitted: false,
        Success: result.Failure is null && result.Value is not null,
        Failure: result.Failure?.ToString() ?? "None",
        Responses: result.Value?.Readings.Select(reading => new CaptureResponse(
            reading.Operation,
            reading.RequestPayloadHex,
            ExpectedStatusHex: "02",
            ResponsePayloadHex: reading.ResponseHex)).ToArray() ?? []);

    private sealed record CaptureDocument(
        int SchemaVersion,
        CaptureDevice Device,
        bool ReadOnly,
        bool ProductionAdmitted,
        bool Success,
        string Failure,
        IReadOnlyList<CaptureResponse> Responses);

    private sealed record CaptureDevice(
        string Model,
        string VendorId,
        string ProductId,
        string BiosVersion);

    private sealed record CaptureResponse(
        string Operation,
        string RequestPayloadHex,
        string ExpectedStatusHex,
        string ResponsePayloadHex);
}
