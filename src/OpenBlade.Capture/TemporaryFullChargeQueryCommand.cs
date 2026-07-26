namespace OpenBlade.Capture;

public sealed class TemporaryFullChargeQueryCommand
{
    public const int UsageExitCode = 1;
    public const int QueryFailureExitCode = 2;
    public const int CanceledExitCode = 4;

    private readonly Func<CancellationToken, Task<TemporaryFullChargeQueryResult>> run;
    private readonly TextWriter output;
    private readonly Func<Action, IDisposable> registerCancellation;

    public TemporaryFullChargeQueryCommand(
        Func<CancellationToken, Task<TemporaryFullChargeQueryResult>> run,
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
        if (arguments.Count != 0)
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture query-temporary-full-charge").ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellation(() => Cancel(commandCancellation));
        try
        {
            TemporaryFullChargeQueryResult result =
                await run(commandCancellation.Token).ConfigureAwait(false);
            commandCancellation.Token.ThrowIfCancellationRequested();
            await output.WriteLineAsync(
                $"Active={result.Active?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
            await output.WriteLineAsync($"Failure={result.Failure}").ConfigureAwait(false);
            await output.WriteLineAsync($"Success={result.Success}").ConfigureAwait(false);
            if (result.Diagnostic is { } diagnostic)
            {
                await output.WriteLineAsync($"Phase={diagnostic.Phase}").ConfigureAwait(false);
                await output.WriteLineAsync($"SessionFailure={diagnostic.Failure}").ConfigureAwait(false);
                await output.WriteLineAsync(
                    $"Win32Error={diagnostic.NativeWin32Error?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
                await output.WriteLineAsync(
                    $"TransferredBytes={diagnostic.TransferredByteCount?.ToString() ?? "Unavailable"}").ConfigureAwait(false);
            }

            return result.Success ? 0 : QueryFailureExitCode;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await output.WriteLineAsync("Canceled=True").ConfigureAwait(false);
            return CanceledExitCode;
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
