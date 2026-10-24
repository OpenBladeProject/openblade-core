using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>
/// Persists infrequent user-mode warnings and errors without introducing a
/// background logging worker. Each process receives its own bounded pair of
/// files, and common user or machine identifiers are removed before writing.
/// </summary>
public sealed class PersistentTraceLog : TraceListener
{
    internal const int MaximumFileBytes = 128 * 1024;
    private const int MaximumMessageCharacters = 16 * 1024;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly object sync = new();
    private readonly string componentName;
    private readonly string componentVersion;
    private readonly string logPath;
    private readonly string previousLogPath;
    private readonly string[] sensitiveTokens;
    private readonly TimeProvider timeProvider;
    private int disposed;

    public PersistentTraceLog(
        string componentName,
        string componentVersion,
        string logDirectory,
        IEnumerable<string>? sensitiveTokens = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        this.componentName = componentName;
        this.componentVersion = componentVersion;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        string safeComponentName = string.Concat(componentName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        logPath = Path.Combine(logDirectory, $"{safeComponentName}.log");
        previousLogPath = Path.Combine(logDirectory, $"{safeComponentName}.previous.log");
        this.sensitiveTokens = (sensitiveTokens ?? UserModeDiagnosticTextRedactor.CreateDefaultSensitiveTokens())
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(token => token.Length)
            .ToArray();
    }

    public string LogPath => logPath;

    public static string DefaultLogDirectory
    {
        get
        {
            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Path.GetTempPath();
            }

            return Path.Combine(localAppData, "OpenBlade", "Logs");
        }
    }

    public static IDisposable RegisterDefault(
        string componentName,
        string componentVersion)
    {
        try
        {
            var listener = new PersistentTraceLog(
                componentName,
                componentVersion,
                DefaultLogDirectory);
            Trace.Listeners.Add(listener);
            listener.Append(TraceEventType.Information, "Persistent logging started.");
            return new Registration(listener);
        }
        catch
        {
            return EmptyRegistration.Instance;
        }
    }

    public override void Write(string? message) =>
        Append(TraceEventType.Verbose, message ?? string.Empty);

