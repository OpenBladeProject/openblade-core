using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed record DiagnosticHistorySnapshot(
    IReadOnlyList<DiagnosticCommandResult> CommandResults,
    IReadOnlyList<string> LogLines);

public sealed class DiagnosticHistory : ILoggerProvider
{
    public const int DefaultMaximumEntries = 96;
    public const int DefaultMaximumTotalCharacters = 12 * 1024;
    public const int DefaultMaximumLineCharacters = 512;

    private readonly object gate = new();
    private readonly Queue<HistoryEntry> entries = [];
    private readonly int maximumEntries;
    private readonly int maximumTotalCharacters;
    private readonly int maximumLineCharacters;
    private readonly TimeProvider timeProvider;
    private int totalCharacters;

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
                    .ToArray());
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
        string line = Clamp($"{level} {category}: {message}");
        if (line.Length == 0)
        {
            return;
        }

        Add(new HistoryEntry(null, line, line.Length));
    }

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
            return;
        }

        lock (gate)
        {
            while (entries.Count > 0
                && (entries.Count >= maximumEntries
                    || totalCharacters + entry.Characters > maximumTotalCharacters))
            {
                totalCharacters -= entries.Dequeue().Characters;
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

        string singleLine = value
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        return singleLine.Length <= maximumLineCharacters
            ? singleLine
            : singleLine[..maximumLineCharacters];
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
            if (!IsEnabled(logLevel) || ContainsException(state))
            {
                return;
            }

            // Intentionally store only the formatter's owned message. Exception text,
            // stacks, native handles, and protocol objects never enter this history.
            owner.AddLog(logLevel, category, formatter(state, null));
        }

        private static bool ContainsException(object? state)
        {
            if (state is Exception)
            {
                return true;
            }

            if (state is IEnumerable<KeyValuePair<string, object?>> structured)
            {
                return structured.Any(pair => pair.Value is Exception);
            }

            return false;
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
