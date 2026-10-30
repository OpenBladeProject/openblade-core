using System.Globalization;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>One bounded startup observation; the hook reads only its cached result.</summary>
internal static class WindowsFunctionRowOwnershipGuard
{
    internal static KeyboardInputBackend SelectBackend(KeyboardInputBackend requested,
        bool driverCanTransfer, DiagnosticFailure? startupFailure) =>
        requested == KeyboardInputBackend.WindowsHook && driverCanTransfer && startupFailure is null
            ? KeyboardInputBackend.WindowsHook : KeyboardInputBackend.Unknown;

    internal static DiagnosticFailure? InspectStartup(string directory, Func<string> readBootIdentity)
    {
        try
        {
            List<DateTime> previousBoots = [];
            foreach (string name in new[] { "function-row-driver-ownership.json", "snaptap-driver-ownership.json" })
            {
                SnapTapDriverSupervisor.OwnershipJournal? marker;
                try
                {
                    using var file = new FileStream(Path.Combine(directory, name), FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (file.Length > 131072) { return Failure("DriverOwnershipMarkerInvalid"); }
                    byte[] bytes = new byte[131073];
                    int count = 0;
                    while (count < bytes.Length)
                    {
                        int read = file.Read(bytes, count, bytes.Length - count);
                        if (read == 0) { break; }
                        count += read;
                    }
                    if (count > 131072) { return Failure("DriverOwnershipMarkerInvalid"); }
                    marker = JsonSerializer.Deserialize<SnapTapDriverSupervisor.OwnershipJournal>(bytes.AsSpan(0, count));
                }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if (marker is null || !SnapTapDriverSupervisor.IsValidOwnershipJournalScope(name, marker.Features)
                    || !TryBoot(marker.BootIdentity, out DateTime boot)
                    || marker.BaselineHooks is null || marker.BaselineHooks.Length > RazerSnapTapDriverProtocol.MaximumHookEnumerationCount
                    || marker.BaselineHooks.Any(record => record is null || record.Length != RazerSnapTapDriverProtocol.HookRecordLength))
                {
                    return Failure("DriverOwnershipMarkerInvalid");
                }
                if ((marker.Features & KeyboardDriverFeatures.FunctionRow) != 0) { previousBoots.Add(boot); }
            }
            if (previousBoots.Count == 0) { return null; }
            if (!TryBoot(readBootIdentity(), out DateTime current)
                || previousBoots.Any(previous => current <= previous))
            {
                return Failure("DriverFunctionRowOwnershipRecoveryRequired");
            }
            // A later Windows boot proves old subscriptions cannot survive. Leave
            // driver journals untouched; only the driver supervisor reconciles them.
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or System.Security.SecurityException or System.Management.ManagementException
            or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return SnapTapDiagnosticFailure.FromException("FunctionRowWindowsOwnership", "InspectStartup",
                "OwnershipJournal", "DriverOwnershipObservationFailed", exception, true);
        }
    }

    private static bool TryBoot(string? value, out DateTime boot) =>
        DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out boot)
        && boot.Kind == DateTimeKind.Utc && boot >= DateTime.UnixEpoch && boot <= DateTime.UtcNow.AddMinutes(1);

    private static DiagnosticFailure Failure(string reason) => SnapTapDiagnosticFailure.Create(
        "FunctionRowWindowsOwnership", "InspectStartup", "OwnershipJournal", reason, true);
}
