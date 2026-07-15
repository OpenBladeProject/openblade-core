namespace OpenBlade.Capture;

public sealed class BatteryQueryCommand
{
    public const int UsageExitCode = 1;
    public const int QueryFailureExitCode = 2;
    public const int CanceledExitCode = 4;

    private readonly Func<CancellationToken, Task<BatteryQueryResult>> _run;
    private readonly TextWriter _output;
    private readonly Func<Action, IDisposable> _registerCancellation;

    public BatteryQueryCommand(
        Func<CancellationToken, Task<BatteryQueryResult>> run,
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
        if (arguments.Count != 0)
        {
            await _output.WriteLineAsync("Usage: OpenBlade.Capture query-battery")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = _registerCancellation(
            () => Cancel(commandCancellation));

        try
        {
            BatteryQueryResult result = await _run(commandCancellation.Token)
                .ConfigureAwait(false);
            commandCancellation.Token.ThrowIfCancellationRequested();
            await RenderAsync(result).ConfigureAwait(false);
            return result.Success ? 0 : QueryFailureExitCode;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await _output.WriteLineAsync("Canceled=True").ConfigureAwait(false);
            return CanceledExitCode;
        }
    }

    private async Task RenderAsync(BatteryQueryResult result)
    {
        await _output.WriteLineAsync(
            $"Setting={result.Setting?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
        await _output.WriteLineAsync($"Failure={result.Failure}").ConfigureAwait(false);
        await _output.WriteLineAsync($"Success={result.Success}").ConfigureAwait(false);
        if (result.Diagnostic is { } diagnostic)
        {
            await _output.WriteLineAsync($"Phase={diagnostic.Phase}").ConfigureAwait(false);
            await _output.WriteLineAsync($"SessionFailure={diagnostic.Failure}").ConfigureAwait(false);
            await _output.WriteLineAsync(
                $"Win32Error={diagnostic.NativeWin32Error?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
            await _output.WriteLineAsync(
                $"TransferredBytes={diagnostic.TransferredByteCount?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
        }
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
