using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Capture;

internal sealed record CpuVoltageReadOnlyCleanupProof(CpuPmDriverOwnerTag Tag, CpuPmAuthorityPeer Child,
    string ResultSha256, string JournalSha256);

// Resource reconciliation only. A readonly result is not CPU admission, a
// restoration claim, or authority to adopt another process/driver lifetime.
internal static class CpuVoltageReadOnlyCleanup
{
    internal static CpuVoltageReadOnlyCleanupProof Verify(CpuVoltageSessionEnrollment enrollment, Guid run,
        CpuPmProcessObservation service, string childImage, string childHash,
        IReadOnlyDictionary<string, JsonElement> records, DateTimeOffset launched, DateTimeOffset completed)
    {
        var result = records["evidence/cpu-voltage-optimizer.json"];
        byte[] bytes = Encoding.UTF8.GetBytes(result.GetRawText());
        CpuVoltageOwnedSmokeEvidence.VerifyObservation(bytes, enrollment.ServiceVersion, launched, completed);
        if (records.ContainsKey("evidence/cpu-voltage-optimizer-baseline.json")
            || records.ContainsKey("control/voltage-restoration-verified.json")
            || records.ContainsKey("control/voltage-owner-completed.json"))
        { throw new IOException("Readonly cleanup cannot reconcile voltage or completed-owner evidence."); }
        var context = records["control/voltage-context.json"];
        Unique(context);
        if (run == Guid.Empty || context.GetProperty("run").GetGuid() != run
            || context.GetProperty("Source").GetString() != enrollment.Source
            || context.GetProperty("Manifest").GetString() != enrollment.Manifest
            || context.GetProperty("ServiceVersion").GetString() != enrollment.ServiceVersion
            || context.GetProperty("Mode").GetString() != "ObserveOnly"
            || context.GetProperty("BudgetSeconds").GetInt32() != 600
            || context.GetProperty("Identity").Deserialize<CpuPmProcessObservation>() != service)
        { throw new IOException("Readonly context differs from the retained Service/enrollment."); }
        var childRecord = records["control/voltage-child.json"]; Unique(childRecord);
        var child = childRecord.Deserialize<CpuPmAuthorityPeer>();
        if (child is not { IsValid: true } || child.ExecutablePath != childImage || child.ExecutableSha256 != childHash
            || child.CreationFileTimeUtc < launched.UtcDateTime.ToFileTimeUtc()
            || child.CreationFileTimeUtc > completed.UtcDateTime.ToFileTimeUtc())
        { throw new IOException("Readonly child provenance differs."); }
        string journal = records["control/lifecycle.jsonl"].GetString() ?? throw new IOException("Missing lifecycle.");
        string[] lines = journal.Split('\n');
        string[] phases = ["CreateIntent", "Created", "TagIntent", "Tagged", "StartIntent", "Started", "ChildLaunchIntent", "ChildBound"];
        if (Encoding.UTF8.GetByteCount(journal) > CpuPmDurableDriverJournal.MaximumBytes
            || lines.Length != phases.Length + 1 || lines[^1] != "") { throw new IOException("Incomplete or advanced lifecycle."); }
        CpuPmDriverOwnerTag? tag = null;
        for (int i = 0; i < phases.Length; i++)
        {
            using var document = JsonDocument.Parse(lines[i]); var record = document.RootElement; Unique(record);
            if (record.EnumerateObject().Count() != 6 || record.GetProperty("version").GetInt32() != 1
                || record.GetProperty("sequence").GetInt32() != i + 1 || record.GetProperty("phase").GetString() != phases[i]
                || record.GetProperty("run").GetString() != run.ToString("N")) { throw new IOException("Lifecycle sequence differs."); }
            string owner = record.GetProperty("owner").GetString() ?? "";
            string[] fields = owner.Split(':');
            if (fields.Length != 5 || fields[0] != "1" || fields[1] != run.ToString("N")
                || fields[3] != enrollment.Manifest || !Guid.TryParseExact(fields[2], "N", out Guid lifetime)
                || record.GetProperty("lifetime").GetString() != fields[2]) { throw new IOException("Lifecycle ownership differs."); }
            var parsed = new CpuPmDriverOwnerTag(run, lifetime, enrollment.Manifest, fields[4]);
            if (parsed.Value != owner || tag is not null && parsed != tag) { throw new IOException("Lifecycle tag changed."); }
            tag = parsed;
        }
        return new(tag!, child, Hash(bytes), Hash(Encoding.UTF8.GetBytes(journal)));
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw new IOException("Expected evidence object."); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) { throw new IOException("Duplicate evidence property."); }
            if (property.Value.ValueKind == JsonValueKind.Object) { Unique(property.Value); }
        }
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static bool IsParkReason(string? value) => value is "CpuVoltageOwner=CompletionOrCleanupUnknown"
        or "CpuVoltageOwner=ChildBindingUnknown" or "CpuVoltageOwner=DriverOrLaunchAdmissionUnknown"
        || value is not null && value.StartsWith("CpuVoltageOwner=Unknown_", StringComparison.Ordinal)
            && value.Length <= 256 && value[24..].All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    // Execute synchronously on ONE owned worker thread; a timed-out native call
    // is never replaced. Every failed step retains the platform and its handles.
    internal static void Execute(ICpuVoltageReadOnlyCleanupPlatform platform)
    {
        platform.Validate();
        platform.ShutdownServiceAndVerifyTerminal();
        platform.Validate();
        platform.TerminateParkedOwnerAndVerifyTerminal();
        platform.Validate();
        platform.StopDeleteOwnedDriverAndVerifyAbsence();
        platform.Validate();
        platform.RecordCompletionAndRelease();
    }
}

internal interface ICpuVoltageReadOnlyCleanupPlatform
{
    void Validate();
    void ShutdownServiceAndVerifyTerminal();
    void TerminateParkedOwnerAndVerifyTerminal();
    void StopDeleteOwnedDriverAndVerifyAbsence();
    void RecordCompletionAndRelease();
}
