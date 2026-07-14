using OpenSynapse.Protocol;

namespace OpenSynapse.Capture;

public sealed class BatteryValidationCommand
{
    public const int UsageExitCode = 1;
    public const int ValidationFailureExitCode = 2;
    public const int CanceledExitCode = 4;

    private readonly Func<
        BatteryValidationSetting,
        CancellationToken,
        Task<BatteryValidationResult>> _run;
    private readonly TextWriter _output;
    private readonly Func<Action, IDisposable> _registerCancellation;

    public BatteryValidationCommand(
        Func<
            BatteryValidationSetting,
            CancellationToken,
            Task<BatteryValidationResult>> run,
        TextWriter output,
        Func<Action, IDisposable>? registerCancellation = null)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _registerCancellation = registerCancellation ?? RegisterConsoleCancellation;
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryParse(arguments, out BatteryValidationSetting candidate))
        {
            await _output
                .WriteLineAsync("Usage: OpenSynapse.Capture validate-battery <off|50|55|60|65|70|75|80>")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = _registerCancellation(
            () => Cancel(commandCancellation));

        try
        {
            BatteryValidationResult result = await _run(candidate, commandCancellation.Token)
                .ConfigureAwait(false);
            commandCancellation.Token.ThrowIfCancellationRequested();
            await RenderAsync(result).ConfigureAwait(false);
            return result.Success ? 0 : ValidationFailureExitCode;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await _output.WriteLineAsync("Canceled=True").ConfigureAwait(false);
            return CanceledExitCode;
        }
    }

    private async Task RenderAsync(BatteryValidationResult result)
    {
        await _output.WriteLineAsync($"ApplyAttempted={result.ApplyAttempted}").ConfigureAwait(false);
        await _output.WriteLineAsync($"Applied={result.Applied}").ConfigureAwait(false);
        await _output.WriteLineAsync($"ReadBack={result.ReadBack}").ConfigureAwait(false);
        await _output.WriteLineAsync($"Restored={result.Restored}").ConfigureAwait(false);
        await _output.WriteLineAsync($"PrimaryFailure={result.PrimaryFailure}").ConfigureAwait(false);
        await _output.WriteLineAsync($"RestorationFailure={result.RestorationFailure}").ConfigureAwait(false);
        await _output.WriteLineAsync($"Success={result.Success}").ConfigureAwait(false);
        if (result.BaselineQueryDiagnostic is { } diagnostic)
        {
            await _output.WriteLineAsync($"BaselineQueryPhase={diagnostic.Phase}").ConfigureAwait(false);
            await _output.WriteLineAsync($"BaselineQuerySessionFailure={diagnostic.Failure}").ConfigureAwait(false);
            await _output.WriteLineAsync(
                $"BaselineQueryWin32Error={diagnostic.NativeWin32Error?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
            await _output.WriteLineAsync(
                $"BaselineQueryTransferredBytes={diagnostic.TransferredByteCount?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
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
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class CancellationRegistration(ConsoleCancelEventHandler handler) : IDisposable
    {
        private ConsoleCancelEventHandler? _handler = handler;

        public void Dispose()
        {
            ConsoleCancelEventHandler? handler = Interlocked.Exchange(ref _handler, null);
            if (handler is not null)
            {
                Console.CancelKeyPress -= handler;
            }
        }
    }
}
