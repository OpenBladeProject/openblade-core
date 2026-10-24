using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

// Legacy 2.6.5 controls only the service's current output. Reading and shutdown
// never select an output, listening mode, processing mode, or EQ configuration.
internal sealed class LegacyThxAudioCommand(
    Func<ILegacyThxAudioNativeAdapter> createNative,
    IThxAudioAdmissionSource admission,
    Func<ThxAudioCompatibilityProfile, ThxAudioEndpoint, string?> readDefaultOutput)
{
    private (string? ModelNumber, string DriverStackIdentity)? admittedIdentity;

    internal ThxAudioHelperResponse Execute(ThxAudioHelperRequest request,
        ThxAudioAdmission current, ThxAudioCompatibilityProfile profile)
    {
        bool mutation = request is not ThxHelperReadRequest;
        if (admittedIdentity is { } identity
            && identity != (current.ModelNumber, current.DriverStackIdentity))
        {
            return Result(ThxAudioOperationStatus.Unavailable,
                "The THX device or driver stack identity changed.");
        }
        if (mutation && request is not ThxHelperSetPropertyRequest { PropertyId: "spatial_enabled" })
        {
            return Result(ThxAudioOperationStatus.Failed,
                "Only spatial audio control is admitted for this THX version.");
        }
        if (mutation && !current.MutationAllowed)
        {
            return Result(ThxAudioOperationStatus.Unavailable, "This THX stack is read-only.");
        }
        bool mayHaveSubmitted = false;
        try
        {
            // COM initialization and release stay on this synchronous request's
            // thread. The pipe loop may resume on another thread after an await.
            using ILegacyThxAudioNativeAdapter native = createNative();
            admittedIdentity = (current.ModelNumber, current.DriverStackIdentity);
            LegacyThxAudioNativeState before = native.Read();
            VerifyOutput(before, profile, request.Endpoint);
            if (request is ThxHelperSetPropertyRequest spatial)
            {
                // A native connection/read is not write admission. Repeat exact
                // hardware, component and controller checks immediately before submission.
                ThxAudioAdmission fresh = admission.Check();
                if (!fresh.IsAvailable || !fresh.MutationAllowed
                    || fresh.ModelNumber != current.ModelNumber || fresh.ProductId != current.ProductId
                    || !string.Equals(fresh.DriverStackIdentity, current.DriverStackIdentity,
                        StringComparison.Ordinal))
                {
                    return new(ThxAudioHelperProtocol.Version, ThxAudioOperationStatus.Unavailable,
                        fresh.Availability, fresh.Error ?? "THX write admission changed.", null);
                }
                VerifyOutput(native.Read(), profile, request.Endpoint);
                mayHaveSubmitted = true;
                native.SetSpatial(spatial.Value);
            }
            LegacyThxAudioNativeState after = mutation ? native.Read() : before;
            VerifyOutput(after, profile, request.Endpoint);
            if (request is ThxHelperSetPropertyRequest requested && after.SpatialEnabled != requested.Value)
            {
                return Result(ThxAudioOperationStatus.Indeterminate,
                    "The THX mutation could not be confirmed by readback.");
            }
            return new(ThxAudioHelperProtocol.Version, ThxAudioOperationStatus.Succeeded,
                current.Availability, null,
                new ThxAudioState(request.Endpoint, after.SpatialEnabled, false, null, [])
                {
                    // The native EQ buffer and processing modes do not establish
                    // the V4 EQ toggle, frequency geometry or preset semantics.
                    Equalizer = ThxAudioFeatureAccess.ObservedOnly,
                    ApplicationProfiles = ThxAudioFeatureAccess.Unavailable,
                });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.LegacyThxAudioCommand",
                "Execute",
                exception);
            return Result(mayHaveSubmitted ? ThxAudioOperationStatus.Indeterminate : ThxAudioOperationStatus.Failed,
                exception is OutputMismatchException
                    ? "The selected THX output does not match the exact active Realtek output. Select the output currently used by Windows."
                    : $"Legacy THX settings could not be confirmed: {exception.GetType().Name}.");
        }
    }

    private void VerifyOutput(LegacyThxAudioNativeState state,
        ThxAudioCompatibilityProfile profile, ThxAudioEndpoint endpoint)
    {
        string? active = readDefaultOutput(profile, endpoint);
        if (active is null || !string.Equals(active, state.OutputDeviceId, StringComparison.OrdinalIgnoreCase)
            || state.ListeningModeIsHeadphones != (endpoint == ThxAudioEndpoint.Headphones))
        {
            throw new OutputMismatchException();
        }
    }

    private sealed class OutputMismatchException : Exception;

    private static ThxAudioHelperResponse Result(ThxAudioOperationStatus status, string error) =>
        new(ThxAudioHelperProtocol.Version, status, ThxAudioAvailability.Available, error, null);

}
