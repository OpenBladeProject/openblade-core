using System.Diagnostics;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class PersistentTraceLogTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"OpenBlade.PersistentTraceLog.Tests-{Guid.NewGuid():N}");

    [Fact]
    public void TraceEvent_PersistsBoundedRedactedLine()
    {
        const string secret = "private-user-value";
        using var listener = new PersistentTraceLog(
            "OpenBlade.Tray",
            "1.2.3+test",
            directory,
            [secret]);

        listener.TraceEvent(
            null,
            "test",
            TraceEventType.Warning,
            0,
            "Could not open {0}",
            secret);

        string content = File.ReadAllText(listener.LogPath);
        Assert.Contains("[OpenBlade.Tray] [1.2.3+test] [Warning]", content);
        Assert.Contains("Could not open [REDACTED]", content);
        Assert.DoesNotContain(secret, content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"PID={Environment.ProcessId}", content);
    }

    [Fact]
    public void TraceEvent_RotatesAtBoundedSize()
    {
        using var listener = new PersistentTraceLog(
            "OpenBlade.SessionAgent",
            "1.2.3+test",
            directory,
            []);
        string message = new('x', 896 * 1024);

        for (int index = 0; index < 12; index++)
        {
            listener.TraceEvent(
                null,
                "test",
                TraceEventType.Error,
                index,
                message);
        }

        string previousPath = Path.Combine(
            directory,
            "OpenBlade.SessionAgent.previous.log");
        Assert.True(File.Exists(previousPath));
        Assert.InRange(new FileInfo(listener.LogPath).Length, 1, 1024 * 1024);
        Assert.InRange(new FileInfo(previousPath).Length, 1, 1024 * 1024);
    }

    [Fact]
    public async Task ConcurrentListenersForOneComponentRetainEveryEvent()
    {
        using var first = new PersistentTraceLog("OpenBlade.Shared", "test", directory, []);
        using var second = new PersistentTraceLog("OpenBlade.Shared", "test", directory, []);
        Task[] writers = Enumerable.Range(0, 80).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).TraceEvent(
                null, "test", TraceEventType.Warning, 0, $"event-{index}"))).ToArray();
        await Task.WhenAll(writers);

        string[] lines = await File.ReadAllLinesAsync(first.LogPath);
        Assert.Equal(80, lines.Length);
        Assert.Equal(80, lines.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void UnwritableLogExposesWriterHealthInsteadOfChangingTheOperation()
    {
        string blocked = Path.Combine(directory, "blocked");
        Directory.CreateDirectory(directory);
        File.WriteAllText(blocked, "not a directory");
        using var listener = new PersistentTraceLog("OpenBlade.Blocked", "test", blocked, []);
        listener.TraceEvent(null, "test", TraceEventType.Warning, 0, "test failure");

        DiagnosticLogWriterHealth health = PersistentTraceLog.CaptureWriterHealth()
            .Single(value => value.Component == "OpenBlade.Blocked");
        Assert.True(health.FailedWrites > 0);
        Assert.NotNull(health.LastFailure?.ExceptionType);
    }

    [Fact]
    public void HealthSidecarPreservesFailureAcrossAHealthyWriterRestart()
    {
        const string component = "OpenBlade.RestartHealth";
        using (var listener = new PersistentTraceLog(component, "before", directory, []))
        {
            Directory.CreateDirectory(listener.LogPath);
            listener.TraceEvent(null, "test", TraceEventType.Warning, 0, "cannot append to a directory");
            Directory.Delete(listener.LogPath);
        }
        using (var listener = new PersistentTraceLog(component, "after", directory, []))
        {
            listener.TraceEvent(null, "test", TraceEventType.Information, 0, "recovered writer");
        }

        string path = Path.Combine(directory, "health", component + ".health.json");
        DiagnosticLogWriterHealth health = System.Text.Json.JsonSerializer.Deserialize<DiagnosticLogWriterHealth>(
            File.ReadAllText(path))!;
        Assert.True(health.FailedWrites > 0);
        Assert.Equal("before", health.LastFailureVersion);
        Assert.Equal("after", health.Version);
        Assert.NotNull(health.LastFailure);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
