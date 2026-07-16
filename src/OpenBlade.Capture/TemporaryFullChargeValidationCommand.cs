namespace OpenBlade.Capture;

public sealed class TemporaryFullChargeValidationCommand
{
    public const int UsageExitCode = 1;
    public const int ValidationFailureExitCode = 2;
    public const int CanceledExitCode = 4;

    private readonly Func<CancellationToken, Task<TemporaryFullChargeValidationResult>> run;
    private readonly TextWriter output;
    private readonly Func<Action, IDisposable> registerCancellation;

    public TemporaryFullChargeValidationCommand(
        Func<CancellationToken, Task<TemporaryFullChargeValidationResult>> run,
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
                "Usage: OpenBlade.Capture validate-temporary-full-charge")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellation(() => Cancel(commandCancellation));
        try
        {
            TemporaryFullChargeValidationResult result = await run(commandCancellation.Token)
                .ConfigureAwait(false);
            commandCancellation.Token.ThrowIfCancellationRequested();
            await output.WriteLineAsync(
                $"BaselineProtection={Format(result.BaselineProtection)}").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"BaselineTemporaryFullCharge={Format(result.BaselineTemporaryFullCharge)}").ConfigureAwait(false);
            await output.WriteLineAsync($"EnableAttempted={result.EnableAttempted}").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"EnableAcknowledged={result.EnableAcknowledged}").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"CandidateProtection={Format(result.CandidateProtection)}").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"CandidateTemporaryFullCharge={Format(result.CandidateTemporaryFullCharge)}").ConfigureAwait(false);
            await output.WriteLineAsync($"ReadBack={result.ReadBack}").ConfigureAwait(false);
            await output.WriteLineAsync($"CancelAttempted={result.CancelAttempted}").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"CancelAcknowledged={result.CancelAcknowledged}").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"RestorationProtection={Format(result.RestorationProtection)}").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"RestorationTemporaryFullCharge={Format(result.RestorationTemporaryFullCharge)}").ConfigureAwait(false);
            await output.WriteLineAsync($"Restored={result.Restored}").ConfigureAwait(false);
            await output.WriteLineAsync($"PrimaryFailure={result.PrimaryFailure}").ConfigureAwait(false);
            await output.WriteLineAsync($"RestorationFailure={result.RestorationFailure}").ConfigureAwait(false);
            await output.WriteLineAsync($"Success={result.Success}").ConfigureAwait(false);
            return result.Success ? 0 : ValidationFailureExitCode;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await output.WriteLineAsync("Canceled=True").ConfigureAwait(false);
            return CanceledExitCode;
        }
    }

    private static string Format<T>(T? value) where T : struct =>
        value?.ToString() ?? "Unavailable";

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
