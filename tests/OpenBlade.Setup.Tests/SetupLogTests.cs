using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupLogTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.Setup.Tests",
        nameof(SetupLogTests),
        Guid.NewGuid().ToString("N"));

    public SetupLogTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void Failure_RecordsTheExceptionAndItsCausesInOrder()
    {
        string path = Path.Combine(directory, "setup.log");
        var log = new SetupLog(path);

        log.Write("Started: Install (bundle-managed: True)");
        log.WriteFailure(new InvalidOperationException(
            "Service replacement failed.",
            new IOException("The file is in use.")));

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.Contains("OpenBlade.Setup: ", line, StringComparison.Ordinal));
        Assert.EndsWith("Started: Install (bundle-managed: True)", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(
            "Failed: System.InvalidOperationException: Service replacement failed.",
            lines[1],
            StringComparison.Ordinal);
        Assert.EndsWith("Caused by: System.IO.IOException: The file is in use.", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateFailure_RecordsEveryRecoveryCause()
    {
        string path = Path.Combine(directory, "setup.log");

        new SetupLog(path).WriteFailure(new AggregateException(
            new IOException("first"),
            new UnauthorizedAccessException("second")));

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("Caused by: System.IO.IOException: first", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("Caused by: System.UnauthorizedAccessException: second", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOrUnwritableLog_NeverThrows()
    {
        new SetupLog(null).Write("ignored");
        new SetupLog(Path.Combine(directory, "missing", "setup.log")).Write("ignored");
        new SetupLog(directory).WriteFailure(new InvalidOperationException("ignored"));

        Assert.Empty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ProgressReporter_RecordsEachStepAndForwardsIt()
    {
        string path = Path.Combine(directory, "setup.log");
        var inner = new RecordingSetupProgressReporter();
        using var reporter = new LoggingSetupProgressReporter(inner, new SetupLog(path));

        reporter.Report(SetupProgressCode.StopServiceForReplacement);

        Assert.Single(inner.Events);
        Assert.EndsWith("Step: StopServiceForReplacement", Assert.Single(File.ReadAllLines(path)), StringComparison.Ordinal);
    }
}
