using System.Text.Json;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

// Fixed current-request records only. No history, output paths, state transitions or native queries.
internal sealed class BrokerDeploymentDiagnostics(BrokerPaths paths)
{
    internal DeploymentStatusSnapshot Read(bool installEnabled)
    {
        byte[] inflight;
        BrokerInFlightDeployment state;
        try
        {
            inflight = ReadFile(paths.InFlightPath, BrokerInFlightDeploymentCodec.MaximumBytes, tail: false).Bytes;
            state = BrokerInFlightDeploymentCodec.ParseCanonical(inflight);
        }
        catch (FileNotFoundException) { return new(installEnabled, "None", null, []); }
        catch (DirectoryNotFoundException) { return new(installEnabled, "None", null, []); }
        catch (Exception exception) when (IsReadFailure(exception))
        { return new(installEnabled, "Unavailable", null, [new("inflight", "Unavailable", false, null)]); }

        string id = state.RequestId.ToString("N");
        List<DeploymentStatusFile> files = [Available("inflight", inflight, false)];
        files.Add(ReadRecord("report", Path.Combine(paths.ReportDirectory, id + ".json"), 16384,
            bytes => RequireRequest(bytes, "requestId", state.RequestId)));
        files.Add(ReadRecord("journal", Path.Combine(paths.JournalDirectory, id + ".jsonl"),
            BrokerDurableRecordStore.MaximumJournalBytes, bytes => ValidateJournal(bytes, state.RequestId)));
        files.Add(ReadRecord("cpuLaunch", Path.Combine(paths.RootDirectory, WindowsCpuVoltageDeployment.LaunchFileName),
            16384, bytes => RequireRequest(bytes, "RequestId", state.RequestId)));
        files.Add(ReadRecord("installerLog", Path.Combine(paths.StagingDirectory, id, "installer.log"),
            DeploymentStatusCodec.MaximumFileBytes, null, tail: true));
        files.AddRange(ReadSetupLogs(Path.Combine(paths.StagingDirectory, id)));
        return new(false, "Current", state.RequestId, files);
    }

    private IReadOnlyList<DeploymentStatusFile> ReadSetupLogs(string directory)
    {
        try
        {
            ProtectedBrokerStorage.RejectReparsePoint(paths.StagingDirectory);
            ProtectedBrokerStorage.RejectReparsePoint(directory);
            paths.VerifyDirectory(directory);
            List<string> matches = [];
            int entries = 0;
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > 32) { throw new InvalidDataException("Installer log inventory exceeds its bound."); }
                string name = Path.GetFileName(entry);
                if (!IsSetupLogName(name)) { continue; }
                if (matches.Count == 2) { throw new InvalidDataException("Ambiguous installer package logs."); }
                matches.Add(entry);
            }
            if (matches.Count == 0) { return [new("setupLog1", "Missing", false, null)]; }
            matches.Sort(StringComparer.Ordinal);
            return matches.Select((path, index) => ReadRecord("setupLog" + (index + 1), path,
                DeploymentStatusCodec.MaximumFileBytes, null, tail: true)).ToArray();
        }
        catch (DirectoryNotFoundException) { return [new("setupLog1", "Missing", false, null)]; }
        catch (Exception exception) when (IsReadFailure(exception))
        { return [new("setupLog1", "Unavailable", false, null)]; }
    }

    internal static bool IsSetupLogName(string name) => name.Length == "installer_000_OpenBladeSetup.log".Length
        && name.StartsWith("installer_", StringComparison.Ordinal)
        && name.EndsWith("_OpenBladeSetup.log", StringComparison.Ordinal)
        && name.AsSpan(10, 3).IndexOfAnyExceptInRange('0', '9') < 0;

    private DeploymentStatusFile ReadRecord(string name, string path, int maximum,
        Action<byte[]>? validate, bool tail = false)
    {
        try
        {
            var value = ReadFile(path, maximum, tail);
            validate?.Invoke(value.Bytes);
            bool truncated = value.Truncated || value.Bytes.Length > DeploymentStatusCodec.MaximumFileBytes;
            byte[] bytes = value.Bytes.Length > DeploymentStatusCodec.MaximumFileBytes
                ? value.Bytes[^DeploymentStatusCodec.MaximumFileBytes..] : value.Bytes;
            return Available(name, bytes, truncated);
        }
        catch (FileNotFoundException) { return new(name, "Missing", false, null); }
        catch (DirectoryNotFoundException) { return new(name, "Missing", false, null); }
        catch (Exception exception) when (IsReadFailure(exception)) { return new(name, "Unavailable", false, null); }
    }

    private (byte[] Bytes, bool Truncated) ReadFile(string path, int maximum, bool tail)
    {
        for (string? current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current))
        {
            ProtectedBrokerStorage.RejectReparsePoint(current);
            if (string.Equals(current, paths.RootDirectory, StringComparison.OrdinalIgnoreCase)) { break; }
        }
        paths.VerifyDirectory(Path.GetDirectoryName(path)!);
        ProtectedBrokerStorage.RejectReparsePoint(path);
        paths.VerifyFile(path);
        // Read-only sharing allows the owned installer to append a live log, never truncate this handle's snapshot.
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long length = stream.Length;
        if (!tail && length > maximum) { throw new InvalidDataException("Deployment record exceeds its bound."); }
        int count = checked((int)Math.Min(length, maximum));
        if (tail) { stream.Position = length - count; }
        byte[] bytes = new byte[count];
        stream.ReadExactly(bytes);
        if (stream.Length < length) { throw new IOException("Deployment record changed while reading."); }
        return (bytes, length > maximum);
    }

    private static void RequireRequest(byte[] bytes, string property, Guid expected)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.GetProperty(property).GetGuid() != expected)
        { throw new InvalidDataException("Diagnostic record belongs to another request."); }
    }

    private static void ValidateJournal(byte[] bytes, Guid expected)
    {
        if (bytes.Length == 0 || bytes[^1] != '\n') { throw new InvalidDataException("Incomplete deployment journal."); }
        int start = 0;
        for (int index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != '\n') { continue; }
            if (BrokerTransitionCodec.ParseCanonical(bytes.AsSpan(start, index - start)).RequestId != expected)
            { throw new InvalidDataException("Deployment journal request differs."); }
            start = index + 1;
        }
    }

    private static DeploymentStatusFile Available(string name, byte[] bytes, bool truncated) =>
        new(name, "Available", truncated, Convert.ToBase64String(bytes));
    private static bool IsReadFailure(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or InvalidOperationException or FormatException or KeyNotFoundException;
}
