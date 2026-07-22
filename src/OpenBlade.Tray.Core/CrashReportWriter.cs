using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.Tray;

public sealed class CrashReportWriter
{
    private const int MaximumReportCharacters = 512 * 1024;
    private const int MaximumRetainedReports = 20;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly string componentName;
    private readonly string componentVersion;
    private readonly string crashDirectory;
    private readonly TimeProvider timeProvider;

    public CrashReportWriter(
        string componentName,
        string componentVersion,
        string crashDirectory,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(crashDirectory);

        this.componentName = componentName;
        this.componentVersion = componentVersion;
        this.crashDirectory = crashDirectory;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static CrashReportWriter CreateDefault(
        string componentName,
        string componentVersion)
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        return new CrashReportWriter(
            componentName,
            componentVersion,
            Path.Combine(localAppData, "OpenBlade", "Crashes"));
    }

    public string? TryWrite(
        string source,
        Exception exception,
        bool processTerminating)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            DateTimeOffset occurredAt = timeProvider.GetUtcNow();
            Directory.CreateDirectory(crashDirectory);

            string path = Path.Combine(
                crashDirectory,
                $"{componentName}-crash-{occurredAt:yyyyMMddTHHmmssfffZ}"
                    + $"-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
            string report = FormatReport(source, exception, processTerminating, occurredAt);
            if (report.Length > MaximumReportCharacters)
            {
                report = report[..MaximumReportCharacters]
                    + Environment.NewLine
                    + "[Crash report truncated]"
                    + Environment.NewLine;
            }

            File.WriteAllText(path, report, Utf8WithoutBom);
            PruneOldReports(path);
            return path;
        }
        catch
        {
            // Crash reporting must never replace or suppress the original failure.
            return null;
        }
    }

    private void PruneOldReports(string currentPath)
    {
        try
        {
            string pattern = $"{componentName}-crash-*.log";
            FileInfo[] otherReports = new DirectoryInfo(crashDirectory)
                .GetFiles(pattern)
                .Where(file => !string.Equals(
                    file.FullName,
                    currentPath,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            foreach (FileInfo stale in otherReports.Skip(MaximumRetainedReports - 1))
            {
                stale.Delete();
            }
        }
        catch
        {
            // Retention is best effort and must never interfere with reporting.
        }
    }

    private string FormatReport(
        string source,
        Exception exception,
        bool processTerminating,
        DateTimeOffset occurredAt)
    {
        var report = new StringBuilder();
        report.AppendLine("OpenBlade crash report");
        report.Append("UTC time: ").AppendLine(occurredAt.ToString("O", CultureInfo.InvariantCulture));
        report.Append("Source: ").AppendLine(source);
        report.Append("Process terminating: ").AppendLine(processTerminating ? "Yes" : "No");
        report.Append("Component: ").AppendLine(componentName);
        report.Append("Version: ").AppendLine(componentVersion);
        report.Append("Process ID: ").AppendLine(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        report.Append("Process path: ").AppendLine(Environment.ProcessPath ?? "Unavailable");
        report.Append("Working directory: ").AppendLine(Environment.CurrentDirectory);
        report.Append("OS: ").AppendLine(RuntimeInformation.OSDescription);
        report.Append("Runtime: ").AppendLine(RuntimeInformation.FrameworkDescription);
        report.Append("Process architecture: ").AppendLine(RuntimeInformation.ProcessArchitecture.ToString());
        report.Append("Managed thread ID: ").AppendLine(
            Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));
        report.AppendLine();
        report.AppendLine("Exception:");
        report.AppendLine(exception.ToString());
        return report.ToString();
    }
}