    public override void WriteLine(string? message) =>
        Append(TraceEventType.Verbose, message ?? string.Empty);

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? message) => Append(eventType, message ?? string.Empty);

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? format,
        params object?[]? args)
    {
        string message;
        try
        {
            message = args is { Length: > 0 }
                ? string.Format(CultureInfo.InvariantCulture, format ?? string.Empty, args)
                : format ?? string.Empty;
        }
        catch (FormatException)
        {
            message = format ?? string.Empty;
        }

        Append(eventType, message);
    }

    protected override void Dispose(bool disposing)
    {
        Interlocked.Exchange(ref disposed, 1);
        base.Dispose(disposing);
    }

    private void Append(TraceEventType eventType, string message)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        try
        {
            string sanitized = Redact(message)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            if (sanitized.Length > MaximumMessageCharacters)
            {
                sanitized = sanitized[..MaximumMessageCharacters] + " [truncated]";
            }

            string line = string.Create(
                CultureInfo.InvariantCulture,
                $"{timeProvider.GetUtcNow():O} [{componentName}] "
                    + $"[{componentVersion}] [{eventType}] {sanitized}{Environment.NewLine}");
            byte[] bytes = Utf8WithoutBom.GetBytes(line);
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                string? directory = Path.GetDirectoryName(logPath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                Directory.CreateDirectory(directory);
                long currentLength = File.Exists(logPath)
                    ? new FileInfo(logPath).Length
                    : 0;
                if (currentLength + bytes.Length > MaximumFileBytes)
                {
                    File.Move(logPath, previousLogPath, overwrite: true);
                }

                using var stream = new FileStream(
                    logPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.None);
                stream.Write(bytes);
            }
        }
        catch
        {
            // Diagnostics must never change product behavior.
        }
    }

    private string Redact(string value)
    {
        return UserModeDiagnosticTextRedactor.Redact(value, sensitiveTokens);
    }

    private sealed class Registration(PersistentTraceLog listener) : IDisposable
    {
        private PersistentTraceLog? listener = listener;

        public void Dispose()
        {
            PersistentTraceLog? owned = Interlocked.Exchange(ref listener, null);
            if (owned is null)
            {
                return;
            }

            Trace.Listeners.Remove(owned);
            owned.Dispose();
        }
    }

    private sealed class EmptyRegistration : IDisposable
    {
        internal static EmptyRegistration Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

internal static class UserModeDiagnosticTextRedactor
{
    internal static string RedactDefault(string value) => Redact(
        value,
        CreateDefaultSensitiveTokens()
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(token => token.Length));

    internal static string Redact(string value, IEnumerable<string> sensitiveTokens)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(sensitiveTokens);
        foreach (string token in sensitiveTokens)
        {
            value = value.Replace(
                token,
                "[REDACTED]",
                StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    internal static IEnumerable<string> CreateDefaultSensitiveTokens()
    {
        yield return Environment.UserName;
        yield return Environment.MachineName;
        yield return Environment.CurrentDirectory;
        yield return AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }
}

internal static class UserModeDiagnosticLogAppender
{
    private const string ArchiveDirectory = "user-mode-logs";
    private const int MaximumLogFiles = 32;

    internal static byte[] TryAppendLogs(
        byte[] archiveBytes,
        string logDirectory,
        CancellationToken cancellationToken,
        Action<DiagnosticExportSection>? report = null)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        var failures = new List<DiagnosticFailure>();
        int included = 0;
        try
        {
            if (!Directory.Exists(logDirectory))
            {
                report?.Invoke(new("UserModeLogs", DiagnosticExportStatus.NotObserved));
                return archiveBytes;
            }

            string[] files = Directory.EnumerateFiles(logDirectory, "OpenBlade.*.log")
                .Take(MaximumLogFiles + 1)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0)
            {
                report?.Invoke(new("UserModeLogs", DiagnosticExportStatus.NotObserved));
                return archiveBytes;
            }
            if (files.Length > MaximumLogFiles)
            {
                failures.Add(new("Diagnostics", "Export", "UserModeLogs", "LogCountLimit"));
            }

            using var destination = new MemoryStream();
            destination.Write(archiveBytes);
            destination.Position = 0;
            using (var archive = new ZipArchive(
                destination,
                ZipArchiveMode.Update,
                leaveOpen: true))
            {
                foreach (string path in files.Take(MaximumLogFiles))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] content;
                    try
                    {
                        // Read each source before changing the ZIP: one locked or oversized
                        // log must not discard every healthy log already collected.
                        using Stream source = new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        using var bounded = new MemoryStream();
                        byte[] buffer = new byte[8192];
                        int count;
                        while ((count = source.Read(buffer, 0, Math.Min(buffer.Length,
                            PersistentTraceLog.MaximumFileBytes + 1 - (int)bounded.Length))) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            bounded.Write(buffer, 0, count);
                        }
                        if (bounded.Length > PersistentTraceLog.MaximumFileBytes)
                        {
                            failures.Add(new("Diagnostics", "Export", "UserModeLogRead", "LogTooLarge"));
                            continue;
                        }
                        content = bounded.ToArray();
                    }
                    catch (Exception exception) when (exception is IOException
                        or UnauthorizedAccessException or NotSupportedException)
                    {
                        failures.Add(DiagnosticFailure.FromException(
                            "Diagnostics", "Export", "UserModeLogRead", "LogReadFailed", exception));
                        continue;
                    }
                    string entryName = $"{ArchiveDirectory}/{Path.GetFileName(path)}";
                    archive.GetEntry(entryName)?.Delete();
                    ZipArchiveEntry entry = archive.CreateEntry(
                        entryName,
                        CompressionLevel.Optimal);
                    using Stream target = entry.Open();
                    target.Write(content);
                    included++;
                }
            }

            report?.Invoke(new("UserModeLogs", failures.Count == 0 ? DiagnosticExportStatus.Included
                : included > 0 ? DiagnosticExportStatus.Partial : DiagnosticExportStatus.Failed,
                included, failures));
            return destination.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            report?.Invoke(new("UserModeLogs", DiagnosticExportStatus.Failed, Failures:
                [DiagnosticFailure.FromException("Diagnostics", "Export", "UserModeLogs", "LogAppendFailed", exception)]));
            return archiveBytes;
        }
    }
}
