using System.Globalization;
using System.Text;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

// A content rejection is a fully correlated reply whose closed decoder rejects
// its payload. Other exchange failures use separate structured diagnostics;
// none may turn failed reply bytes into confirmed device state.
internal static class DeviceStateContentRejection
{
    internal const int MaximumRejectedPayloadBytes = 16;

    internal static bool IsContentRejection(HidProtocolValidationFailure failure) =>
        failure == HidProtocolValidationFailure.PayloadValidator;

    // The retained payload is the positive evidence that a correlated reply
    // arrived. A defaulted category without it (for example a rejected
    // transaction assignment before any exchange) is never isolated.
    internal static bool IsContentRejection(RazerProtocolException exception) =>
        exception.StateQuery is not null
        && exception.RejectedPayloadHex is not null
        && IsContentRejection(exception.ValidationFailure);

    internal static string FormatPayload(ReadOnlySpan<byte> payload)
    {
        int length = Math.Min(payload.Length, MaximumRejectedPayloadBytes);
        if (length == 0)
        {
            return "(empty)";
        }

        var builder = new StringBuilder(length * 3 + 4);
        for (int index = 0; index < length; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(payload[index].ToString("X2", CultureInfo.InvariantCulture));
        }

        if (payload.Length > length)
        {
            builder.Append(" ...");
        }

        return builder.ToString();
    }

    internal static string FormatPayloads(
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second) =>
        $"{FormatPayload(first)} / {FormatPayload(second)}";

    internal static string QueryName(DeviceStateQuery? query, string fallback) => query switch
    {
        DeviceStateQuery.BatteryProtection => "Battery protection query (07/92)",
        DeviceStateQuery.TemporaryFullCharge => "Temporary full charge query (07/8F)",
        DeviceStateQuery.PerformanceAndFans => "Performance and fan state query",
        DeviceStateQuery.ThermalChannelOne => "Thermal channel 1 query (0D/82)",
        DeviceStateQuery.ThermalChannelTwo => "Thermal channel 2 query (0D/82)",
        DeviceStateQuery.ThermalChannelAgreement => "Thermal channel agreement (0D/82)",
        DeviceStateQuery.CustomCpuPerformance => "Custom CPU performance query (0D/87)",
        DeviceStateQuery.CustomGpuPerformance => "Custom GPU performance query (0D/87)",
        DeviceStateQuery.CustomPerformanceCombination => "Custom performance combination (0D/87)",
        DeviceStateQuery.KeyboardBrightness => "Keyboard brightness query (03/83)",
        DeviceStateQuery.KeyboardLightingEffect => "Keyboard effect query (0F/82)",
        DeviceStateQuery.KeyboardLightingCombination => "Keyboard brightness and effect (03/83, 0F/82)",
        DeviceStateQuery.LogoLightingState => "Logo lighting state query (03/80)",
        DeviceStateQuery.LogoLightingEffect => "Logo lighting effect query (03/82)",
        DeviceStateQuery.LogoLightingCombination => "Logo lighting state and effect (03/80, 03/82)",
        _ => fallback,
    };

    // Lighting getters report a closed read identity only for the admitted
    // keyboard brightness and logo queries; any other read returns null.
    internal static DeviceStateQuery? LightingQueryFor(RazerCommandSpec command) =>
        (command.ClassId, command.CommandId) switch
        {
            (LightingBrightnessProtocol.OperationClass, LightingBrightnessProtocol.QueryOperationId) =>
                DeviceStateQuery.KeyboardBrightness,
            (LogoLightingProtocol.CommandClass, LogoLightingProtocol.QueryStateCommandId) =>
                DeviceStateQuery.LogoLightingState,
            (LogoLightingProtocol.CommandClass, LogoLightingProtocol.QueryEffectCommandId) =>
                DeviceStateQuery.LogoLightingEffect,
            _ => null,
        };

    // Returns the lighting read failure to throw: a content rejection keeps the
    // existing I/O failure type so every current handler still applies, and
    // additionally carries the closed identity and bounded payload.
    internal static IOException LightingReadFailure(
        string message,
        RazerCommandSpec command,
        HidSessionFailureDiagnostic? diagnostic) =>
        LightingQueryFor(command) is { } query
        && diagnostic is
        {
            ProtocolValidationFailure: HidProtocolValidationFailure.PayloadValidator,
            RejectedPayloadHex: { } payload,
        }
            ? new DeviceStateContentRejectedException(message, query, payload)
            : new IOException(message);

