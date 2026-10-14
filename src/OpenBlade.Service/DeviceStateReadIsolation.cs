using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// Shared admission for partial device-state reads. A combined read reports a
// content-rejected getter by throwing a protocol exception that carries the
// partial state, so every caller that does not opt in keeps the fail-closed
// whole-read behavior. A caller may continue only when none of the subsystems
// its decision depends on was left unknown by a rejected getter.
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

    internal static PartialDeviceStateRead? PartialRead(Exception exception) =>
        exception is RazerProtocolException { PartialState: { } partial }
            ? partial
            : null;

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
        catch (RazerProtocolException exception) when (
            exception.PartialState is { } partial)
        {
            if (!partial.AffectsAny(dependencies))
            {
                return partial.State;
            }

            // Report only the rejected getters this decision depends on.
            throw new RazerProtocolException(
                string.Join(
                    " ",
                    partial.Failures
                        .Where(failure => failure.Subsystems.Any(dependencies.Contains))
                        .Select(failure => failure.Message)),
                exception.ValidationFailure)
            {
                StateQuery = exception.StateQuery,
                PartialState = partial,
            };
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
