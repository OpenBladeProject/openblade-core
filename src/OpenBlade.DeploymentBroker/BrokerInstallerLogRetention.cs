using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenBlade.DeploymentBroker;

// Diagnostics are data only: never retain or execute the rest of TEMP, an
// installer, or a caller-supplied path. All paths derive from the request ID.
internal sealed class BrokerInstallerLogRetention(BrokerPaths paths)
{
    internal const int MaximumRequests = 8;
    internal const int MaximumLogs = 32;
    internal const int MaximumEntries = 4096;
    internal const int MaximumDepth = 16;
    internal const int MaximumLogBytes = 2 * 1024 * 1024;
    internal const int MaximumRequestBytes = 8 * 1024 * 1024;
    private const int MaximumIndexBytes = 64 * 1024;

    internal static async Task<bool> RetainAndRecordAsync(
        BrokerPaths paths,
        BrokerDurableRecordStore records,
        Guid requestId,
        DateTimeOffset now,
        ILogger logger)
    {
        string detail;
        try
        {
            detail = await new BrokerInstallerLogRetention(paths).RetainAsync(requestId)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            logger.LogError(exception, "Installer diagnostic logs could not be fully retained for {RequestId}.", requestId);
            detail = "installer-logs-unavailable";
        }

        // A diagnostic failure does not turn verified rollback into an unknown
        // installation. Persist the separate result before staging is deleted.
        await records.AppendTransitionAsync(
            new BrokerTransition(1, requestId, BrokerTransitionKind.InstallerExited, now, detail),
            CancellationToken.None).ConfigureAwait(false);
        return detail != "installer-logs-unavailable"
            || !Directory.Exists(Path.Combine(paths.StagingDirectory, requestId.ToString("N")));
    }

    internal async Task<string> RetainAsync(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty request ID is required.", nameof(requestId));
        }

        VerifyDirectory(paths.RootDirectory);
        if (Directory.Exists(paths.InstallerLogDirectory))
        {
            VerifyDirectory(paths.InstallerLogDirectory);
        }
        paths.EnsureDirectory(paths.InstallerLogDirectory);
        VerifyDirectory(paths.InstallerLogDirectory);
        string destination = Path.Combine(paths.InstallerLogDirectory, requestId.ToString("N"));
        string staging = Path.Combine(paths.StagingDirectory, requestId.ToString("N"));
        List<string> archives = [];
        foreach (string archive in Directory.EnumerateFileSystemEntries(paths.InstallerLogDirectory))
        {
            if (archives.Count >= MaximumRequests + 1
                || !Guid.TryParseExact(Path.GetFileName(archive), "N", out _)
                || !Directory.Exists(archive))
            {
                throw new InvalidDataException("The installer log archive has an invalid entry or count.");
            }
            ValidateArchive(archive);
            archives.Add(archive);
        }

        if (Directory.Exists(destination) && File.Exists(Path.Combine(destination, "index.json")))
        {
            // A completed snapshot is immutable and survives restart cleanup.
            byte[] bytes = await BrokerStateFile.ReadAsync(
                Path.Combine(destination, "index.json"), MaximumIndexBytes, CancellationToken.None)
                .ConfigureAwait(false);
            RetentionIndex? existingIndex = JsonSerializer.Deserialize<RetentionIndex>(bytes);
            if (existingIndex is null || existingIndex.RequestId != requestId || existingIndex.Logs is null
                || existingIndex.Logs.Count > MaximumLogs)
            {
                throw new InvalidDataException("The retained installer log index is invalid.");
            }
            for (int number = 0; number < existingIndex.Logs.Count; number++)
            {
                RetainedLog log = existingIndex.Logs[number];
                string expectedName = $"log-{number:D2}.log";
                if (log is null || log.File != expectedName || log.RetainedBytes < 0
                    || log.OriginalBytes < log.RetainedBytes
                    || !File.Exists(Path.Combine(destination, expectedName))
                    || new FileInfo(Path.Combine(destination, expectedName)).Length != log.RetainedBytes)
                {
                    throw new InvalidDataException("A retained installer log does not match its index.");
                }
            }
            Rotate(archives.Where(archive => archive != destination).ToList());
            return existingIndex.Truncated ? "installer-logs-truncated"
                : existingIndex.Logs.Count == 0 ? "installer-logs-none" : "installer-logs-retained";
        }
        if (Directory.Exists(destination))
        {
            DeleteArchive(destination);
            archives.Remove(destination);
        }
        if (archives.Count > MaximumRequests)
        {
            throw new InvalidDataException("The installer log archive requires successful rotation before another snapshot.");
        }
        if (!Directory.Exists(staging))
        {
            return "installer-logs-unavailable";
        }

        VerifyDirectory(paths.StagingDirectory);
        VerifyDirectory(staging);
        List<string> logs = [];
        int entries = 0;
        Scan(staging, 0);

