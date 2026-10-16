using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text.Json;

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
}

public sealed record SnapTapRuntimeStatus(
    SnapTapRuntimeState State,
    DateTimeOffset UpdatedAt,
    int ProcessId = 0);

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

    public static SnapTapRuntimeStatus? Read(string? path = null)
    {
        try
        {
            using FileStream file = new(
                path ?? GetDefaultPath(), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > MaximumFileSize)
            {
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
                return null;
            }

            StatusEnvelope? envelope = JsonSerializer.Deserialize<StatusEnvelope>(
                content.AsSpan(0, length), Options);
            if (envelope is not { Version: 1, Status: { } status }
                || !IsValid(status))
            {
                return null;
            }

            // A surviving file is not proof that its helper still owns the input hook.
            // The process start time also rejects a PID reused since the last update.
            return status.State is SnapTapRuntimeState.Off or SnapTapRuntimeState.Failed
                || IsProcessCurrent(status)
                    ? status
                    : status with { State = SnapTapRuntimeState.Paused };
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or SecurityException or JsonException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

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
            File.Move(temporary, destination, overwrite: true);
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
        && status.UpdatedAt <= DateTimeOffset.UtcNow.AddMinutes(1);

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
