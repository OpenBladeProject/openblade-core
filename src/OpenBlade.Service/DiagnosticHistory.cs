using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed record DiagnosticHistorySnapshot(
    IReadOnlyList<DiagnosticCommandResult> CommandResults,
    IReadOnlyList<string> LogLines)
{
    public long EvictedEntries { get; init; }
    public long DroppedEntries { get; init; }
    public long TruncatedValues { get; init; }
    public int RetainedEntries { get; init; }
    public int RetainedCharacters { get; init; }
}

public sealed class DiagnosticHistory : ILoggerProvider
{
    public const int DefaultMaximumEntries = 2048;
    public const int DefaultMaximumTotalCharacters = 4 * 1024 * 1024;
    public const int DefaultMaximumLineCharacters = 4096;
    private const string TruncatedMarker = "...[truncated]";
    private static readonly string ServiceVersion = OpenBladeProductVersion.Read(
        typeof(DiagnosticHistory).Assembly);

    private readonly object gate = new();
    private readonly Queue<HistoryEntry> entries = [];
    private readonly int maximumEntries;
    private readonly int maximumTotalCharacters;
    private readonly int maximumLineCharacters;
    private readonly TimeProvider timeProvider;
    private readonly string runId = Guid.NewGuid().ToString("N");
    private int totalCharacters;
    private long evictedEntries;
    private long droppedEntries;
    private long truncatedValues;

    public DiagnosticHistory(
        int maximumEntries = DefaultMaximumEntries,
        int maximumTotalCharacters = DefaultMaximumTotalCharacters,
        int maximumLineCharacters = DefaultMaximumLineCharacters,
        TimeProvider? timeProvider = null)
    {
        if (maximumEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        if (maximumTotalCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTotalCharacters));
        }

