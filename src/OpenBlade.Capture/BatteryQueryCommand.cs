namespace OpenBlade.Capture;

public sealed class BatteryQueryCommand
{
    public const int UsageExitCode = 1;
    public const int QueryFailureExitCode = 2;
    public const int CanceledExitCode = 4;

    private readonly Func<CancellationToken, Task<BatteryQueryResult>> runAsync;
    private readonly TextWriter outputWriter;
    private readonly Func<Action, IDisposable> registerCancellationHandler;

    public BatteryQueryCommand(
        Func<CancellationToken, Task<BatteryQueryResult>> run,
        TextWriter output,
        Func<Action, IDisposable>? registerCancellation = null)
    {
        runAsync = run ?? throw new ArgumentNullException(nameof(run));
        outputWriter = output ?? throw new ArgumentNullException(nameof(output));
        registerCancellationHandler = registerCancellation ?? RegisterConsoleCancellation;
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 0)
        {
            await outputWriter.WriteLineAsync("Usage: OpenBlade.Capture query-battery")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellationHandler(
            () => Cancel(commandCancellation));

        try
        {
            BatteryQueryResult result = await runAsync(commandCancellation.Token)
                .ConfigureAwait(false);
            commandCancellation.Token.ThrowIfCancellationRequested();
            await RenderAsync(result).ConfigureAwait(false);
            return result.Success ? 0 : QueryFailureExitCode;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await outputWriter.WriteLineAsync("Canceled=True").ConfigureAwait(false);
            return CanceledExitCode;
        }
    }

    private async Task RenderAsync(BatteryQueryResult result)
    {
        await outputWriter.WriteLineAsync(
            $"Setting={result.Setting?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
        await outputWriter.WriteLineAsync($"Failure={result.Failure}").ConfigureAwait(false);
        await outputWriter.WriteLineAsync($"Success={result.Success}").ConfigureAwait(false);
        if (result.Diagnostic is { } diagnostic)
        {
            await outputWriter.WriteLineAsync($"Phase={diagnostic.Phase}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync($"SessionFailure={diagnostic.Failure}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync(
                $"Win32Error={diagnostic.NativeWin32Error?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
            await outputWriter.WriteLineAsync(
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
        private ConsoleCancelEventHandler? registeredHandler = handler;

        public void Dispose()
        {
            ConsoleCancelEventHandler? handler = Interlocked.Exchange(ref registeredHandler, null);
            if (handler is not null)
            {
                Console.CancelKeyPress -= handler;
            }
        }
    }
}
