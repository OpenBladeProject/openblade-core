namespace OpenBlade.Setup;

// Appends a short run record to the Burn-allocated package log so a quiet install
// failure keeps its reason. Logging is best effort and never changes the setup result.
public sealed class SetupLog(string? path)
{
    public void Write(string message)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(
                path,
                $"[{DateTimeOffset.UtcNow:O}] OpenBlade.Setup: {message}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException
            or ArgumentException)
        {
            // A missing or locked log must not turn a working install into a failure.
        }
    }

    public void WriteFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write($"Failed: {Describe(exception)}");
        IEnumerable<Exception> causes = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : exception.InnerException is { } inner ? [inner] : [];
        foreach (Exception cause in causes)
        {
            Write($"Caused by: {Describe(cause)}");
        }
    }

    private static string Describe(Exception exception) =>
        $"{exception.GetType().FullName}: {exception.Message}";
}

// Records each setup step so the log shows how far a failed run got. Repeats of one
// step, such as a copy per payload file, collapse into a count and the last argument.
public sealed class LoggingSetupProgressReporter(ISetupProgressReporter inner, SetupLog log)
    : ISetupProgressReporter
{
    private SetupProgressCode? repeatedCode;
    private int repeats;
    private string? lastArguments;

    public void Report(SetupProgressEvent progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.Code == repeatedCode)
        {
            repeats++;
        }
        else
        {
            FlushRepeats();
            repeatedCode = progress.Code;
            log.Write($"Step: {progress.Code}");
        }
        lastArguments = string.Join(", ", progress.Arguments);
        inner.Report(progress);
    }

    public void Dispose()
    {
        FlushRepeats();
        inner.Dispose();
    }

    private void FlushRepeats()
    {
        if (repeats > 0)
        {
            log.Write($"Step: {repeatedCode} x{repeats + 1} in total, last: {lastArguments}");
        }
        repeats = 0;
    }
}