        if (maximumLineCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLineCharacters));
        }

        this.maximumEntries = maximumEntries;
        this.maximumTotalCharacters = maximumTotalCharacters;
        this.maximumLineCharacters = maximumLineCharacters;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ILogger CreateLogger(string categoryName) =>
        new HistoryLogger(
            this,
            Clamp(categoryName),
            string.Equals(categoryName, "OpenBlade", StringComparison.Ordinal)
                || categoryName.StartsWith("OpenBlade.", StringComparison.Ordinal));

    public void Dispose()
    {
    }

    public DiagnosticHistorySnapshot Snapshot()
    {
        lock (gate)
        {
            return new DiagnosticHistorySnapshot(
                entries
                    .Where(entry => entry.CommandResult is not null)
                    .Select(entry => entry.CommandResult!)
                    .ToArray(),
                entries
                    .Where(entry => entry.LogLine is not null)
                    .Select(entry => entry.LogLine!)
                    .ToArray())
            {
                EvictedEntries = evictedEntries,
                DroppedEntries = droppedEntries,
                TruncatedValues = truncatedValues,
                RetainedEntries = entries.Count,
                RetainedCharacters = totalCharacters,
            };
        }
    }

    public void RecordSubsystemResults(SubsystemApplyResults results)
    {
        ArgumentNullException.ThrowIfNull(results);
        RecordSubsystem("Performance", results.Performance);
        RecordSubsystem("Fans", results.Fans);
        RecordSubsystem("Battery", results.Battery);
        RecordSubsystem("Lighting", results.Lighting);
    }

    public void RecordSubsystemResult(DeviceSubsystem subsystem, ControlResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        RecordSubsystem(subsystem.ToString(), result);
    }

    public void RecordSummary(
        ControlOperationStatus status,
        string summary,
        Guid? transitionId = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        AddCommandResult(new DiagnosticCommandResult(
            timeProvider.GetUtcNow(),
            "Service",
            "Reconcile",
            status.ToString(),
            Clamp(summary))
        {
            TransitionId = transitionId,
        });
    }

    public void RecordResumeValidation(
        ControlOperationStatus status,
        string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        AddCommandResult(new DiagnosticCommandResult(
            timeProvider.GetUtcNow(),
            "Service",
            "ResumeValidation",
            status.ToString(),
            Clamp(summary)));
    }

    private void RecordSubsystem(string subsystem, ControlResult result) =>
        AddCommandResult(new DiagnosticCommandResult(
            timeProvider.GetUtcNow(),
            subsystem,
            "Reconcile",
            result.Status.ToString(),
            Clamp(result.Message)));

    private void AddLog(LogLevel level, string category, string message)
    {
        string prefix = $"{timeProvider.GetUtcNow():O} pid={Environment.ProcessId} "
            + $"run={runId} version={ServiceVersion} {level} {category}: ";
        int contentLimit = Math.Max(1, maximumLineCharacters - prefix.Length);
        string normalized = DiagnosticText.Redact(message).Replace('\r', '\n');
        foreach (string logicalLine in normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            for (int offset = 0; offset < logicalLine.Length; offset += contentLimit)
            {
                string content = logicalLine.Substring(
                    offset,
                    Math.Min(contentLimit, logicalLine.Length - offset));
                string line = Clamp(prefix + content);
                if (line.Length > 0)
                {
                    Add(new HistoryEntry(null, line, line.Length));
                }
            }
        }
    }

    internal void RecordTrace(LogLevel level, string message) =>
        AddLog(level, "OpenBlade.DiagnosticTrace", message);

    private static string FormatLogException(
        string category,
        string message,
        Exception exception) => DiagnosticTrace.FormatException(
            category,
            "ILogger",
            exception,
            message);

    private void AddCommandResult(DiagnosticCommandResult result)
    {
        DiagnosticCommandResult bounded = result with
        {
            Subsystem = Clamp(result.Subsystem),
            Action = Clamp(result.Action),
            Status = Clamp(result.Status),
            Detail = result.Detail is null ? null : Clamp(result.Detail),
        };
        int characters = bounded.Subsystem.Length
            + bounded.Action.Length
            + bounded.Status.Length
            + (bounded.Detail?.Length ?? 0);
        Add(new HistoryEntry(bounded, null, characters));
    }

    private void Add(HistoryEntry entry)
    {
        if (entry.Characters > maximumTotalCharacters)
        {
            Interlocked.Increment(ref droppedEntries);
            return;
        }

        lock (gate)
        {
            while (entries.Count > 0
                && (entries.Count >= maximumEntries
                    || totalCharacters + entry.Characters > maximumTotalCharacters))
            {
                totalCharacters -= entries.Dequeue().Characters;
                evictedEntries++;
            }

            entries.Enqueue(entry);
            totalCharacters += entry.Characters;
        }
    }

    private string Clamp(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string singleLine = DiagnosticText.Redact(value)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        if (singleLine.Length <= maximumLineCharacters)
        {
            return singleLine;
        }

        Interlocked.Increment(ref truncatedValues);
        if (maximumLineCharacters <= TruncatedMarker.Length)
        {
            return TruncatedMarker[..maximumLineCharacters];
        }
        return singleLine[..(maximumLineCharacters - TruncatedMarker.Length)] + TruncatedMarker;
    }

    private sealed record HistoryEntry(
        DiagnosticCommandResult? CommandResult,
        string? LogLine,
        int Characters);

    private sealed class HistoryLogger(
        DiagnosticHistory owner,
        string category,
        bool ownedCategory) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) =>
            ownedCategory && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var failures = new List<Exception>();
            if (exception is not null)
            {
                failures.Add(exception);
            }
            string? template = null;
            bool exceptionState = false;
            if (state is Exception direct)
            {
                exceptionState = true;
                if (!failures.Contains(direct))
                {
                    failures.Add(direct);
                }
            }
            else if (state is IEnumerable<KeyValuePair<string, object?>> structured)
            {
                foreach (KeyValuePair<string, object?> pair in structured)
                {
                    if (pair.Key == "{OriginalFormat}" && pair.Value is string format)
                    {
                        template = format;
                    }
                    if (pair.Value is Exception embedded)
                    {
                        exceptionState = true;
                        if (failures.Count < DiagnosticFailure.MaximumNodes && !failures.Contains(embedded))
                        {
                            failures.Add(embedded);
                        }
                    }
                }
            }

            // Exception-valued structured state must be captured through the same recorder,
            // rather than dropped or rendered by arbitrary Exception.ToString overrides.
            string message = exceptionState ? template ?? "Structured exception state." : formatter(state, null);
            Exception? failure = failures.Count switch
            {
                0 => null,
                1 => failures[0],
                _ => new AggregateException("Multiple logger exceptions.", failures),
            };
            owner.AddLog(logLevel, category, failure is null
                ? message : FormatLogException(category, message, failure));
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
