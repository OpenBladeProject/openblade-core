using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public sealed class BatteryQuerySequenceCommand
{
    public const int SuccessExitCode = 0;
    public const int UsageExitCode = 1;
    public const int QueryFailureExitCode = 2;
    public const int CanceledExitCode = 4;

    private readonly Func<CancellationToken, Task<BatteryQuerySequenceResult>> run;
    private readonly TextWriter output;
    private readonly Func<Action, IDisposable> registerCancellation;

    public BatteryQuerySequenceCommand(
        Func<CancellationToken, Task<BatteryQuerySequenceResult>> run,
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
            await output.WriteLineAsync("Usage: OpenBlade.Capture probe-battery-sequence")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellation(
            () => ConsoleCancellation.Cancel(commandCancellation));

        try
        {
            BatteryQuerySequenceResult result = await run(commandCancellation.Token)
                .ConfigureAwait(false);
            commandCancellation.Token.ThrowIfCancellationRequested();
            await RenderAsync(result).ConfigureAwait(false);
            return result.Success ? SuccessExitCode : QueryFailureExitCode;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await output.WriteLineAsync("Canceled=True").ConfigureAwait(false);
            return CanceledExitCode;
        }
    }

    private async Task RenderAsync(BatteryQuerySequenceResult result)
    {
        BatteryQuerySequenceOrdinalResult[] ordinals =
            [result.Query1, result.Query2, result.Query3, result.Query4, result.Query5];
        for (int index = 0; index < ordinals.Length; index++)
        {
            int ordinal = index + 1;
            BatteryQuerySequenceOrdinalResult item = ordinals[index];
            await output.WriteLineAsync(
                $"Query{ordinal}Setting={item.Setting?.ToString() ?? "Unavailable"}")
                .ConfigureAwait(false);
            await output.WriteLineAsync($"Query{ordinal}Failure={item.Failure}")
                .ConfigureAwait(false);
            if (item.Diagnostic is { } diagnostic)
            {
                await RenderDiagnosticAsync(ordinal, diagnostic).ConfigureAwait(false);
            }
        }

        await output.WriteLineAsync($"CompletedCount={result.CompletedCount}")
            .ConfigureAwait(false);
        await output.WriteLineAsync($"Success={result.Success}").ConfigureAwait(false);
    }

    private async Task RenderDiagnosticAsync(
        int ordinal,
        HidSessionFailureDiagnostic diagnostic)
    {
        await output.WriteLineAsync($"Query{ordinal}Phase={diagnostic.Phase}")
            .ConfigureAwait(false);
        await output.WriteLineAsync($"Query{ordinal}SessionFailure={diagnostic.Failure}")
            .ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Query{ordinal}Win32Error={diagnostic.NativeWin32Error?.ToString() ?? "Unavailable"}")
            .ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Query{ordinal}TransferredBytes={diagnostic.TransferredByteCount?.ToString() ?? "Unavailable"}")
            .ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Query{ordinal}ProtocolValidationFailure={SafeProtocolCategory(diagnostic.ProtocolValidationFailure)}")
            .ConfigureAwait(false);
    }

    private static string SafeProtocolCategory(
        HidProtocolValidationFailure? failure) => failure switch
        {
            null => "Unavailable",
            HidProtocolValidationFailure.ReportId => "FramingIdentifier",
            HidProtocolValidationFailure.EnvelopeLength => "EnvelopeLength",
            HidProtocolValidationFailure.Checksum => "Integrity",
            HidProtocolValidationFailure.TransactionMismatch => "CorrelationMismatch",
            HidProtocolValidationFailure.CommandClassMismatch => "ClassMismatch",
            HidProtocolValidationFailure.CommandIdMismatch => "OperationMismatch",
            HidProtocolValidationFailure.Status => "Status",
            HidProtocolValidationFailure.PayloadLength => "ContentLength",
            HidProtocolValidationFailure.PayloadValidator => "ContentValidator",
            _ => "Unknown",
        };

}
