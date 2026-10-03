using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Capture;

// Consumes immutable output from the retained, reviewed FirmwareSmoke child.
// This is completion evidence only; it never admits a native CPU command.
internal static class CpuVoltageOwnedSmokeEvidence
{
    internal const string BaselineFile = "cpu-voltage-optimizer-baseline.json";
    internal const string ResultFile = "cpu-voltage-optimizer.json";
    private static readonly int[] Cores = [0, 2, 4, 6, 8, 9, 10, 11, 12, 13, 14, 15];
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
    internal static void Verify(byte[] baselineBytes, byte[] resultBytes, string version, DateTimeOffset launched,
        DateTimeOffset completed)
    {
        using var baseline = Parse(baselineBytes); using var result = Parse(resultBytes);
        var b = baseline.RootElement; var r = result.RootElement;
        if (b.GetProperty("schemaVersion").GetInt32() != 1 || b.GetProperty("coreMask").GetInt32() != 0xFF55
            || b.GetProperty("expectedServiceVersion").GetString() != version
            || b.GetProperty("status").GetProperty("serviceVersion").GetString() != version
            || b.GetProperty("capturedAtUtc").GetDateTimeOffset() < launched
            || b.GetProperty("capturedAtUtc").GetDateTimeOffset() > completed
            || r.GetProperty("expectedServiceVersion").GetString() != version
            || r.TryGetProperty("mode", out var mode) && mode.GetString() != "Cycle"
            || r.GetProperty("outcome").GetString() != "Passed" || r.GetProperty("phase").GetString() != "OriginalReadback"
            || !r.GetProperty("applyAttempted").GetBoolean() || !r.GetProperty("restoreAttempted").GetBoolean()
            || !r.GetProperty("originalVerified").GetBoolean()) { throw new InvalidDataException("Smoke completion is not verified."); }
        int original = Vector(b.GetProperty("cpuTuning").GetProperty("liveState"));
        if (Vector(r.GetProperty("baseline")) != original || Vector(r.GetProperty("final")) != original
            || Vector(r.GetProperty("forward")) != (original == 0 ? -1 : 0))
        { throw new InvalidDataException("Smoke vectors do not prove the exact original restoration."); }
        var settings = b.GetProperty("requestedSettings").Deserialize<CpuTuningSettings>(Options);
        if (settings is null || settings.PowerLimitWatts is not null
            || settings.CurveOptimizer is not { Value: -1 } curve || curve.Enabled != (original == 0))
        { throw new InvalidDataException("Smoke request is not optimizer-only."); }
        var status = b.GetProperty("cpuTuning").Deserialize<CpuTuningStatus>(Options);
        if (status is not { IsWellFormed: true, Live.CanWrite: true } || status.RestorePending
            || status.LiveRecovery is not { State: CpuTuningRecoveryState.None, Features: CpuTuningFeatures.None }
            || status.FirmwareRecovery is not { State: CpuTuningRecoveryState.None, Features: CpuTuningFeatures.None })
        { throw new InvalidDataException("Smoke baseline has unresolved ownership."); }
    }
    internal static void VerifyObservation(byte[] resultBytes, string version, DateTimeOffset launched, DateTimeOffset completed)
    {
        using var result = Parse(resultBytes);
        var r = result.RootElement;
        if (r.GetProperty("mode").GetString() != "ObserveOnly" || r.GetProperty("outcome").GetString() != "ObservationRecorded"
            || r.GetProperty("phase").GetString() != "Observation" || r.GetProperty("expectedServiceVersion").GetString() != version
            || r.GetProperty("applyAttempted").GetBoolean() || r.GetProperty("restoreAttempted").GetBoolean()
            || r.GetProperty("originalVerified").GetBoolean()
            || r.GetProperty("baseline").ValueKind != JsonValueKind.Null || r.GetProperty("forward").ValueKind != JsonValueKind.Null
            || r.GetProperty("final").ValueKind != JsonValueKind.Null
            || r.GetProperty("responseReceivedAtUtc").GetDateTimeOffset() < launched
            || r.GetProperty("responseReceivedAtUtc").GetDateTimeOffset() > completed)
        { throw new InvalidDataException("Read-only CPU observation recording is not verified."); }
        var response = r.GetProperty("controlResponse");
        if (response.ValueKind != JsonValueKind.Object || response.GetProperty("message").ValueKind != JsonValueKind.String
            || !Enum.TryParse<ControlOperationStatus>(response.GetProperty("status").GetString(), out var status) || !Enum.IsDefined(status)
            || response.Deserialize<ControlOperationResult>(Options) is null)
        { throw new InvalidDataException("An actual typed CPU response is required; transport uncertainty cannot permit cleanup."); }
    }
    private static int Vector(JsonElement json)
    {
        var state = json.Deserialize<CpuLiveState>(Options);
        if (state is not { IsWellFormed: true } || state.CoreValues.Count != Cores.Length
            || !state.CoreValues.Select(c => c.CoreIndex).Order().SequenceEqual(Cores)
            || state.UniformCurveOptimizerValue is not (-1 or 0)) { throw new InvalidDataException("Complete -1/0 vector required."); }
        return state.UniformCurveOptimizerValue.Value;
    }
    private static JsonDocument Parse(byte[] bytes)
    {
        if (bytes.Length is 0 or > CpuPmLaunchSchema.ManifestLimit) { throw new InvalidDataException("Smoke evidence size denied."); }
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        try { Unique(document.RootElement); return document; } catch { document.Dispose(); throw; }
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) { throw new InvalidDataException("Duplicate evidence field."); } Unique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) { foreach (var item in value.EnumerateArray()) { Unique(item); } }
    }
}
