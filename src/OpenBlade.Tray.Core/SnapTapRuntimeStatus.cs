using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public enum SnapTapRuntimeState
{
    Off,
    Starting,
    WaitingForRelease,
    Active,
    Paused,
    Failed,
    Armed,
    Unavailable,
    RecoveryRequired,
    DriverMissing,
    UnsupportedModel,
    ManualDriverSetupRequired,
}

public sealed record SnapTapRuntimeStatus(
    SnapTapRuntimeState State,
    DateTimeOffset UpdatedAt,
    int ProcessId = 0,
    string? DriverModel = null,
    bool ExperimentalDriver = false,
    DiagnosticFailure? Failure = null);

public static class SnapTapStatusStore
{
    private const int MaximumFileSize = 8192;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string GetDefaultPath()
    {
        using Process process = Process.GetCurrentProcess();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenBlade",
            $"snap-tap-status-{process.SessionId}.json");
    }

    public static SnapTapRuntimeStatus? Read(string? path = null) =>
        ReadCore(path, out _);

    public static SnapTapDiagnosticReport CaptureDiagnostics(string? path = null)
    {
        SnapTapRuntimeStatus? status = ReadCore(path, out DiagnosticFailure? failure);
        if (status is not null)
        {
            // Export only the reviewed model number, never arbitrary text from a local file.
            status = status with
            {
                DriverModel = RazerBladeModelCatalog.All.FirstOrDefault(model =>
                    status.DriverModel?.EndsWith(" - " + model.ModelNumber, StringComparison.Ordinal) == true)
                    ?.ModelNumber,
            };
        }
        return new(DateTimeOffset.UtcNow, status, failure);
    }

    public static bool IsTerminalState(SnapTapRuntimeState state) => state is
        SnapTapRuntimeState.Failed or SnapTapRuntimeState.Unavailable
        or SnapTapRuntimeState.RecoveryRequired or SnapTapRuntimeState.DriverMissing
        or SnapTapRuntimeState.UnsupportedModel or SnapTapRuntimeState.ManualDriverSetupRequired;

    private static SnapTapRuntimeStatus? ReadCore(string? path, out DiagnosticFailure? failure)
    {
        failure = null;
        try
        {
            using FileStream file = new(
                path ?? GetDefaultPath(), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > MaximumFileSize)
            {
                failure = InvalidStatus("StatusTooLarge");
                return null;
            }

            // Read one extra byte to detect growth without allocating from untrusted length.
            byte[] content = new byte[MaximumFileSize + 1];
            int length = 0;
            int count;
            while (length < content.Length
                && (count = file.Read(content, length, content.Length - length)) != 0)
            {
                length += count;
            }
            if (length > MaximumFileSize)
            {
                failure = InvalidStatus("StatusTooLarge");
                return null;
            }

            StatusEnvelope? envelope = JsonSerializer.Deserialize<StatusEnvelope>(
                content.AsSpan(0, length), Options);
            if (envelope is not { Version: 1, Status: { } status }
                || !IsValid(status))
            {
                failure = InvalidStatus("InvalidStatusEnvelope");
                return null;
            }

            // A surviving file is not proof that its helper still owns the input hook.
            // The process start time also rejects a PID reused since the last update.
            return status.State == SnapTapRuntimeState.Off || IsTerminalState(status.State)
                || IsProcessCurrent(status)
                    ? status
                    : status with { State = SnapTapRuntimeState.Paused };
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or SecurityException or JsonException
            or ArgumentException or NotSupportedException)
        {
            failure = DiagnosticFailure.FromException("SnapTap", "Status", "Read",
                exception is FileNotFoundException or DirectoryNotFoundException
                    ? "StatusNotObserved" : "StatusReadFailed", exception);
            return null;
        }
    }

    private static DiagnosticFailure InvalidStatus(string reason) =>
        new("SnapTap", "Status", "Read", reason);

    public static void Write(SnapTapRuntimeStatus status, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!IsValid(status))
        {
            throw new ArgumentException("Invalid SnapTap runtime status.", nameof(status));
        }

        string destination = Path.GetFullPath(path ?? GetDefaultPath());
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".snap-tap-{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new StatusEnvelope(1, status), Options);
                stream.Flush(flushToDisk: true);
            }
            // Windows readers keep the old file open with delete sharing. Replace
            // preserves that snapshot; Move(overwrite: true) can reject the open target.
            if (File.Exists(destination))
            {
                File.Replace(temporary, destination, destinationBackupFileName: null);
            }
            else
            {
                try
                {
                    File.Move(temporary, destination);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    // The parent and helper can race to create the first status file.
                    File.Replace(temporary, destination, destinationBackupFileName: null);
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException or SecurityException)
            {
            }
        }
    }

    private static bool IsValid(SnapTapRuntimeStatus status) =>
        Enum.IsDefined(status.State)
        && status.ProcessId >= 0
        && status.UpdatedAt >= DateTimeOffset.UnixEpoch
        && status.UpdatedAt <= DateTimeOffset.UtcNow.AddMinutes(1)
        && (status.Failure is null || IsTerminalState(status.State) && status.Failure.IsValid())
        && (status.DriverModel is null || status.DriverModel.Length <= 96);

    private static bool IsProcessCurrent(SnapTapRuntimeStatus status)
    {
        if (status.ProcessId <= 0)
        {
            return false;
        }
        try
        {
            using Process process = Process.GetProcessById(status.ProcessId);
            using Process current = Process.GetCurrentProcess();
            return !process.HasExited
                && process.SessionId == current.SessionId
                && process.StartTime.ToUniversalTime() <= status.UpdatedAt.UtcDateTime;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or Win32Exception or NotSupportedException
            or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private sealed record StatusEnvelope(int Version, SnapTapRuntimeStatus? Status);
}

public sealed record SnapTapDiagnosticReport(
    DateTimeOffset CapturedAt,
    SnapTapRuntimeStatus? Status,
    DiagnosticFailure? CaptureFailure);
