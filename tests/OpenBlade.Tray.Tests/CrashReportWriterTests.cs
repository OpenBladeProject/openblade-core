using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class CrashReportWriterTests : IDisposable
{
    private readonly string testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"OpenBlade.CrashReportWriter.Tests-{Guid.NewGuid():N}");

    [Fact]
    public void TryWrite_PersistsExceptionStackAndProcessContext()
    {
        var time = new DateTimeOffset(2026, 7, 21, 20, 15, 30, TimeSpan.Zero);
        var writer = new CrashReportWriter(
            "OpenBlade.Tray",
            "0.1.0+12345678",
            testDirectory,
            new FixedTimeProvider(time));
        var exception = new InvalidOperationException(
            "outer failure",
            new ArgumentException("inner failure"));

        string? path = writer.TryWrite(
            "WinUI unhandled exception",
            exception,
            processTerminating: true);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.StartsWith(
            "OpenBlade.Tray-crash-20260721T201530000Z-",
            Path.GetFileName(path),
            StringComparison.Ordinal);

        string report = File.ReadAllText(path);
        Assert.Contains("UTC time: 2026-07-21T20:15:30.0000000+00:00", report);
        Assert.Contains("Source: WinUI unhandled exception", report);
        Assert.Contains("Process terminating: Yes", report);
        Assert.Contains("Version: 0.1.0+12345678", report);
        Assert.Contains("Process ID:", report);
        Assert.Contains("OS:", report);
        Assert.Contains("Runtime:", report);
        Assert.Contains("System.InvalidOperationException: outer failure", report);
        Assert.Contains("System.ArgumentException: inner failure", report);
    }

    [Fact]
    public void TryWrite_ReturnsNullWhenReportCannotBeWritten()
    {
        var writer = new CrashReportWriter(
            "OpenBlade.Tray",
            "0.1.0",
            "invalid\0path");

        Exception? thrown = Record.Exception(() =>
        {
            string? path = writer.TryWrite(
                "AppDomain unhandled exception",
                new InvalidOperationException("original failure"),
                processTerminating: true);
            Assert.Null(path);
        });

        Assert.Null(thrown);
    }

    [Fact]
    public void TryWrite_RetainsOnlyTheNewestTwentyReports()
    {
        var writer = new CrashReportWriter(
            "OpenBlade.Tray",
            "0.1.0",
            testDirectory);

        for (int index = 0; index < 24; index++)
        {
            Assert.NotNull(writer.TryWrite(
                "test",
                new InvalidOperationException($"failure {index}"),
                processTerminating: false));
        }

        Assert.Equal(
            20,
            Directory.GetFiles(testDirectory, "OpenBlade.Tray-crash-*.log").Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
