namespace OpenBlade.Protocol;

internal enum FirmwareQueryOutcome
{
    Succeeded,
    ExchangeFailed,
    UnrecognizedValue,
    ModelAdmissionRejected,
    ContentRejected,
}

internal sealed record FirmwareQueryObservation(
    long Id,
    string Command,
    string RequestPayloadHex,
    DateTimeOffset Timestamp,
    FirmwareQueryOutcome Outcome,
    HidSessionFailureKind? Failure,
    HidSessionFailurePhase? Phase,
    HidProtocolValidationFailure? ProtocolValidationFailure,
    int? NativeWin32Error,
    int? TransferredByteCount,
    string? ResponsePayloadHex)
{
    // A caller may report a semantic rejection after another query completes.
    // Registration belongs to that observation and is never exported.
    internal FirmwareQueryRegistration? Registration { get; init; }
}

internal sealed class FirmwareQueryRegistration(FirmwareQueryDiagnostics owner)
{
    internal FirmwareQueryDiagnostics Owner { get; } = owner;
    internal bool RejectionRecorded { get; set; }
}

internal sealed record FirmwareQueryDiagnostic(
    string Command,
    string RequestPayloadHex,
    long ObservationCount,
    long FailureCount,
    FirmwareQueryObservation Latest,
    FirmwareQueryObservation? FirstFailure,
    FirmwareQueryObservation? LastFailure,
    FirmwareQueryObservation? LastResponseFailure);

/// <summary>
/// Bounded, process-lifetime evidence from existing typed firmware queries.
/// Reading a snapshot never queries hardware. Recovery and port disposal do
/// not clear failures; a service restart begins a new collection.
/// </summary>
internal sealed class FirmwareQueryDiagnostics
{
    private readonly object sync = new();
    private readonly Dictionary<(string Command, string Request), FirmwareQueryDiagnostic> entries = new();
    private long nextId;

    public static FirmwareQueryDiagnostics Process { get; } = new();

    public IReadOnlyList<FirmwareQueryDiagnostic> Snapshot()
    {
        lock (sync)
        {
            return Array.AsReadOnly(entries.Values
                .OrderBy(entry => entry.Command, StringComparer.Ordinal)
                .ThenBy(entry => entry.RequestPayloadHex, StringComparer.Ordinal)
                .ToArray());
        }
    }

    internal FirmwareQueryObservation? Record(
        RazerCommandSpec command,
        HidSessionResult<ReadOnlyMemory<byte>> result,
        DateTimeOffset timestamp)
    {
        if (!CanRecord(command))
        {
            return null;
        }

        string name = $"{command.ClassId:X2}/{command.CommandId:X2}";
        string request = Convert.ToHexString(command.Arguments.Span);
        lock (sync)
        {
            var observation = new FirmwareQueryObservation(
                ++nextId, name, request, timestamp,
                result.Failure is null ? FirmwareQueryOutcome.Succeeded : FirmwareQueryOutcome.ExchangeFailed,
                result.Failure, result.Diagnostic?.Phase,
                result.Diagnostic?.ProtocolValidationFailure,
                result.Diagnostic?.NativeWin32Error,
                result.Diagnostic?.TransferredByteCount,
                SafePayload(result.QueryResponsePayloadHex))
            {
                Registration = new(this),
            };
            var key = (name, request);
            FirmwareQueryDiagnostic entry = entries.TryGetValue(key, out var previous)
                ? previous with { ObservationCount = previous.ObservationCount + 1, Latest = observation }
                : new(name, request, 1, 0, observation, null, null, null);
            entries[key] = observation.Outcome == FirmwareQueryOutcome.Succeeded
                ? entry : AddFailure(entry, observation);
            return observation;
        }
    }

    internal void RecordRejection(FirmwareQueryObservation observation, FirmwareQueryOutcome outcome)
    {
        if (outcome is not (FirmwareQueryOutcome.UnrecognizedValue
            or FirmwareQueryOutcome.ModelAdmissionRejected or FirmwareQueryOutcome.ContentRejected)
            || observation.Outcome != FirmwareQueryOutcome.Succeeded
            || observation.Registration is not { } registration
            || !ReferenceEquals(registration.Owner, this))
        {
            return;
        }

        lock (sync)
        {
            if (registration.RejectionRecorded)
            {
                return;
            }

            var key = (observation.Command, observation.RequestPayloadHex);
            if (!entries.TryGetValue(key, out var entry))
            {
                return;
            }

            registration.RejectionRecorded = true;
            var rejected = observation with { Outcome = outcome };
            if (entry.Latest.Id == observation.Id)
            {
                entry = entry with { Latest = rejected };
            }

            entries[key] = AddFailure(entry, rejected);
        }
    }

    private static FirmwareQueryDiagnostic AddFailure(
        FirmwareQueryDiagnostic entry, FirmwareQueryObservation observation)
    {
        return entry with
        {
            FailureCount = entry.FailureCount + 1,
            FirstFailure = entry.FirstFailure is null || observation.Id < entry.FirstFailure.Id
                ? observation : entry.FirstFailure,
            LastFailure = entry.LastFailure is null || observation.Id > entry.LastFailure.Id
                ? observation : entry.LastFailure,
            LastResponseFailure = observation.ResponsePayloadHex is not null
                && (entry.LastResponseFailure is null || observation.Id > entry.LastResponseFailure.Id)
                    ? observation : entry.LastResponseFailure,
        };
    }

    private static string? SafePayload(string? value) => value is not null
        && value.Length <= RazerReport.MaximumPayloadLength * 2 && value.Length % 2 == 0
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F')
            ? value : null;

    // Closed request shapes bound both the number of entries and their data.
    // Do not infer queries from the high command bit: 00/82 reads a serial,
    // and 00/86 remains excluded from this shared payload catalog. The exact-02A0
    // edition getter records only decoded regional layout through its own policy.
    internal static bool CanRecord(RazerCommandSpec command)
    {
        ReadOnlySpan<byte> arguments = command.Arguments.Span;
        if (command.ExpectedPayloadLength != arguments.Length)
        {
            return false;
        }

        if (command.AllowVariableResponseLength)
        {
            return command is { ClassId: 0x0F, CommandId: 0x82, PayloadValidator: not null }
                && arguments is [1, 5, 0];
        }

        return (command.ClassId, command.CommandId) switch
        {
            (0x00, 0x84) => arguments is [0, 0],
            (0x00, 0x87) or (0x00, 0x88) => arguments is [0, 0, 0, 0],
            (0x02, 0x86) => arguments is [1, 0],
            (0x03, 0x80) or (0x03, 0x82) => arguments is [0 or 1, 4, 0],
            (0x03, 0x83) => arguments is [1, 5, 0],
            (0x07, 0x8C) => arguments is [0, 0],
            (0x07, 0x8F) or (0x07, 0x92) or (0x0F, 0x98)
                or (0x0D, 0x8E) or (0x0D, 0x8F) => arguments is [0],
            (0x0D, 0x80) => arguments.Length == 80 && !arguments.ContainsAnyExcept((byte)0),
            // Validated models use class 1; the experimental definitions use 0.
            (0x0D, 0x82) => arguments is [0 or 1, 1 or 2, 0, 0],
            (0x0D, 0x87) => arguments is [0 or 1, 1 or 2, 0],
            (0x0D, 0x88) => arguments is [1, 1 or 2, 0],
            _ => false,
        };
    }
}
