using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// A caller may consume a partial read only when every state dependency of its
// decision was confirmed. Native and protocol failures retain their categories.
internal static class DeviceStateReadIsolation
{
    private static readonly IReadOnlySet<DeviceSubsystem> ThermalDependencies =
        new HashSet<DeviceSubsystem>
        {
            DeviceSubsystem.Performance,
            DeviceSubsystem.Fans,
        };

    private static readonly IReadOnlySet<DeviceSubsystem> BatteryDependencies =
        new HashSet<DeviceSubsystem> { DeviceSubsystem.Battery };

    private static readonly IReadOnlySet<DeviceSubsystem> LightingDependencies =
        new HashSet<DeviceSubsystem> { DeviceSubsystem.Lighting };

    internal static IReadOnlySet<DeviceSubsystem> Battery => BatteryDependencies;

    // Performance and fan decisions read each other's confirmed state
    // (manual-fan blocks, HyperBoost and the balanced manual-entry baseline).
    internal static IReadOnlySet<DeviceSubsystem> DependenciesOf(
        DeviceSubsystem subsystem) => subsystem switch
        {
            DeviceSubsystem.Performance or DeviceSubsystem.Fans => ThermalDependencies,
            DeviceSubsystem.Battery => BatteryDependencies,
            DeviceSubsystem.Lighting => LightingDependencies,
            _ => new HashSet<DeviceSubsystem> { subsystem },
        };

    internal static PartialDeviceStateRead? PartialRead(Exception exception) => exception switch
    {
        RazerProtocolException protocol => protocol.PartialState,
        PartialDeviceStateReadException read => read.PartialState,
        _ => null,
    };

    // This runs only at a closed, typed GET boundary. Admission failures,
    // cancellation, exhausted/poisoned sessions and unclassified exceptions
    // never qualify. A failed reply is diagnostic evidence, never readback.
    internal static bool CanIsolate(Exception exception) => exception switch
    {
        RazerProtocolException protocol when DeviceStateContentRejection.IsContentRejection(protocol) => true,
        RazerProtocolException { StateQuery: not null, Diagnostic: { } diagnostic } protocol =>
            IsExchangeFailure(diagnostic)
            && diagnostic.ProtocolValidationFailure == protocol.ValidationFailure,
        NativeDeviceReadException { StateQuery: not null, Diagnostic: { } diagnostic } native =>
            diagnostic.Failure == native.Failure && IsExchangeFailure(diagnostic),
        NativeDeviceReadTimeoutException { StateQuery: not null, Diagnostic: { } diagnostic } =>
            diagnostic.Failure == HidSessionFailureKind.Timeout && IsExchangeFailure(diagnostic),
        _ => false,
    };

    private static bool IsExchangeFailure(HidSessionFailureDiagnostic diagnostic) =>
        diagnostic.Failure switch
        {
            HidSessionFailureKind.ProtocolFailure =>
                diagnostic.Phase == HidSessionFailurePhase.ProtocolValidation
                && diagnostic.ProtocolValidationFailure is { } validation
                && (validation != HidProtocolValidationFailure.PayloadValidator
                    || diagnostic.RejectedPayloadHex is not null),
            HidSessionFailureKind.NativeIoFailure or HidSessionFailureKind.Timeout =>
                diagnostic.Phase is HidSessionFailurePhase.SetSubmit or HidSessionFailurePhase.SetCompletion
                    or HidSessionFailurePhase.GetSubmit or HidSessionFailurePhase.GetCompletion,
            _ => false,
        };

    internal static Task? NativeRetirement(Exception exception) => exception switch
    {
        RazerProtocolException protocol => protocol.NativeRetirement,
        NativeDeviceReadException native => native.NativeRetirement,
        NativeDeviceReadTimeoutException timeout => timeout.NativeRetirement,
        _ => null,
    };

    internal static DeviceStateReadFailure DescribeFailure(
        Exception exception,
        DeviceStateQuery query,
        IReadOnlySet<DeviceSubsystem> subsystems)
    {
        HidSessionFailureDiagnostic? diagnostic = exception switch
        {
            RazerProtocolException protocol => protocol.Diagnostic,
            NativeDeviceReadException native => native.Diagnostic,
            NativeDeviceReadTimeoutException timeout => timeout.Diagnostic,
            _ => null,
        };
        DeviceStateQuery actualQuery = exception switch
        {
            RazerProtocolException protocol => protocol.StateQuery ?? query,
            NativeDeviceReadException native => native.StateQuery ?? query,
            NativeDeviceReadTimeoutException timeout => timeout.StateQuery ?? query,
            _ => query,
        };
        HidProtocolValidationFailure? validation = exception is RazerProtocolException protocolFailure
            ? protocolFailure.ValidationFailure : diagnostic?.ProtocolValidationFailure;
        string? payload = exception is RazerProtocolException protocolPayload
            ? protocolPayload.RejectedPayloadHex : diagnostic?.RejectedPayloadHex;
        string message = validation is { } category
            ? DeviceStateContentRejection.Describe(new RazerProtocolException("Query failed.", category)
            {
                StateQuery = actualQuery,
                RejectedPayloadHex = payload,
            }, "Device state query")
            : $"{DeviceStateContentRejection.QueryName(actualQuery, "Device state query")} failed "
                + $"(failure={diagnostic?.Failure}, phase={diagnostic?.Phase}, "
                + $"nativeError={diagnostic?.NativeWin32Error?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}, "
                + $"bytes={diagnostic?.TransferredByteCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}).";
        return new(actualQuery, subsystems, message)
        {
            ValidationFailure = validation,
            Diagnostic = diagnostic,
            RejectedPayloadHex = payload,
        };
    }

    internal static async Task<ConfirmedDeviceState> ReadAsync(
        Func<CancellationToken, Task<ConfirmedDeviceState>> readAsync,
        IReadOnlySet<DeviceSubsystem> dependencies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readAsync);
        ArgumentNullException.ThrowIfNull(dependencies);
        try
        {
            return await readAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (PartialRead(exception) is { } partial)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!partial.AffectsAny(dependencies))
            {
                return partial.State;
            }

            throw partial.CreateException(dependencies);
        }
    }

    // Combines the blocking partial-read note with non-blocking lighting read
    // rejections into one sanitized status and diagnostic note.
    internal static string? DescribeReadNotes(
        PartialDeviceStateRead? partial,
        IReadOnlyList<string> lightingRejections)
    {
        ArgumentNullException.ThrowIfNull(lightingRejections);
        var notes = new List<string>(2);
        if (partial is not null)
        {
            notes.Add(DescribeUnavailable(partial));
        }

        if (lightingRejections.Count > 0)
        {
            notes.Add($"Lighting state could not be confirmed: {string.Join(" ", lightingRejections)}");
        }

        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    // Formats an observation note for the subsystems left unknown by a
    // partial read, in the stable subsystem order used by status messages.
    internal static string DescribeUnavailable(PartialDeviceStateRead partial)
    {
        ArgumentNullException.ThrowIfNull(partial);
        string subsystems = string.Join(
            ", ",
            DeviceSubsystemOrder.Default
                .Where(partial.UnavailableSubsystems.Contains)
                .Select(subsystem => subsystem.ToString()));
        return $"{subsystems} state is unavailable and writes to it remain blocked: {partial.Message}";
    }
}

// Exposes sanitized getter rejections that do not change any read outcome,
// such as a rejected keyboard brightness or logo reply.
internal interface IDeviceStateReadDiagnostics
{
    IReadOnlyList<string> StateReadRejections { get; }
}
