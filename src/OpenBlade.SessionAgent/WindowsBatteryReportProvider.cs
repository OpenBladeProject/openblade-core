using System.Diagnostics;
using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

internal sealed record WindowsBatteryReportProcessResult(
    int ExitCode,
    string Output,
    string Error);

internal interface IWindowsBatteryReportProcessRunner
{
    Task<WindowsBatteryReportProcessResult> RunAsync(
        string outputPath,
        CancellationToken cancellationToken);
}

internal interface IWindowsBatteryReportChildProcess : IDisposable
{
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill(bool entireProcessTree);
}

internal sealed class WindowsBatteryReportProcessRunner : IWindowsBatteryReportProcessRunner
{
    internal const int MaximumCapturedCharacters = 16 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private readonly Func<ProcessStartInfo, IWindowsBatteryReportChildProcess> startProcess;
    private readonly TimeSpan timeout;

    internal WindowsBatteryReportProcessRunner()
        : this(StartProcess, DefaultTimeout)
    {
    }

    internal WindowsBatteryReportProcessRunner(
        Func<ProcessStartInfo, IWindowsBatteryReportChildProcess> startProcess,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(startProcess);
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        this.startProcess = startProcess;
        this.timeout = timeout;
    }

    public async Task<WindowsBatteryReportProcessResult> RunAsync(
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ProcessStartInfo startInfo = CreateStartInfo(outputPath);
        using IWindowsBatteryReportChildProcess process = startProcess(startInfo);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        Task<string> output = ReadBoundedAsync(
            process.StandardOutput,
            timeoutCancellation.Token);
        Task<string> error = ReadBoundedAsync(
            process.StandardError,
            timeoutCancellation.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            return new WindowsBatteryReportProcessResult(
                process.ExitCode,
                await output.ConfigureAwait(false),
                await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            TryKill(process);
            await DrainAfterStopAsync(process, output, error).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Windows battery report generation timed out.");
        }
        catch
        {
            TryKill(process);
            await DrainAfterStopAsync(process, output, error).ConfigureAwait(false);
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string outputPath)
    {
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsDirectory))
        {
            throw new InvalidOperationException("The Windows directory is unavailable.");
        }
        string executable = Path.Combine(windowsDirectory, "System32", "powercfg.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(outputPath)!,
        };
        start.ArgumentList.Add("/batteryreport");
        start.ArgumentList.Add("/duration");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("/xml");
        start.ArgumentList.Add("/output");
        start.ArgumentList.Add(outputPath);
        return start;
    }

    private static IWindowsBatteryReportChildProcess StartProcess(ProcessStartInfo startInfo) =>
        new SystemChildProcess(Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows battery report generation did not start."));

    private static async Task<string> ReadBoundedAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        char[] buffer = new char[1024];
        while (true)
        {
            int read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return builder.ToString();
            }
            if (builder.Length > MaximumCapturedCharacters - read)
            {
                throw new InvalidDataException("Windows battery report process output exceeded its limit.");
            }
            builder.Append(buffer, 0, read);
        }
    }

    private static void TryKill(IWindowsBatteryReportChildProcess process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(
                nameof(WindowsBatteryReportProvider),
                "TerminateReportProcess",
                exception);
        }
    }

    private static async Task DrainAfterStopAsync(
        IWindowsBatteryReportChildProcess process,
        Task<string> output,
        Task<string> error)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(StopTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(
                nameof(WindowsBatteryReportProvider),
                "WaitForTerminatedReportProcess",
                exception);
        }
        try
        {
            await Task.WhenAll(output, error)
                .WaitAsync(StopTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(
                nameof(WindowsBatteryReportProvider),
                "DrainTerminatedReportProcess",
                exception);
        }
    }

    private sealed class SystemChildProcess(Process process)
        : IWindowsBatteryReportChildProcess
    {
        public TextReader StandardOutput => process.StandardOutput;
        public TextReader StandardError => process.StandardError;
        public int ExitCode => process.ExitCode;
        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            process.WaitForExitAsync(cancellationToken);
        public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);
        public void Dispose() => process.Dispose();
    }
}

