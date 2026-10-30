using System.Globalization;
using System.Text.Json;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>Shared bounded observation of the driver journal; Windows callers never alter it.</summary>
internal static class DriverOwnershipJournalReader
{
    internal const int MaximumLength = 131072;

    internal static SnapTapDriverSupervisor.OwnershipJournal? Read(string path)
    {
        FileStream file;
        try { file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using (file)
        {
            if (file.Length > MaximumLength) { throw new InvalidDataException("Driver ownership journal is oversized."); }
            byte[] bytes = new byte[MaximumLength + 1];
            int count = 0;
            while (count < bytes.Length)
            {
                int read = file.Read(bytes, count, bytes.Length - count);
                if (read == 0) { break; }
                count += read;
            }
            if (count > MaximumLength) { throw new InvalidDataException("Driver ownership journal grew beyond its bound."); }
            var marker = JsonSerializer.Deserialize<SnapTapDriverSupervisor.OwnershipJournal>(bytes.AsSpan(0, count));
            if (marker is null || !SnapTapDriverSupervisor.IsValidOwnershipJournalScope(Path.GetFileName(path), marker.Features)
                || !TryBoot(marker.BootIdentity, out _)
                || marker.BaselineHooks is null || marker.BaselineHooks.Length > RazerSnapTapDriverProtocol.MaximumHookEnumerationCount
                || marker.BaselineHooks.Any(record => record is null || record.Length != RazerSnapTapDriverProtocol.HookRecordLength))
            {
                throw new InvalidDataException("Driver ownership journal is invalid.");
            }
            return marker;
        }
    }

    internal static bool AllowsWindowsBackend(string directory, KeyboardDriverFeatures features, Func<string> readBootIdentity)
    {
        if (features is not (KeyboardDriverFeatures.SnapTap or KeyboardDriverFeatures.FunctionRow))
        {
            throw new ArgumentOutOfRangeException(nameof(features));
        }
        // A Fn-only journal cannot own A/D. Fn must also inspect the SnapTap
        // filename because it can hold a combined session or a legacy A/D session.
        string[] names = features == KeyboardDriverFeatures.SnapTap
            ? ["snaptap-driver-ownership.json"]
            : ["function-row-driver-ownership.json", "snaptap-driver-ownership.json"];
        List<string> previousBoots = [];
        foreach (string name in names)
        {
            var marker = Read(Path.Combine(directory, name));
            if (marker is not null && (marker.Features & features) != 0)
            {
                previousBoots.Add(marker.BootIdentity);
            }
        }
        if (previousBoots.Count == 0) { return true; }
        string currentBoot = readBootIdentity();
        // A strictly later boot ends the old file subscriptions and their output
        // lifetime. This read-only decision never clears journals or driver hooks.
        return previousBoots.All(previous => IsLaterBoot(previous, currentBoot));
    }

    internal static bool IsLaterBoot(string? previousBoot, string currentBoot) =>
        TryBoot(previousBoot, out DateTime previous) && TryBoot(currentBoot, out DateTime current)
        && current > previous;

    private static bool TryBoot(string? value, out DateTime boot) =>
        DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out boot)
        && boot.Kind == DateTimeKind.Utc && boot >= DateTime.UnixEpoch && boot <= DateTime.UtcNow.AddMinutes(1);

    internal static bool IsObservationException(Exception exception) => exception is
        IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException
        or System.Security.SecurityException or System.Management.ManagementException
        or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or ArgumentException;
}
