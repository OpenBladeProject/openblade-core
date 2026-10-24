using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace OpenBlade.DeploymentBroker;

internal sealed class BrokerDiagnosticTraceListener(ILogger logger) : TraceListener
{
    public override void Write(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            LogNoThrow(LogLevel.Debug, message);
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
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        LogNoThrow(Level(eventType), message);
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

    private void LogNoThrow(LogLevel level, string message)
    {
        try
        {
            logger.Log(level, "{Diagnostic}", message);
        }
        catch
        {
            // Diagnostics must not change the broker operation being observed.
        }
    }

    private static LogLevel Level(TraceEventType eventType) => eventType switch
    {
        TraceEventType.Critical => LogLevel.Critical,
        TraceEventType.Error => LogLevel.Error,
        TraceEventType.Warning => LogLevel.Warning,
        TraceEventType.Information => LogLevel.Information,
        _ => LogLevel.Debug,
    };
}
