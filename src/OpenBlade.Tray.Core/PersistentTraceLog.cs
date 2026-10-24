using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>
/// Persists infrequent user-mode warnings and errors without introducing a
/// background logging worker. Components retain four bounded log generations,
/// shared safely by supervised processes, and common user or machine identifiers are removed before writing.
/// </summary>
public sealed class PersistentTraceLog : TraceListener
{
    internal const int MaximumFileBytes = 1024 * 1024;
    private const int MaximumMessageCharacters = 896 * 1024;
    private static readonly string ProcessInstanceId = Guid.NewGuid().ToString("N");
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly object HealthLock = new();
    private static readonly Dictionary<string, DiagnosticLogWriterHealth> WriterHealth = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private readonly string writerMutexName;
    private readonly string componentName;
    private readonly string componentVersion;
    private readonly string logPath;
    private readonly string logDirectory;
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
        this.logDirectory = logDirectory;
        this.componentVersion = componentVersion;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        string safeComponentName = string.Concat(componentName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        logPath = Path.Combine(logDirectory, $"{safeComponentName}.log");
        previousLogPath = Path.Combine(logDirectory, $"{safeComponentName}.previous.log");
        writerMutexName = "Local\\OpenBlade.Log." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(logPath).ToUpperInvariant())));
        UpdateHealth();

        this.sensitiveTokens = (sensitiveTokens ?? UserModeDiagnosticTextRedactor.CreateDefaultSensitiveTokens())
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(token => token.Length)
            .ToArray();
    }

    public static IReadOnlyList<DiagnosticLogWriterHealth> CaptureWriterHealth()
    {
        lock (HealthLock)
        {
            return WriterHealth.Values.OrderBy(value => value.Component, StringComparer.Ordinal).ToArray();
        }
    }

    private void UpdateHealth(Exception? failure = null, bool truncated = false)
    {
        DiagnosticLogWriterHealth health = RecordHealth(componentName, componentVersion, failure, truncated, "Append");
        PersistHealth(logDirectory, health, failure is null ? 0 : 1, truncated ? 1 : 0);
    }

    private static DiagnosticLogWriterHealth RecordHealth(
        string component, string version, Exception? failure, bool truncated, string stage)
    {
        lock (HealthLock)
        {
            WriterHealth.TryGetValue(component, out DiagnosticLogWriterHealth? previous);
            previous ??= new(component, 0, 0, null);
            DiagnosticLogWriterHealth current = previous with
            {
                FailedWrites = previous.FailedWrites + (failure is null ? 0 : 1),
                TruncatedEvents = previous.TruncatedEvents + (truncated ? 1 : 0),
                LastFailure = failure is null ? previous.LastFailure : DiagnosticFailure.FromException(
                    "Diagnostics", "PersistentLog", stage, "LogWriteFailed", failure),
                UpdatedAt = DateTimeOffset.UtcNow,
                ProcessId = Environment.ProcessId,
                Version = version,
                LastFailureAt = failure is null ? previous.LastFailureAt : DateTimeOffset.UtcNow,
                LastFailureVersion = failure is null ? previous.LastFailureVersion : version,
            };
            if (WriterHealth.ContainsKey(component) || WriterHealth.Count < 32)
            {
                WriterHealth[component] = current;
            }
            return current;
        }
    }

    private static void PersistHealth(
        string directory, DiagnosticLogWriterHealth current, int newFailures, int newTruncations)
    {
        string? temporary = null;
        try
        {
            string healthDirectory = Path.Combine(directory, "health");
            string safeName = string.Concat(current.Component.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            string path = Path.Combine(healthDirectory, safeName + ".health.json");
            string mutexName = "Local\\OpenBlade.LogHealth." + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
            using var mutex = new Mutex(false, mutexName);
            bool owned;
            try
            {
                owned = mutex.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }
            if (!owned)
            {
                throw new TimeoutException("Timed out waiting for diagnostic writer health.");
            }
            try
            {
                DiagnosticLogWriterHealth? previous = null;
                if (File.Exists(path))
                {
                    using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    if (source.Length > 2 * 1024 * 1024)
                    {
                        throw new InvalidDataException("The writer-health file exceeds its bound.");
                    }
                    previous = JsonSerializer.Deserialize<DiagnosticLogWriterHealth>(source);
                    if (previous is not null && (previous.Component != current.Component
                        || previous.FailedWrites < 0 || previous.TruncatedEvents < 0))
                    {
                        throw new InvalidDataException("The writer-health identity or counts are invalid.");
                    }
                }
                DiagnosticLogWriterHealth combined = current with
                {
                    FailedWrites = checked((previous?.FailedWrites ?? 0) + newFailures),
                    TruncatedEvents = checked((previous?.TruncatedEvents ?? 0) + newTruncations),
                    LastFailure = newFailures > 0 ? current.LastFailure : previous?.LastFailure,
                    LastFailureAt = newFailures > 0 ? current.LastFailureAt : previous?.LastFailureAt,
                    LastFailureVersion = newFailures > 0 ? current.LastFailureVersion : previous?.LastFailureVersion,
                };
                Directory.CreateDirectory(healthDirectory);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(combined));
                File.Move(temporary, path, overwrite: true);
                temporary = null;
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch
        {
            // A failed health sink must never recurse into the trace sink.
            lock (HealthLock)
            {
                if (WriterHealth.TryGetValue(current.Component, out DiagnosticLogWriterHealth? health))
                {
                    WriterHealth[current.Component] = health with
                    {
                        HealthPersistenceFailures = health.HealthPersistenceFailures + 1,
                    };
                }
            }
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
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
        PersistentTraceLog? listener = null;
        try
        {
            listener = new PersistentTraceLog(
                componentName,
                componentVersion,
                DefaultLogDirectory);
            Trace.Listeners.Add(listener);
            listener.Append(TraceEventType.Information, "Persistent logging started.");
            return new Registration(listener);
        }
        catch (Exception exception)
        {
            if (listener is not null)
            {
                Trace.Listeners.Remove(listener);
                listener.Dispose();
            }
            DiagnosticLogWriterHealth health = RecordHealth(
                componentName, componentVersion, exception, truncated: false, "Initialize");
            try { PersistHealth(DefaultLogDirectory, health, 1, 0); }
            catch { /* Default-directory discovery can itself be unavailable. */ }
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

        int nativeError = Marshal.GetLastPInvokeError();
        try
        {
            string sanitized = Redact(message)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            if (sanitized.Length > MaximumMessageCharacters)
            {
                sanitized = sanitized[..MaximumMessageCharacters] + " [truncated]";
                UpdateHealth(truncated: true);
            }
            int byteBudget = MaximumFileBytes - 2048;
            if (Utf8WithoutBom.GetByteCount(sanitized) > byteBudget)
            {
                Utf8WithoutBom.GetEncoder().Convert(sanitized.AsSpan(), new byte[byteBudget], true,
                    out int charactersUsed, out _, out _);
                sanitized = sanitized[..charactersUsed] + " [truncated UTF8]";
                UpdateHealth(truncated: true);
            }

            string line = string.Create(
                CultureInfo.InvariantCulture,
                $"{timeProvider.GetUtcNow():O} [{componentName}] "
                    + $"[{componentVersion}] [{eventType}] [PID={Environment.ProcessId};run={ProcessInstanceId}] {sanitized}{Environment.NewLine}");
            byte[] bytes = Utf8WithoutBom.GetBytes(line);
            using var writerMutex = new Mutex(false, writerMutexName);
            bool owned;
            try
            {
                owned = writerMutex.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }
            if (!owned)
            {
                throw new TimeoutException("Timed out waiting for the diagnostic log writer.");
            }
            try
            {
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
                    if (currentLength > 0 && currentLength + bytes.Length > MaximumFileBytes)
                    {
                        for (int generation = 3; generation >= 2; generation--)
                        {
                            string source = generation == 2 ? previousLogPath
                                : Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(logPath)}.previous.{generation - 1}.log");
                            string target = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(logPath)}.previous.{generation}.log");
                            if (File.Exists(source))
                            {
                                File.Move(source, target, overwrite: true);
                            }
                        }
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
            finally
            {
                writerMutex.ReleaseMutex();
            }
        }
        catch (Exception exception)
        {
            // Expose sink failures in export health without recursively logging to the failed sink.
            UpdateHealth(exception);
        }
        finally
        {
            Marshal.SetLastPInvokeError(nativeError);
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

public sealed record DiagnosticLogWriterHealth(
    string Component,
    long FailedWrites,
    long TruncatedEvents,
    DiagnosticFailure? LastFailure)
{
    public long HealthPersistenceFailures { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public int ProcessId { get; init; }
    public string? Version { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
    public string? LastFailureVersion { get; init; }
}

internal static class UserModeDiagnosticTextRedactor
{
    internal static string RedactDefault(string value) => Redact(
        value,
        CreateDefaultSensitiveTokens()
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(token => token.Length));

    internal static string Redact(string value, IEnumerable<string> sensitiveTokens) =>
        DiagnosticText.Redact(value, sensitiveTokens);

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
    private const int MaximumLogFiles = 128;
    private const long MaximumTotalBytes = 64L * 1024 * 1024;

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
        long includedBytes = 0;
        var items = new List<DiagnosticExportItem>();
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
                    long sourceLength;
                    bool truncated;
                    string name = Path.GetFileName(path);
                    if (includedBytes >= MaximumTotalBytes)
                    {
                        var limit = new DiagnosticFailure("Diagnostics", "Export", "UserModeLogRead", "LogBudgetLimit");
                        failures.Add(limit);
                        items.Add(new(name, DiagnosticExportStatus.Failed, null, 0, limit));
                        continue;
                    }
                    try
                    {
                        // Read each source before changing the ZIP: one locked or oversized
                        // log must not discard every healthy log already collected.
                        using Stream source = new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        sourceLength = source.Length;
                        int maximum = (int)Math.Min(PersistentTraceLog.MaximumFileBytes, MaximumTotalBytes - includedBytes);
                        truncated = sourceLength > maximum;
                        if (truncated)
                        {
                            source.Seek(-maximum, SeekOrigin.End);
                        }
                        using var bounded = new MemoryStream();
                        byte[] buffer = new byte[8192];
                        int count;
                        while (bounded.Length < maximum && (count = source.Read(buffer, 0,
                            (int)Math.Min(buffer.Length, maximum - bounded.Length))) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            bounded.Write(buffer, 0, count);
                        }
                        content = bounded.ToArray();
                        if (truncated)
                        {
                            failures.Add(new("Diagnostics", "Export", "UserModeLogRead", "LogTailTruncated")
                            {
                                Context = name,
                                DetailsTruncated = true,
                            });
                        }
                    }
                    catch (Exception exception) when (exception is IOException
                        or UnauthorizedAccessException or NotSupportedException)
                    {
                        DiagnosticFailure readFailure = DiagnosticFailure.FromException(
                            "Diagnostics", "Export", "UserModeLogRead", "LogReadFailed", exception) with
                        { Context = name };
                        failures.Add(readFailure);
                        items.Add(new(name, DiagnosticExportStatus.Failed, null, 0, readFailure));
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
                    includedBytes += content.Length;
                    items.Add(new(name, truncated ? DiagnosticExportStatus.Partial : DiagnosticExportStatus.Included,
                        sourceLength, content.Length));
                }
            }

            report?.Invoke(new("UserModeLogs", failures.Count == 0 ? DiagnosticExportStatus.Included
                : included > 0 ? DiagnosticExportStatus.Partial : DiagnosticExportStatus.Failed,
                included, failures, items));
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
