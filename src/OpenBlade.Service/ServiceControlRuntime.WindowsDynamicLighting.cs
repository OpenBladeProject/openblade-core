using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed partial class ServiceControlRuntime
{
    // The Session Agent reads the current user's Windows Dynamic Lighting switch
    // and WinRT LampArray presence; the service cannot see either as
    // LocalSystem. The observation is advisory only: no lighting gate,
    // renderer, restore or suppression reads it. It is bound to the reporting
    // user and session, and to the exact model that was verified when it was
    // accepted, so it never carries over to another user, session or laptop.
    private sealed record WindowsDynamicLightingObservation(
        string UserSid,
        Func<bool> StillControlsSession,
        string ModelNumber,
        WindowsDynamicLightingAdvisory Advisory);

    private WindowsDynamicLightingObservation? windowsDynamicLightingObservation;

    private WindowsDynamicLightingAdvisory PublishedWindowsDynamicLightingAdvisory(
        ServiceStatusSnapshot snapshot) =>
        Volatile.Read(ref windowsDynamicLightingObservation) is { } observation
            && snapshot.DeviceVerified
            && WindowsDynamicLightingLampArrays.Find(snapshot.Firmware?.ModelNumber) is { } profile
            && string.Equals(profile.ModelNumber, observation.ModelNumber, StringComparison.Ordinal)
            && string.Equals(
                Volatile.Read(ref activeUserSid),
                observation.UserSid,
                StringComparison.OrdinalIgnoreCase)
            && StillControlsReportingSession(observation)
            ? observation.Advisory
            : WindowsDynamicLightingAdvisory.Unknown;

    // A lost or unreadable session turns the advisory into Unknown; status
    // projection never fails because of it.
    private static bool StillControlsReportingSession(
        WindowsDynamicLightingObservation observation)
    {
        try
        {
            return observation.StillControlsSession();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    public async Task<ControlOperationResult> ReportWindowsDynamicLightingAdvisoryAsync(
        ReportWindowsDynamicLightingAdvisoryRequest request,
        string reportingUserSid,
        Func<bool> stillControlsSession,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportingUserSid);
        ArgumentNullException.ThrowIfNull(stillControlsSession);
        EnsureInitialized();
        if (request.Advisory is not (WindowsDynamicLightingAdvisory.Off
                or WindowsDynamicLightingAdvisory.MayControl)
            || WindowsDynamicLightingLampArrays.Find(request.ModelNumber) is not { } reported
            || !string.Equals(reported.ModelNumber, request.ModelNumber, StringComparison.Ordinal))
        {
            return RecordRejectedIntent(new ArgumentException(
                "The Windows Dynamic Lighting report does not name a reviewed LampArray keyboard."));
        }

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!stillControlsSession()
                || !string.Equals(
                    Volatile.Read(ref activeUserSid),
                    reportingUserSid,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Windows Dynamic Lighting reports require the active local Windows session.");
            }

            ServiceStatusSnapshot current = GetStatus();
            if (!current.DeviceVerified
                || WindowsDynamicLightingLampArrays.Find(current.Firmware?.ModelNumber) is not { } profile
                || !string.Equals(profile.ModelNumber, request.ModelNumber, StringComparison.Ordinal))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "The Windows Dynamic Lighting report does not match the verified laptop.");
            }

            Volatile.Write(
                ref windowsDynamicLightingObservation,
                new WindowsDynamicLightingObservation(
                    reportingUserSid,
                    stillControlsSession,
                    profile.ModelNumber,
                    request.Advisory));
            PublishIfChanged(GetStatus());
            return new ControlOperationResult(
                ControlOperationStatus.Stored,
                "The Windows Dynamic Lighting observation was recorded. No lighting change was applied.");
        }
        finally
        {
            operationGate.Release();
        }
    }

    // A different user never inherits the previous user's observation; that
    // user's own Session Agent reports again.
    private void ForgetWindowsDynamicLightingObservationForOtherUser(string userSid)
    {
        if (Volatile.Read(ref windowsDynamicLightingObservation) is { } observation
            && !string.Equals(observation.UserSid, userSid, StringComparison.OrdinalIgnoreCase))
        {
            Volatile.Write(ref windowsDynamicLightingObservation, null);
        }
    }
}
