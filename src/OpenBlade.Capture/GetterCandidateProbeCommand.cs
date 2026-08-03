using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal abstract class GetterCandidateProbeCommand
{
    internal const int UsageExitCode = 1;
    internal const int QueryFailureExitCode = 2;
    internal const int CanceledExitCode = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Func<CancellationToken,
        Task<HidSessionResult<GetterCandidateProbeSnapshot>>> run;
    private readonly TextWriter output;
    private readonly Func<Action, IDisposable> registerCancellation;
    private readonly DeviceIdentity device;

    protected GetterCandidateProbeCommand(
        Func<CancellationToken,
            Task<HidSessionResult<GetterCandidateProbeSnapshot>>> run,
        TextWriter output,
        Func<Action, IDisposable>? registerCancellation = null,
        DeviceIdentity? device = null)
    {
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.registerCancellation = registerCancellation ?? ConsoleCancellation.Register;
        this.device = device ?? new DeviceIdentity(
            TargetBladeDevice.Model,
            TargetBladeDevice.VendorId,
            TargetBladeDevice.ProductId,
            TargetBladeDevice.BiosVersion);
    }

    protected abstract CandidateMetadata Metadata { get; }

    internal async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 0)
        {
            await output.WriteLineAsync($"Usage: OpenBlade.Capture {Metadata.CommandName}")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellation(
            () => ConsoleCancellation.Cancel(commandCancellation));
        try
        {
            HidSessionResult<GetterCandidateProbeSnapshot> result =
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

    private CaptureDocument CreateDocument(
        HidSessionResult<GetterCandidateProbeSnapshot> result) => new(
        SchemaVersion: 1,
        Device: new(
            device.Model,
            device.VendorId.ToString("X4"),
            device.ProductId.ToString("X4"),
            device.BiosVersion),
        Candidate: Metadata.Candidate,
        Request: new(
            Metadata.ClassIdHex,
            Metadata.CommandIdHex,
            Metadata.RequestPayloadHex,
            Metadata.ExpectedPayloadLength,
            ExpectedStatusHex: "02"),
        ReadOnlySemanticIntent: true,
        FeatureReportSubmissionRequired: true,
        ProductionAdmitted: false,
        TypedReadbackAdmitted: false,
        SemanticMappingConfirmed: false,
        TransportSuccess: result.Failure is null && result.Value is not null,
        Failure: result.Failure?.ToString() ?? "None",
        FailurePhase: result.Diagnostic?.Phase.ToString() ?? "None",
        ProtocolValidationFailure:
            result.Diagnostic?.ProtocolValidationFailure?.ToString() ?? "None",
        ResponsePayloadHex: result.Value?.ResponsePayloadHex ?? string.Empty);

    protected sealed record CandidateMetadata(
        string CommandName,
        string Candidate,
        string ClassIdHex,
        string CommandIdHex,
        string RequestPayloadHex,
        int ExpectedPayloadLength);

    private sealed record CaptureDocument(
        int SchemaVersion,
        CaptureDevice Device,
        string Candidate,
        CaptureRequest Request,
        bool ReadOnlySemanticIntent,
        bool FeatureReportSubmissionRequired,
        bool ProductionAdmitted,
        bool TypedReadbackAdmitted,
        bool SemanticMappingConfirmed,
        bool TransportSuccess,
        string Failure,
        string FailurePhase,
        string ProtocolValidationFailure,
        string ResponsePayloadHex);

    private sealed record CaptureDevice(
        string Model,
        string VendorId,
        string ProductId,
        string BiosVersion);

    private sealed record CaptureRequest(
        string ClassIdHex,
        string CommandIdHex,
        string PayloadHex,
        int ExpectedPayloadLength,
        string ExpectedStatusHex);
}

internal sealed class FunctionKeyPrimaryGetterCandidateProbeCommand(
    Func<CancellationToken, Task<HidSessionResult<GetterCandidateProbeSnapshot>>> run,
    TextWriter output,
    Func<Action, IDisposable>? registerCancellation = null,
    DeviceIdentity? device = null)
    : GetterCandidateProbeCommand(run, output, registerCancellation, device)
{
    protected override CandidateMetadata Metadata { get; } = new(
        CommandName: "probe-function-key-primary-getter",
        Candidate: "function-key-primary-readback",
        ClassIdHex: "02",
        CommandIdHex: "86",
        RequestPayloadHex: "0100",
        ExpectedPayloadLength: 2);
}

internal sealed class GamingModeGetterCandidateProbeCommand(
    Func<CancellationToken, Task<HidSessionResult<GetterCandidateProbeSnapshot>>> run,
    TextWriter output,
    Func<Action, IDisposable>? registerCancellation = null,
    DeviceIdentity? device = null)
    : GetterCandidateProbeCommand(run, output, registerCancellation, device)
{
    protected override CandidateMetadata Metadata { get; } = new(
        CommandName: "probe-gaming-mode-getter",
        Candidate: "gaming-mode-readback",
        ClassIdHex: "00",
        CommandIdHex: "88",
        RequestPayloadHex: "00000000",
        ExpectedPayloadLength: 4);
}

internal sealed class DeviceModeQueryCommand(
    Func<CancellationToken, Task<HidSessionResult<GetterCandidateProbeSnapshot>>> run,
    TextWriter output,
    Func<Action, IDisposable>? registerCancellation = null)
{
    internal const int UsageExitCode = 1;
    internal const int QueryFailureExitCode = 2;
    internal const int CanceledExitCode = 4;

    internal async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 0)
        {
            await output.WriteLineAsync("Usage: OpenBlade.Capture query-device-mode")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = (registerCancellation
            ?? ConsoleCancellation.Register)(
                () => ConsoleCancellation.Cancel(commandCancellation));
        try
        {
            HidSessionResult<GetterCandidateProbeSnapshot> result =
                await run(commandCancellation.Token).ConfigureAwait(false);
            commandCancellation.Token.ThrowIfCancellationRequested();

            RazerDeviceMode? mode = null;
            if (result.Value is not null
                && TryDecodeMode(result.Value.ResponsePayloadHex, out RazerDeviceMode decoded))
            {
                mode = decoded;
            }

            await output.WriteLineAsync($"Mode={mode?.ToString() ?? "Unavailable"}")
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                $"ResponsePayload={result.Value?.ResponsePayloadHex ?? "Unavailable"}")
                .ConfigureAwait(false);
            await output.WriteLineAsync($"Failure={result.Failure?.ToString() ?? "None"}")
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                $"FailurePhase={result.Diagnostic?.Phase.ToString() ?? "None"}")
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                $"ProtocolValidation=" +
                $"{result.Diagnostic?.ProtocolValidationFailure?.ToString() ?? "None"}")
                .ConfigureAwait(false);

            return result.Failure is null && mode is not null
                ? 0
                : QueryFailureExitCode;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await output.WriteLineAsync("Canceled=True").ConfigureAwait(false);
            return CanceledExitCode;
        }
    }

    private static bool TryDecodeMode(string payloadHex, out RazerDeviceMode mode)
    {
        mode = default;
        try
        {
            return DeviceModeProtocol.TryDecodeQuery(
                Convert.FromHexString(payloadHex),
                out mode);
        }
        catch (FormatException)
        {
            return false;
        }
    }

}
