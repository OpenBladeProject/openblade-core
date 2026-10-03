using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Capture;

internal enum CpuVoltageSessionAction { ObserveOnly, VoltageCycle, Status, Close }
internal sealed record CpuVoltageSessionRequest(int Version, Guid RequestId, CpuVoltageSessionAction Action);
internal sealed record CpuVoltageSessionReply(Guid Session, string State, Guid? Job, bool ObservationAdmitted,
    string Message, IReadOnlyDictionary<string, JsonElement>? Evidence = null)
{
    public bool RequestAccepted { get; init; }
}

// Fixed local messages. Paths and command arguments exist only in attended startup enrollment.
internal static class CpuVoltageSessionProtocol
{
    internal const int MaximumRequest = 512, MaximumReply = 512 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };
    internal static CpuVoltageSessionRequest Parse(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumRequest) { throw new InvalidDataException("Invalid session request size."); }
        using var document = JsonDocument.Parse(bytes);
        var properties = document.RootElement.EnumerateObject().ToArray();
        if (properties.Length != 3 || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 3
            || properties.Any(p => p.Name is not ("version" or "requestId" or "action")))
        { throw new InvalidDataException("Only version, requestId and action are accepted."); }
        var request = JsonSerializer.Deserialize<CpuVoltageSessionRequest>(bytes, Json);
        if (request is null || request.Version != 1 || request.RequestId == Guid.Empty || !Enum.IsDefined(request.Action)
            || document.RootElement.GetProperty("action").ValueKind != JsonValueKind.String
            || document.RootElement.GetProperty("action").GetString() != request.Action.ToString())
        { throw new InvalidDataException("Invalid session request."); }
        return request;
    }
    internal static async Task<byte[]> ReadAsync(Stream stream, int limit, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 || length > limit) { throw new InvalidDataException("Session frame exceeds its bound."); }
        byte[] payload = new byte[length]; await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false); return payload;
    }
    internal static async Task WriteAsync(Stream stream, object value, int limit, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Json);
        if (bytes.Length > limit) { throw new InvalidDataException("Session response exceeds its bound."); }
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}

// In-memory reducer is backed by a CreateNew durable acceptance record before launch.
// One observation and one cycle are the entire authority of an enrollment.
internal sealed class CpuVoltageSessionState(Guid session)
{
    private readonly HashSet<Guid> accepted = [];
    private bool observed, cycled, blocked, closed;
    private Guid? active;
    private bool observationAdmitted;
    private string message = "Ready for one observation.";
    private IReadOnlyDictionary<string, JsonElement>? evidence;
    internal CpuVoltageSessionReply Snapshot => new(session, closed ? "Closed" : blocked ? "Blocked"
        : active is not null ? "Running" : "Ready", active, observationAdmitted, message, evidence);
    internal bool MustRetainHost => active is not null;
    internal bool Accept(CpuVoltageSessionRequest request, Action<CpuVoltageSessionRequest> persist)
    {
        if (request.Action is CpuVoltageSessionAction.Status) { return false; }
        if (request.Action is CpuVoltageSessionAction.Close)
        {
            if (active is null) { closed = true; message = "Session closed."; }
            return false;
        }
        if (blocked || closed || active is not null || accepted.Contains(request.RequestId)
            || request.Action == CpuVoltageSessionAction.ObserveOnly && observed
            || request.Action == CpuVoltageSessionAction.VoltageCycle && (cycled || !observationAdmitted))
        { return false; }
        // A failed durable append closes admission; it never leaves a retryable launch.
        accepted.Add(request.RequestId);
        try { persist(request); }
        catch { Block("Acceptance could not be durably recorded. No job launched."); throw; }
        if (request.Action == CpuVoltageSessionAction.ObserveOnly) { observed = true; } else { cycled = true; }
        active = request.RequestId; message = "Accepted; do not resubmit this job."; return true;
    }
    internal void Complete(bool success, bool admitCycle, IReadOnlyDictionary<string, JsonElement>? records, string detail)
    {
        evidence = records; message = detail;
        if (!success) { blocked = true; return; }
        active = null;
        observationAdmitted = admitCycle;
        if (!admitCycle) { blocked = true; }
    }
    internal void Block(string detail) { blocked = true; message = detail; }
}
