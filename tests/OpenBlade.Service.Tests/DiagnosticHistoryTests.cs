using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class DiagnosticHistoryTests
{
    [Fact]
    public void LoggerProvider_BoundsLinesEntryCountAndTotalText()
    {
        var history = new DiagnosticHistory(
            maximumEntries: 3,
            maximumTotalCharacters: 18,
            maximumLineCharacters: 8,
            TimeProvider.System);
        ILogger logger = history.CreateLogger("OpenBlade.Tests");

        logger.LogInformation("first-long-line");
        logger.LogInformation("second-long-line");
        logger.LogInformation("third-long-line");

        DiagnosticHistorySnapshot snapshot = history.Snapshot();

        Assert.InRange(snapshot.LogLines.Count, 1, 3);
        Assert.All(snapshot.LogLines, line => Assert.InRange(line.Length, 1, 8));
        Assert.InRange(snapshot.LogLines.Sum(line => line.Length), 1, 18);
        Assert.DoesNotContain(snapshot.LogLines, line => line.Contains("first", StringComparison.Ordinal));
    }

    [Fact]
    public void LoggerProvider_CapturesFormattedExceptionButNotStructuredExceptionData()
    {
        const string exceptionSecret = "exception-private-data";
        var history = new DiagnosticHistory();
        ILogger logger = history.CreateLogger("OpenBlade.Tests");
        InvalidOperationException exception;
        try
        {
            throw new InvalidOperationException(exceptionSecret);
        }
        catch (InvalidOperationException captured)
        {
            exception = captured;
        }
        exception.Data["payload"] = "raw-hid-private-data";

        logger.LogError(exception, "Controller access failed.");

        string diagnostic = string.Join('\n', history.Snapshot().LogLines);
        Assert.Contains("Controller access failed.", diagnostic, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), diagnostic, StringComparison.Ordinal);
        Assert.Contains(exceptionSecret, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-hid", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(LoggerProvider_CapturesFormattedExceptionButNotStructuredExceptionData),
            diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void LoggerProvider_RetainsExceptionValuedStructuredStateWithoutRenderingRawData()
    {
        var history = new DiagnosticHistory();
        ILogger logger = history.CreateLogger("OpenBlade.Tests");
        var failure = new InvalidDataException("The installed INF binding is absent.");
        failure.Data["raw"] = "raw-hid-secret";

        logger.LogWarning("Package discovery failed: {Failure}", failure);
        logger.Log(LogLevel.Warning, new EventId(0), failure, null,
            static (_, _) => "raw-hid-formatter-output");

        string diagnostic = string.Join(Environment.NewLine, history.Snapshot().LogLines);
        Assert.Contains("The installed INF binding is absent.", diagnostic);
        Assert.Contains(nameof(InvalidDataException), diagnostic);
        Assert.DoesNotContain("raw-hid", diagnostic);
    }

    [Fact]
    public void TraceListener_PersistsDiagnosticTraceWarnings()
    {
        var history = new DiagnosticHistory();
        using var listener = new DiagnosticHistoryTraceListener(history);

        listener.TraceEvent(null, "OpenBlade", TraceEventType.Warning, 0,
            "component=Protocol operation=Capture reason=Failure");

        Assert.Contains(history.Snapshot().LogLines, line =>
            line.Contains("component=Protocol operation=Capture reason=Failure", StringComparison.Ordinal));
    }

    [Fact]
    public void TraceListener_MalformedFormatNeverEscapes()
    {
        var history = new DiagnosticHistory();
        using var listener = new DiagnosticHistoryTraceListener(history);

        Exception? exception = Record.Exception(() => listener.TraceEvent(
            null, "OpenBlade", TraceEventType.Warning, 0, "invalid {0} {1}", ["only-one"]));

        Assert.Null(exception);
        Assert.Contains(history.Snapshot().LogLines, line =>
            line.Contains("invalid {0} {1}", StringComparison.Ordinal));
    }

    [Fact]
    public void Snapshot_ReportsEvictionAndExplicitValueTruncation()
    {
        var history = new DiagnosticHistory(
            maximumEntries: 2,
            maximumTotalCharacters: 80,
            maximumLineCharacters: 32,
            TimeProvider.System);

        history.RecordSummary(ControlOperationStatus.Failed, new string('x', 100));
        Assert.Contains("[truncated]", history.Snapshot().CommandResults.Single().Detail, StringComparison.Ordinal);
        ILogger logger = history.CreateLogger("OpenBlade.Tests");
        logger.LogWarning("first");
        logger.LogWarning("second");
        logger.LogWarning("third");

        DiagnosticHistorySnapshot snapshot = history.Snapshot();
        Assert.True(snapshot.EvictedEntries > 0);
        Assert.True(snapshot.TruncatedValues > 0);
        Assert.Equal(snapshot.CommandResults.Count + snapshot.LogLines.Count, snapshot.RetainedEntries);
        Assert.True(snapshot.RetainedCharacters <= 80);
    }

    [Fact]
    public void LoggerProvider_IgnoresCategoriesNotOwnedByOpenBlade()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-15T12:34:56Z"));
        var history = new DiagnosticHistory(timeProvider: time);

        history.CreateLogger("Microsoft.Hosting.Lifetime")
            .LogInformation("Hosting secret");
        history.CreateLogger("OpenBladeAdjacent")
            .LogWarning("Adjacent secret");
        history.CreateLogger("OpenBlade")
            .LogInformation("root-owned");
        history.CreateLogger("OpenBlade.Service.Worker")
            .LogInformation("child-owned");

        string[] lines = history.Snapshot().LogLines.ToArray();
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.StartsWith(
            $"{time.GetUtcNow():O} pid={Environment.ProcessId} run=",
            line,
            StringComparison.Ordinal));
        string[] firstPrefix = lines[0].Split(' ', 6);
        string[] secondPrefix = lines[1].Split(' ', 6);
        Assert.Equal(firstPrefix[2], secondPrefix[2]);
        Assert.Equal(
            $"version={OpenBladeProductVersion.Read(typeof(DiagnosticHistory).Assembly)}",
            firstPrefix[3]);
        Assert.EndsWith("Information OpenBlade: root-owned", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("Information OpenBlade.Service.Worker: child-owned", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void LoggerProvider_CapturesExceptionValuedStateWithoutInvokingItsFormatter()
    {
        const string secret = "structured-exception-secret";
        var history = new DiagnosticHistory();
        ILogger logger = history.CreateLogger("OpenBlade.Service.Worker");

        logger.LogInformation(
            "Controller state {Failure}",
            new InvalidOperationException(secret));
        logger.Log(
            LogLevel.Warning,
            default,
            new InvalidDataException(secret),
            exception: null,
            static (_, _) => throw new InvalidOperationException("Unsafe exception formatter invoked."));

        string diagnostic = string.Join(Environment.NewLine, history.Snapshot().LogLines);
        Assert.Contains(secret, diagnostic, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), diagnostic, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidDataException), diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("Unsafe exception formatter", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoggerProvider_IsThreadSafeUnderConcurrentWriters()
    {
        var history = new DiagnosticHistory(
            maximumEntries: 64,
            maximumTotalCharacters: 4_096,
            maximumLineCharacters: 64,
            TimeProvider.System);
        ILogger logger = history.CreateLogger("OpenBlade.Concurrent");

        await Task.WhenAll(Enumerable.Range(0, 200).Select(index => Task.Run(
            () => logger.LogInformation("entry-{Index}", index))));

        DiagnosticHistorySnapshot snapshot = history.Snapshot();
        Assert.InRange(snapshot.LogLines.Count, 1, 64);
        Assert.True(snapshot.LogLines.Sum(line => line.Length) <= 4_096);
    }

    [Fact]
    public void RecordSubsystemResults_CapturesFourBoundedReconcileRecords()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-15T12:34:56Z"));
        var history = new DiagnosticHistory(timeProvider: time);
        var results = new SubsystemApplyResults(
            new ControlResult(ControlResultStatus.Unchanged, "performance"),
            new ControlResult(ControlResultStatus.Blocked, "fans"),
            new ControlResult(ControlResultStatus.Applied, "battery"),
            new ControlResult(ControlResultStatus.Failed, "lighting"));

        history.RecordSubsystemResults(results);

        DiagnosticCommandResult[] records = history.Snapshot().CommandResults.ToArray();
        Assert.Equal(4, records.Length);
        Assert.Equal(["Performance", "Fans", "Battery", "Lighting"], records.Select(record => record.Subsystem));
        Assert.All(records, record => Assert.Equal("Reconcile", record.Action));
        Assert.All(records, record => Assert.Equal(time.GetUtcNow(), record.Timestamp));
        Assert.Equal(["Unchanged", "Blocked", "Applied", "Failed"], records.Select(record => record.Status));
    }

    [Fact]
    public void RecordSubsystemResult_CapturesOnlyTheNamedScopedResult()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-15T12:34:56Z"));
        var history = new DiagnosticHistory(timeProvider: time);

        history.RecordSubsystemResult(
            DeviceSubsystem.Fans,
            new ControlResult(ControlResultStatus.Applied, "fan confirmed"));

        DiagnosticCommandResult record = Assert.Single(history.Snapshot().CommandResults);
        Assert.Equal("Fans", record.Subsystem);
        Assert.Equal("Reconcile", record.Action);
        Assert.Equal("Applied", record.Status);
        Assert.Equal("fan confirmed", record.Detail);
        Assert.Equal(time.GetUtcNow(), record.Timestamp);
    }

    [Fact]
    public void RecordSummary_PreservesTransitionIdentity()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-15T12:34:56Z"));
        var history = new DiagnosticHistory(timeProvider: time);
        Guid transitionId = Guid.Parse("a178986d-e244-4730-97a4-9fc83672b3f2");

        history.RecordSummary(
            ControlOperationStatus.Applied,
            "lifecycle restored",
            transitionId);

        DiagnosticCommandResult record = Assert.Single(
            history.Snapshot().CommandResults);
        Assert.Equal(transitionId, record.TransitionId);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
