using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public sealed class BatteryValidationCommand
{
    public const int UsageExitCode = 1;
    public const int ValidationFailureExitCode = 2;
    public const int CanceledExitCode = 4;

    private readonly Func<
        BatteryValidationSetting,
        CancellationToken,
        Task<BatteryValidationResult>> runAsync;
    private readonly TextWriter outputWriter;
    private readonly Func<Action, IDisposable> registerCancellationHandler;

    public BatteryValidationCommand(
        Func<
            BatteryValidationSetting,
            CancellationToken,
            Task<BatteryValidationResult>> run,
        TextWriter output,
        Func<Action, IDisposable>? registerCancellation = null)
    {
        runAsync = run ?? throw new ArgumentNullException(nameof(run));
        outputWriter = output ?? throw new ArgumentNullException(nameof(output));
        registerCancellationHandler = registerCancellation ?? ConsoleCancellation.Register;
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryParse(arguments, out BatteryValidationSetting candidate))
        {
            await outputWriter
                .WriteLineAsync("Usage: OpenBlade.Capture validate-battery <off|50|55|60|65|70|75|80>")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellationHandler(
            () => ConsoleCancellation.Cancel(commandCancellation));

        try
        {
            BatteryValidationResult result = await runAsync(candidate, commandCancellation.Token)
                .ConfigureAwait(false);
            commandCancellation.Token.ThrowIfCancellationRequested();
            await RenderAsync(result).ConfigureAwait(false);
            return result.Success ? 0 : ValidationFailureExitCode;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await outputWriter.WriteLineAsync("Canceled=True").ConfigureAwait(false);
            return CanceledExitCode;
        }
    }

    private async Task RenderAsync(BatteryValidationResult result)
    {
        await outputWriter.WriteLineAsync($"ApplyAttempted={result.ApplyAttempted}").ConfigureAwait(false);
        await outputWriter.WriteLineAsync($"Applied={result.Applied}").ConfigureAwait(false);
        await outputWriter.WriteLineAsync($"ReadBack={result.ReadBack}").ConfigureAwait(false);
        await outputWriter.WriteLineAsync($"Restored={result.Restored}").ConfigureAwait(false);
        await outputWriter.WriteLineAsync($"PrimaryFailure={result.PrimaryFailure}").ConfigureAwait(false);
        await outputWriter.WriteLineAsync($"RestorationFailure={result.RestorationFailure}").ConfigureAwait(false);
        await outputWriter.WriteLineAsync($"Success={result.Success}").ConfigureAwait(false);
        if (result.BaselineQueryDiagnostic is { } diagnostic)
        {
            await outputWriter.WriteLineAsync($"BaselineQueryPhase={diagnostic.Phase}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync($"BaselineQuerySessionFailure={diagnostic.Failure}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync(
                $"BaselineQueryWin32Error={diagnostic.NativeWin32Error?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync(
                $"BaselineQueryTransferredBytes={diagnostic.TransferredByteCount?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync(
                $"BaselineQueryProtocolValidationFailure={diagnostic.ProtocolValidationFailure?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
        }
        if (result.CandidateApplyDiagnostic is { } candidateApplyDiagnostic)
        {
            await outputWriter.WriteLineAsync($"CandidateApplyPhase={candidateApplyDiagnostic.Phase}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync($"CandidateApplySessionFailure={candidateApplyDiagnostic.Failure}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync(
                $"CandidateApplyWin32Error={candidateApplyDiagnostic.NativeWin32Error?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync(
                $"CandidateApplyTransferredBytes={candidateApplyDiagnostic.TransferredByteCount?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync(
                $"CandidateApplyProtocolValidationFailure={candidateApplyDiagnostic.ProtocolValidationFailure?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
        }
        if (result.CandidateQueryDiagnostic is { } candidateQueryDiagnostic)
        {
            await outputWriter.WriteLineAsync($"CandidateQueryPhase={candidateQueryDiagnostic.Phase}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync($"CandidateQuerySessionFailure={candidateQueryDiagnostic.Failure}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync(
                $"CandidateQueryWin32Error={candidateQueryDiagnostic.NativeWin32Error?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync(
                $"CandidateQueryTransferredBytes={candidateQueryDiagnostic.TransferredByteCount?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync(
                $"CandidateQueryProtocolValidationFailure={candidateQueryDiagnostic.ProtocolValidationFailure?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
        }
        if (result.RestorationDiagnostic is { } restorationDiagnostic)
        {
            await outputWriter.WriteLineAsync($"RestorationPhase={restorationDiagnostic.Phase}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync($"RestorationSessionFailure={restorationDiagnostic.Failure}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync(
                $"RestorationWin32Error={restorationDiagnostic.NativeWin32Error?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync(
                $"RestorationTransferredBytes={restorationDiagnostic.TransferredByteCount?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync(
                $"RestorationProtocolValidationFailure={restorationDiagnostic.ProtocolValidationFailure?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
        }
    }

    private static bool TryParse(
        IReadOnlyList<string> arguments,
        out BatteryValidationSetting candidate)
    {
        candidate = default;
        if (arguments.Count != 1)
        {
            return false;
        }

        (bool Success, BatteryValidationSetting Setting) parsed = arguments[0] switch
        {
            "off" => (true, BatteryValidationSetting.ProtectionOff),
            "50" => (true, BatteryValidationSetting.Limit50),
            "55" => (true, BatteryValidationSetting.Limit55),
            "60" => (true, BatteryValidationSetting.Limit60),
            "65" => (true, BatteryValidationSetting.Limit65),
            "70" => (true, BatteryValidationSetting.Limit70),
            "75" => (true, BatteryValidationSetting.Limit75),
            "80" => (true, BatteryValidationSetting.Limit80),
            _ => (false, default),
        };
        candidate = parsed.Setting;
        return parsed.Success;
    }

}
