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
            if (!DriverOwnershipJournalReader.AllowsWindowsBackend(directory, KeyboardDriverFeatures.FunctionRow, readBootIdentity))
            {
                return Failure("DriverFunctionRowOwnershipRecoveryRequired");
            }
            // A later Windows boot proves old subscriptions cannot survive. Leave
            // driver journals untouched; only the driver supervisor reconciles them.
            return null;
        }
        catch (Exception exception) when (DriverOwnershipJournalReader.IsObservationException(exception))
        {
            return SnapTapDiagnosticFailure.FromException("FunctionRowWindowsOwnership", "InspectStartup",
                "OwnershipJournal", "DriverOwnershipObservationFailed", exception, true);
        }
    }

    private static DiagnosticFailure Failure(string reason) => SnapTapDiagnosticFailure.Create(
        "FunctionRowWindowsOwnership", "InspectStartup", "OwnershipJournal", reason, true);
}
