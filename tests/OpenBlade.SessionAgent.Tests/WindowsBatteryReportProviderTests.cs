using System.Diagnostics;
using System.Text;
using OpenBlade.Session;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsBatteryReportProviderTests
{
    [Fact]
    public void CreateStartInfo_UsesOnlyTrustedPowerCfgAndFixedArguments()
    {
        string output = Path.Combine(Path.GetTempPath(), "private", "report.xml");

        ProcessStartInfo start = WindowsBatteryReportProcessRunner.CreateStartInfo(output);

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(Path.Combine(windows, "System32", "powercfg.exe"), start.FileName);
        Assert.Equal(
            ["/batteryreport", "/duration", "1", "/xml", "/output", output],
            start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
    }

    [Fact]
    public async Task ReadAsync_ParsesReportAndDeletesPrivateTemporaryDirectory()
    {
        string? createdDirectory = null;
        var runner = new CallbackRunner((outputPath, _) =>
        {
            File.WriteAllText(outputPath, MinimalReport);
            return Task.FromResult(new WindowsBatteryReportProcessResult(0, "", ""));
        });
        var provider = new WindowsBatteryReportProvider(
            runner,
            () => createdDirectory = CreateTemporaryDirectory());

        WindowsBatteryReport? report = await provider.ReadAsync();

        Assert.NotNull(report);
        Assert.NotNull(createdDirectory);
        Assert.False(Directory.Exists(createdDirectory));
        Assert.Equal(Utc(2026, 1, 2, 12), report.GeneratedAtUtc);
    }

    [Fact]
    public async Task ReadAsync_DeletesTemporaryDirectoryWhenCanceled()
    {
        string? createdDirectory = null;
        var runner = new CallbackRunner(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new WindowsBatteryReportProcessResult(0, "", "");
        });
        var provider = new WindowsBatteryReportProvider(
            runner,
            () => createdDirectory = CreateTemporaryDirectory());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.ReadAsync(cancellation.Token));

        Assert.NotNull(createdDirectory);
        Assert.False(Directory.Exists(createdDirectory));
    }

    [Fact]
    public async Task ReadAsync_ReturnsUnavailableAndCleansUpAfterProcessFailure()
    {
        string? createdDirectory = null;
        var runner = new CallbackRunner((_, _) => Task.FromResult(
            new WindowsBatteryReportProcessResult(1, "bounded output", "bounded error")));
        var provider = new WindowsBatteryReportProvider(
            runner,
            () => createdDirectory = CreateTemporaryDirectory());

        WindowsBatteryReport? report = await provider.ReadAsync();

        Assert.Null(report);
        Assert.NotNull(createdDirectory);
        Assert.False(Directory.Exists(createdDirectory));
    }

    [Fact]
    public async Task ProcessRunner_TimesOutAndKillsItsOwnedChildTree()
    {
        var child = new BlockingChildProcess();
        var runner = new WindowsBatteryReportProcessRunner(
            _ => child,
            TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<TimeoutException>(
            () => runner.RunAsync(Path.Combine(Path.GetTempPath(), "report.xml"), default));

        Assert.True(child.Killed);
        Assert.True(child.Disposed);
    }

    [Fact]
    public async Task ProcessRunner_KillsChildWhenCapturedOutputExceedsLimit()
    {
        var child = new CompletedChildProcess(
            new string('x', WindowsBatteryReportProcessRunner.MaximumCapturedCharacters + 1));
        var runner = new WindowsBatteryReportProcessRunner(
            _ => child,
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => runner.RunAsync(Path.Combine(Path.GetTempPath(), "report.xml"), default));

        Assert.True(child.Killed);
        Assert.True(child.Disposed);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"OpenBlade.BatteryReport.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private const string MinimalReport = """
        <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
          <ReportInformation>
            <ScanTime>2026-01-02T12:00:00Z</ScanTime>
            <ReportStartTime>2026-01-01T12:00:00Z</ReportStartTime>
          </ReportInformation>
        </BatteryReport>
        """;

    private sealed class CallbackRunner(
        Func<string, CancellationToken, Task<WindowsBatteryReportProcessResult>> callback)
        : IWindowsBatteryReportProcessRunner
    {
        public Task<WindowsBatteryReportProcessResult> RunAsync(
            string outputPath,
            CancellationToken cancellationToken) =>
            callback(outputPath, cancellationToken);
    }

    private sealed class BlockingChildProcess : IWindowsBatteryReportChildProcess
    {
        private readonly TaskCompletionSource exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TextReader StandardOutput { get; } = new StringReader("");
        public TextReader StandardError { get; } = new StringReader("");
        public int ExitCode => 0;
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            exited.Task.WaitAsync(cancellationToken);

        public void Kill(bool entireProcessTree)
        {
            Assert.True(entireProcessTree);
            Killed = true;
            exited.TrySetResult();
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class CompletedChildProcess(string output)
        : IWindowsBatteryReportChildProcess
    {
        public TextReader StandardOutput { get; } = new StringReader(output);
        public TextReader StandardError { get; } = new StringReader("");
        public int ExitCode => 0;
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Kill(bool entireProcessTree) => Killed = entireProcessTree;
        public void Dispose() => Disposed = true;
    }
}
