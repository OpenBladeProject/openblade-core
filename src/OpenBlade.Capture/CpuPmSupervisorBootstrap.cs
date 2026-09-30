using System.Diagnostics;
using System.Text.Json;

namespace OpenBlade.Capture;

internal sealed record CpuPmOuterLaunchRecord(Guid Run, string Source, string Manifest, string PresetHash,
    CpuPmAuthorityPeer Outer, CpuPmAuthorityPeer Supervisor);

internal static class CpuPmOuterLaunchSchema
{
    internal static CpuPmReviewedLaunchBootstrap ReadPreset(byte[] bytes)
    {
        using var document = Document(bytes); var root = document.RootElement;
        Fields(root, "version", "source", "manifest"); Version(root);
        return new(root.GetProperty("source").GetString()!, root.GetProperty("manifest").GetString()!);
    }
    internal static CpuPmOuterLaunchRecord ReadRecord(byte[] bytes)
    {
        using var document = Document(bytes); var root = document.RootElement;
        Fields(root, "version", "run", "source", "manifest", "presetHash", "outer", "supervisor"); Version(root);
        string text = root.GetProperty("run").GetString()!;
        if (!Guid.TryParseExact(text, "N", out var run) || run == Guid.Empty || run.ToString("N") != text) { throw new InvalidDataException(); }
        var trusted = new CpuPmReviewedLaunchBootstrap(root.GetProperty("source").GetString()!, root.GetProperty("manifest").GetString()!);
        string hash = root.GetProperty("presetHash").GetString()!;
        if (!CpuPmLaunchSchema.IsHex(hash, 64, false)) { throw new InvalidDataException(); }
        var outer = Peer(root.GetProperty("outer")); var supervisor = Peer(root.GetProperty("supervisor"));
        if (outer.ProcessId == supervisor.ProcessId || supervisor.ExecutablePath != Path.Combine(trusted.PayloadPath, "OpenBlade.PmSupervisor.exe"))
        { throw new InvalidDataException(); }
        return new(run, trusted.Source, trusted.ManifestSha256, hash, outer, supervisor);
    }
    internal static bool Matches(CpuPmOuterLaunchRecord record, Guid run, CpuPmReviewedLaunchBootstrap preset,
        string presetHash, CpuPmAuthorityPeer actualSupervisor, int actualOuterParent) => record.Run == run
        && record.Source == preset.Source && record.Manifest == preset.ManifestSha256 && record.PresetHash == presetHash
        && record.Supervisor == actualSupervisor && record.Outer.ProcessId == actualOuterParent;
    private static JsonDocument Document(byte[] bytes) => bytes.Length is > 0 and <= CpuPmLaunchSchema.RecordLimit
        ? JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 3 }) : throw new InvalidDataException();
    private static void Fields(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) { throw new InvalidDataException(); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        { if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) { throw new InvalidDataException(); } }
        if (seen.Count != names.Length) { throw new InvalidDataException(); }
    }
    private static void Version(JsonElement value)
    { if (!value.GetProperty("version").TryGetInt32(out int version) || version != 1) { throw new InvalidDataException(); } }
    private static CpuPmAuthorityPeer Peer(JsonElement value)
    {
        Fields(value, "pid", "created", "image", "sha256");
        var result = new CpuPmAuthorityPeer(value.GetProperty("pid").GetInt32(), value.GetProperty("created").GetInt64(),
            value.GetProperty("image").GetString()!, value.GetProperty("sha256").GetString()!);
        if (!result.IsValid || result.ExecutablePath != Path.GetFullPath(result.ExecutablePath)
            || !result.ExecutablePath.StartsWith(@"C:\", StringComparison.Ordinal) || result.ExecutablePath.Contains('/'))
        { throw new InvalidDataException(); }
        return result;
    }
}

// Trust originates in the separately reviewed outer launcher AND its adjacent
// preset, outside the payload. Private ACLs/retained processes bind that delivery;
// neither a self-hash nor this JSON schema proves that a human reviewed anything.
// Only the explicit developer entry path under that trusted-operator premise calls
// this factory. The record does not attest driver ownership or kernel continuity.
internal sealed class CpuPmSupervisorBootstrap
{
    internal const string FixedRoot = @"C:\OpenBlade\validation-archive\cpu-tuning-re\pm-supervisor-bootstrap";
    internal const string PresetPath = FixedRoot + @"\reviewed-preset.json";
    private static CpuPmSupervisorBootstrap? processRoot;
    private static int claimed;
    private readonly WindowsCpuPmLaunchFiles files = new();
    private WindowsCpuPmPinnedPeer? outer, supervisor;
    internal CpuPmReviewedLaunchBootstrap Trusted { get; private set; } = null!;
    internal CpuPmOuterLaunchRecord Record { get; private set; } = null!;
    private CpuPmSupervisorBootstrap() { }
    internal static CpuPmSupervisorBootstrap Load(Guid run, bool isSupervisor, CancellationToken token)
    {
        if (run == Guid.Empty || Interlocked.CompareExchange(ref claimed, 1, 0) != 0) { throw new IOException("Bootstrap already consumed."); }
        var result = new CpuPmSupervisorBootstrap(); processRoot = result;
        result.Initialize(run, isSupervisor, token); return result;
    }
    private void Initialize(Guid run, bool isSupervisor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CpuPmAuthenticatedChildRun.PinAncestors(files, FixedRoot); files.PinDirectory(FixedRoot, true, true);
        var preset = files.PinFile(PresetPath, CpuPmLaunchSchema.RecordLimit, true);
        Trusted = CpuPmOuterLaunchSchema.ReadPreset(preset.ReadBounded(CpuPmLaunchSchema.RecordLimit));
        string directory = Path.Combine(FixedRoot, run.ToString("N")); files.PinDirectory(directory, true, false);
        string path = Path.Combine(directory, "bootstrap.json"); var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            token.ThrowIfCancellationRequested(); if (wait.Elapsed >= TimeSpan.FromSeconds(5)) { throw new IOException("Bootstrap publication unavailable."); }
            Thread.Sleep(25);
        }
        token.ThrowIfCancellationRequested();
        var record = files.PinFile(path, CpuPmLaunchSchema.RecordLimit, true);
        Record = CpuPmOuterLaunchSchema.ReadRecord(record.ReadBounded(CpuPmLaunchSchema.RecordLimit));
        if (Record.Run != run || Record.Source != Trusted.Source || Record.Manifest != Trusted.ManifestSha256
            || Record.PresetHash != preset.Sha256) { throw new IOException("Bootstrap preset mismatch."); }
        CpuPmAuthenticatedChildRun.PinAncestors(files, Trusted.PayloadPath); files.PinDirectory(Trusted.PayloadPath, true, true);
        var manifest = files.PinFile(Path.Combine(Trusted.PayloadPath, "manifest.json"), CpuPmLaunchSchema.ManifestLimit, true);
        var entries = CpuPmLaunchSchema.ParseManifest(manifest.ReadBounded(CpuPmLaunchSchema.ManifestLimit), Trusted);
        CpuPmAuthenticatedChildRun.ValidatePayload(files, Trusted.PayloadPath, entries, token);
        if (entries.Single(e => e.Name == "OpenBlade.PmSupervisor.exe").Sha256 != Record.Supervisor.ExecutableSha256)
        { throw new IOException("Supervisor manifest mismatch."); }
        outer = WindowsCpuPmPinnedPeer.Open(Record.Outer);
        using var current = WindowsCpuPmProcessProbe.Current(); var observed = current.Read(true);
        if (isSupervisor)
        {
            var actual = new CpuPmAuthorityPeer(observed.Pid, observed.Created, observed.Image!, Record.Supervisor.ExecutableSha256);
            if (observed.State != CpuPmChildState.Running || observed.Pid != Environment.ProcessId
                || !CpuPmOuterLaunchSchema.Matches(Record, run, Trusted, preset.Sha256, actual, WindowsCpuPmProcessProbe.CurrentParentId()))
            { throw new IOException("Actual supervisor bootstrap binding denied."); }
        }
        else
        {
            if (WindowsCpuPmProcessProbe.CurrentParentId() != Record.Supervisor.ProcessId) { throw new IOException("Capture parent denied."); }
            supervisor = WindowsCpuPmPinnedPeer.Open(Record.Supervisor);
        }
        AssertHeld(); token.ThrowIfCancellationRequested();
    }
    internal void AssertHeld()
    {
        files.AssertAllPins();
        if (outer?.IsCurrent() != true || supervisor is not null && !supervisor.IsCurrent()) { throw new IOException("Bootstrap live parent unavailable."); }
        GC.KeepAlive(processRoot);
    }
}
