using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.SessionAgent;

/// <summary>One durable marker per Windows A/D helper lifetime, never per keystroke.</summary>
internal static class SnapTapWindowsOwnershipJournal
{
    private const int MaximumLength = 16384;
    internal sealed record Marker(int Version, string BootIdentity, int HelperProcessId,
        DateTimeOffset HelperStartedAt, string HelperVersion, string ExecutablePathHash);

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenBlade", "snaptap-windows-ownership.json");

    internal static bool ReconcilePrior(string path, Func<string> readBootIdentity,
        out DiagnosticFailure? failure)
    {
        failure = null;
        try
        {
            Marker? previous;
            try { previous = Read(path); }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            string currentBoot = readBootIdentity();
            if (!CanDiscard(previous, currentBoot))
            {
                failure = Failure("WindowsOwnershipRecoveryRequired");
                return false;
            }
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (IsJournalException(exception))
        {
            failure = Failure("WindowsOwnershipObservationFailed", exception);
            return false;
        }
    }

    internal static bool TryAcquire(string path, Func<string> readBootIdentity,
        out Lease? lease, out DiagnosticFailure? failure)
    {
        lease = null;
        string? cachedBoot = null;
        string ReadBoot() => cachedBoot ??= readBootIdentity();
        if (!ReconcilePrior(path, ReadBoot, out failure)) { return false; }
        try
        {
            string boot = ReadBoot();
            if (!TryBoot(boot, out _))
            {
                failure = Failure("WindowsBootIdentityUnavailable");
                return false;
            }
            using Process process = Process.GetCurrentProcess();
            var marker = new Marker(1, boot, Environment.ProcessId,
                process.StartTime.ToUniversalTime(),
                typeof(SnapTapWindowsOwnershipJournal).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Environment.ProcessPath!)))));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            // CreateNew must never replace unresolved evidence, including a marker
            // created concurrently after the prior-state observation.
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(file, marker);
                file.Flush(true);
            }
            lease = new Lease(path, marker);
            return true;
        }
        catch (Exception exception) when (IsJournalException(exception))
        {
            failure = Failure("WindowsOwnershipJournalCreateFailed", exception);
            return false;
        }
    }

    internal static bool CanDiscard(Marker? marker, string currentBoot) =>
        marker is { Version: 1, HelperProcessId: > 0 }
        && !string.IsNullOrWhiteSpace(marker.HelperVersion) && marker.HelperVersion.Length <= 128
        && marker.ExecutablePathHash is { Length: 64 }
        && marker.ExecutablePathHash.All(Uri.IsHexDigit)
        && TryBoot(marker.BootIdentity, out DateTime previous)
        && TryBoot(currentBoot, out DateTime current)
        && current > previous
        && current > marker.HelperStartedAt.UtcDateTime
        && marker.HelperStartedAt >= previous
        && marker.HelperStartedAt <= DateTimeOffset.UtcNow.AddMinutes(1);

    private static bool TryBoot(string? value, out DateTime boot) =>
        DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out boot) && boot.Kind == DateTimeKind.Utc
        && boot >= DateTime.UnixEpoch && boot <= DateTime.UtcNow.AddMinutes(1);

    private static Marker? Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumLength) { throw new InvalidDataException("Ownership marker exceeds its bound."); }
        // A fixed buffer bounds parsing even if an external writer grows the file.
        byte[] bytes = new byte[MaximumLength + 1];
        int count = 0;
        while (count < bytes.Length)
        {
            int read = file.Read(bytes, count, bytes.Length - count);
            if (read == 0) { break; }
            count += read;
        }
        if (count > MaximumLength) { throw new InvalidDataException("Ownership marker exceeds its bound."); }
        return JsonSerializer.Deserialize<Marker>(bytes.AsSpan(0, count));
    }

    private static bool IsJournalException(Exception exception) => exception is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or JsonException or InvalidOperationException
        or System.ComponentModel.Win32Exception or System.Management.ManagementException
        or System.Runtime.InteropServices.COMException or ArgumentException or NotSupportedException;

    private static DiagnosticFailure Failure(string reason, Exception? exception = null) => exception is null
        ? SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Worker,
            "WindowsOwnershipJournal", "OwnershipJournal", reason, true)
        : SnapTapDiagnosticFailure.FromException(SnapTapDiagnosticFailure.Worker,
            "WindowsOwnershipJournal", "OwnershipJournal", reason, exception, true);

    internal sealed class Lease(string path, Marker marker)
    {
        // This is deliberately explicit rather than IDisposable: a failure or
        // exception must retain the marker instead of implicitly releasing it.
        internal bool CompleteCleanly(out DiagnosticFailure? failure)
        {
            failure = null;
            try
            {
                if (Read(path) != marker)
                {
                    failure = Failure("WindowsOwnershipMarkerChanged");
                    return false;
                }
                File.Delete(path);
                return true;
            }
            catch (Exception exception) when (IsJournalException(exception))
            {
                failure = Failure("WindowsOwnershipJournalCleanupFailed", exception);
                return false;
            }
        }
    }
}