    internal static string Describe(RazerProtocolException exception, string fallback)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string query = QueryName(exception.StateQuery, fallback);
        return IsContentRejection(exception.ValidationFailure)
            && exception.RejectedPayloadHex is { Length: > 0 } payload
                ? $"{query} returned unrecognized payload {payload} ({exception.ValidationFailure})."
                : $"{query} failed protocol validation ({exception.ValidationFailure}).";
    }
}

internal sealed record DeviceStateReadFailure(
    DeviceStateQuery Query,
    IReadOnlySet<DeviceSubsystem> Subsystems,
    string Message)
{
    internal HidProtocolValidationFailure? ValidationFailure { get; init; }

    internal HidSessionFailureDiagnostic? Diagnostic { get; init; }

    internal string? RejectedPayloadHex { get; init; }
}

// A combined read with isolated getter failures. Remaining getters complete
// only after safe native retirement and fresh admission. Dependent fields stay
// null and must be treated as unknown.
internal sealed record PartialDeviceStateRead
{
    internal PartialDeviceStateRead(
        ConfirmedDeviceState state,
        bool lightingOffConfirmationUnavailable,
        IReadOnlyList<DeviceStateReadFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            throw new ArgumentException(
                "A partial device-state read requires at least one failure.",
                nameof(failures));
        }

        State = state;
        LightingOffConfirmationUnavailable = lightingOffConfirmationUnavailable;
        Failures = failures;
        UnavailableSubsystems = failures
            .SelectMany(failure => failure.Subsystems)
            .ToHashSet();
    }

    internal ConfirmedDeviceState State { get; }

    internal bool LightingOffConfirmationUnavailable { get; }

    internal IReadOnlyList<DeviceStateReadFailure> Failures { get; }

    internal IReadOnlySet<DeviceSubsystem> UnavailableSubsystems { get; }

    internal string Message => string.Join(
        " ",
        Failures.Select(failure => failure.Message));

    internal string? MessageFor(DeviceSubsystem subsystem)
    {
        string[] messages = Failures
            .Where(failure => failure.Subsystems.Contains(subsystem))
            .Select(failure => failure.Message)
            .ToArray();
        return messages.Length == 0 ? null : string.Join(" ", messages);
    }

    internal Exception CreateException(IReadOnlySet<DeviceSubsystem>? dependencies = null)
    {
        DeviceStateReadFailure[] failures = dependencies is null
            ? Failures.ToArray()
            : Failures.Where(failure => failure.Subsystems.Any(dependencies.Contains)).ToArray();
        DeviceStateReadFailure first = failures[0];
        string message = string.Join(" ", failures.Select(failure => failure.Message));
        return first.ValidationFailure is { } validation
            ? new RazerProtocolException(message, validation)
            {
                StateQuery = first.Query,
                Diagnostic = first.Diagnostic,
                RejectedPayloadHex = first.RejectedPayloadHex,
                PartialState = this,
            }
            : new PartialDeviceStateReadException(this, message);
    }

    internal bool AffectsAny(IEnumerable<DeviceSubsystem> subsystems) =>
        subsystems.Any(UnavailableSubsystems.Contains);
}

// A lighting getter's content rejection. It remains an IOException so the
// lighting session's existing scoped handling is unchanged; it only adds the
// sanitized evidence used by status and diagnostic text.
internal sealed class DeviceStateContentRejectedException : IOException
{
    internal DeviceStateContentRejectedException(
        string message,
        DeviceStateQuery query,
        string rejectedPayloadHex)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(rejectedPayloadHex);
        Query = query;
        RejectedPayloadHex = rejectedPayloadHex;
    }

    internal DeviceStateQuery Query { get; }

    internal string RejectedPayloadHex { get; }

    internal string Description =>
        $"{DeviceStateContentRejection.QueryName(Query, "Device state query")} returned unrecognized payload {RejectedPayloadHex} ({HidProtocolValidationFailure.PayloadValidator}).";
}

// Consumers must explicitly opt into partial reads and name the state on which
// their decision depends. Ordinary callers continue to fail closed.
internal sealed class PartialDeviceStateReadException : IOException
{
    internal PartialDeviceStateReadException(PartialDeviceStateRead partialState, string? message = null)
        : base(message ?? partialState.Message) => PartialState = partialState;

    internal PartialDeviceStateRead PartialState { get; }
}
