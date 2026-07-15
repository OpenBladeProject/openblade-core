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
    public void LoggerProvider_DoesNotCaptureExceptionStacksOrStructuredExceptionData()
    {
        const string exceptionSecret = "exception-private-data";
        var history = new DiagnosticHistory();
        ILogger logger = history.CreateLogger("OpenBlade.Tests");
        var exception = new InvalidOperationException(exceptionSecret);
        exception.Data["payload"] = "raw-hid-private-data";

        logger.LogError(exception, "Controller access failed.");

        string line = Assert.Single(history.Snapshot().LogLines);
        Assert.Equal("Error OpenBlade.Tests: Controller access failed.", line);
        Assert.DoesNotContain(exceptionSecret, line, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-hid", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void LoggerProvider_IgnoresCategoriesNotOwnedByOpenBlade()
    {
        var history = new DiagnosticHistory();

        history.CreateLogger("Microsoft.Hosting.Lifetime")
            .LogInformation("Hosting secret");
        history.CreateLogger("OpenBladeAdjacent")
            .LogWarning("Adjacent secret");
        history.CreateLogger("OpenBlade")
            .LogInformation("root-owned");
        history.CreateLogger("OpenBlade.Service.Worker")
            .LogInformation("child-owned");

        Assert.Equal(
            [
                "Information OpenBlade: root-owned",
                "Information OpenBlade.Service.Worker: child-owned",
            ],
            history.Snapshot().LogLines);
    }

    [Fact]
    public void LoggerProvider_RejectsExceptionValuedStructuredStateBeforeFormatting()
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
            static (state, _) => state.ToString());

        Assert.Empty(history.Snapshot().LogLines);
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