public sealed class WindowsBatteryReportProvider
{
    private const string ReportFileName = "battery-report.xml";
    private const string DiagnosticComponent = "session-agent";
    private const string DiagnosticOperation = "windows-battery-report";
    private readonly IWindowsBatteryReportProcessRunner processRunner;
    private readonly Func<string> createTemporaryDirectory;
    private readonly Action<DiagnosticFailure> diagnosticFailureSink;

    public WindowsBatteryReportProvider()
        : this(
            new WindowsBatteryReportProcessRunner(),
            static () => Directory.CreateTempSubdirectory("OpenBlade.BatteryReport.").FullName,
            TraceFailure)
    {
    }

    internal WindowsBatteryReportProvider(
        IWindowsBatteryReportProcessRunner processRunner,
        Func<string> createTemporaryDirectory,
        Action<DiagnosticFailure>? diagnosticFailureSink = null)
    {
        this.processRunner = processRunner
            ?? throw new ArgumentNullException(nameof(processRunner));
        this.createTemporaryDirectory = createTemporaryDirectory
            ?? throw new ArgumentNullException(nameof(createTemporaryDirectory));
        this.diagnosticFailureSink = diagnosticFailureSink ?? TraceFailure;
    }

    public async Task<WindowsBatteryReport?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        string? temporaryDirectory = null;
        string stage = "temporary-directory";
        try
        {
            temporaryDirectory = createTemporaryDirectory();
            string reportPath = Path.Combine(temporaryDirectory, ReportFileName);
            stage = "process";
            WindowsBatteryReportProcessResult result = await processRunner
                .RunAsync(reportPath, cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                diagnosticFailureSink(new DiagnosticFailure(
                    DiagnosticComponent,
                    DiagnosticOperation,
                    stage,
                    "process-exit") with
                {
                    ProcessExitCode = result.ExitCode,
                });
                return null;
            }
            if (!File.Exists(reportPath))
            {
                diagnosticFailureSink(new DiagnosticFailure(
                    DiagnosticComponent,
                    DiagnosticOperation,
                    "output",
                    "output-missing"));
                return null;
            }

            stage = "output-read";
            byte[] xml = await ReadBoundedFileAsync(reportPath, cancellationToken)
                .ConfigureAwait(false);
            stage = "parse";
            if (!WindowsBatteryReportParser.TryParse(xml, out WindowsBatteryReport? report))
            {
                diagnosticFailureSink(new DiagnosticFailure(
                    DiagnosticComponent,
                    DiagnosticOperation,
                    stage,
                    "parse-rejected"));
                return null;
            }
            return report;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            diagnosticFailureSink(DiagnosticFailure.FromException(
                DiagnosticComponent,
                DiagnosticOperation,
                stage,
                "exception",
                exception));
            // Keep the existing dispatcher Failed classification for process-start
            // failures while retaining the more specific provider phase as evidence.
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or TimeoutException)
        {
            diagnosticFailureSink(DiagnosticFailure.FromException(
                DiagnosticComponent,
                DiagnosticOperation,
                stage,
                "exception",
                exception));
            return null;
        }
        finally
        {
            if (temporaryDirectory is not null)
            {
                TryDeleteDirectory(temporaryDirectory);
            }
        }
    }

    private static void TraceFailure(DiagnosticFailure failure) =>
        Trace.TraceWarning("Windows battery report failure: {0}", failure);

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > WindowsBatteryReportParser.MaximumBytes)
        {
            throw new InvalidDataException("Windows battery report size is invalid.");
        }
        byte[] result = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("Windows battery report changed while it was read.");
        }
        return result;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticTrace.TraceException(
                nameof(WindowsBatteryReportProvider),
                "DeleteReportDirectory",
                exception);
        }
    }
}
