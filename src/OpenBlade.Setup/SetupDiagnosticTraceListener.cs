using System.Diagnostics;

namespace OpenBlade.Setup;

internal sealed class SetupDiagnosticTraceListener(SetupLog log) : TraceListener
{
    public override void Write(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            WriteNoThrow("Verbose", message);
        }
    }

    public override void WriteLine(string? message) => Write(message);

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            WriteNoThrow(eventType.ToString(), message);
        }
    }

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? format,
        params object?[]? args)
    {
        string? message = format;
        try
        {
            if (args is { Length: > 0 })
            {
                message = string.Format(format ?? string.Empty, args);
            }
        }
        catch (FormatException)
        {
            message = format;
        }
        TraceEvent(eventCache, source, eventType, id, message);
    }

    private void WriteNoThrow(string level, string message)
    {
        try
        {
            log.Write($"Trace {level}: {message}");
        }
        catch
        {
            // Never trace a logging failure back through this listener.
        }
    }
}