        paths.EnsureDirectory(destination);
        VerifyDirectory(destination);
        List<RetainedLog> retained = [];
        int remaining = MaximumRequestBytes;
        bool truncated = false;
        foreach (string log in logs)
        {
            if (remaining == 0)
            {
                truncated = true;
                break;
            }
            ProtectedBrokerStorage.RejectReparsePoint(log);
            await using FileStream source = new(log, FileMode.Open, FileAccess.Read, FileShare.Read);
            long originalBytes = source.Length;
            int bytes = (int)Math.Min(originalBytes, Math.Min(remaining, MaximumLogBytes));
            source.Seek(originalBytes - bytes, SeekOrigin.Begin);
            string name = $"log-{retained.Count:D2}.log";
            string target = Path.Combine(destination, name);
            await using (FileStream output = new(target, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                byte[] buffer = new byte[81920];
                int left = bytes;
                while (left > 0)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, left)))
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new IOException("An installer log changed during retention.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    left -= read;
                }
                output.Flush(flushToDisk: true);
            }
            paths.ProtectFile(target);
            paths.VerifyFile(target);
            retained.Add(new RetainedLog(name, Path.GetRelativePath(staging, log), originalBytes, bytes));
            remaining -= bytes;
            truncated |= originalBytes != bytes;
        }

        byte[] index = JsonSerializer.SerializeToUtf8Bytes(new RetentionIndex(requestId, truncated, retained));
        if (index.Length > MaximumIndexBytes)
        {
            throw new InvalidDataException("The installer log index exceeds its fixed size limit.");
        }
        string indexPath = Path.Combine(destination, "index.json");
        await using (FileStream stream = new(indexPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(index).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        paths.ProtectFile(indexPath);
        paths.VerifyFile(indexPath);

        // Publish the current evidence before removing any older request.
        Rotate(archives);
        return truncated ? "installer-logs-truncated"
            : retained.Count == 0 ? "installer-logs-none" : "installer-logs-retained";

        void Scan(string directory, int depth)
        {
            if (depth > MaximumDepth)
            {
                throw new InvalidDataException("Installer temporary directories exceed the diagnostic depth limit.");
            }
            ProtectedBrokerStorage.RejectReparsePoint(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > MaximumEntries)
                {
                    throw new InvalidDataException("Installer temporary entries exceed the diagnostic scan limit.");
                }
                string relative = Path.GetRelativePath(staging, Path.GetFullPath(entry));
                if (Path.IsPathRooted(relative) || relative == ".."
                    || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || relative.Length > 512)
                {
                    throw new InvalidDataException("An installer diagnostic path is outside its fixed bounds.");
                }
                ProtectedBrokerStorage.RejectReparsePoint(entry);
                if (Directory.Exists(entry))
                {
                    Scan(entry, depth + 1);
                }
                else if (string.Equals(Path.GetExtension(entry), ".log", StringComparison.OrdinalIgnoreCase))
                {
                    if (logs.Count >= MaximumLogs)
                    {
                        throw new InvalidDataException("Installer logs exceed the diagnostic count limit.");
                    }
                    logs.Add(entry);
                }
            }
        }
    }

    private void VerifyDirectory(string path)
    {
        ProtectedBrokerStorage.RejectReparsePoint(path);
        paths.VerifyDirectory(path);
    }

    private void Rotate(List<string> olderArchives)
    {
        foreach (string old in olderArchives.OrderBy(Directory.GetLastWriteTimeUtc)
            .ThenBy(path => path, StringComparer.Ordinal).Take(Math.Max(0, olderArchives.Count + 1 - MaximumRequests)))
        {
            DeleteArchive(old);
        }
    }

    private void ValidateArchive(string directory)
    {
        VerifyDirectory(directory);
        int count = 0;
        long bytes = 0;
        foreach (string file in Directory.EnumerateFileSystemEntries(directory))
        {
            ProtectedBrokerStorage.RejectReparsePoint(file);
            string name = Path.GetFileName(file);
            bool index = name == "index.json";
            bool log = name.Length == 10 && name.StartsWith("log-", StringComparison.Ordinal)
                && name.EndsWith(".log", StringComparison.Ordinal)
                && int.TryParse(name.AsSpan(4, 2), out int number) && number < MaximumLogs;
            if (++count > MaximumLogs + 1 || Directory.Exists(file) || (!index && !log))
            {
                throw new InvalidDataException("The installer log archive contains an unexpected entry.");
            }
            paths.VerifyFile(file);
            long length = new FileInfo(file).Length;
            bytes += length;
            if (length > (index ? MaximumIndexBytes : MaximumLogBytes)
                || bytes > MaximumRequestBytes + MaximumIndexBytes)
            {
                throw new InvalidDataException("The installer log archive exceeds its fixed size limit.");
            }
        }
    }

    private void DeleteArchive(string directory)
    {
        ValidateArchive(directory);
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            File.Delete(file);
        }
        Directory.Delete(directory, recursive: false);
    }

    private sealed record RetainedLog(string File, string RelativeSource, long OriginalBytes, int RetainedBytes);
    private sealed record RetentionIndex(Guid RequestId, bool Truncated, List<RetainedLog> Logs);
}
