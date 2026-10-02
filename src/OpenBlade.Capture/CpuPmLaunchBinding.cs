using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

// This value MUST originate outside the inspected payload/records: a reviewed
// launcher preset or authenticated bootstrap delivery. There is intentionally no
// CLI/JSON/native factory for it here. Hashing a record cannot create this trust.
internal sealed record CpuPmReviewedLaunchBootstrap
{
    internal string Source { get; }
    internal string ManifestSha256 { get; }
    internal CpuPmObservationProfile Profile { get; }
    internal string PayloadPath => Path.Combine(PayloadRoot, Source, ManifestSha256);
    internal const string PayloadRoot = @"C:\OpenBlade\validation-archive\cpu-tuning-re\pm-supervisor-payloads";
    internal CpuPmReviewedLaunchBootstrap(string source, string manifestSha256,
        CpuPmObservationProfile profile = CpuPmObservationProfile.IdleThreeSamples)
    {
        if (!CpuPmLaunchSchema.IsHex(source, 40, lower: true) || !CpuPmLaunchSchema.IsHex(manifestSha256, 64, lower: false))
        { throw new ArgumentException("Independent reviewed bootstrap required."); }
        if (profile is not (CpuPmObservationProfile.IdleThreeSamples or CpuPmObservationProfile.RangeSingleSample))
        { throw new ArgumentException("Fixed reviewed profile required.", nameof(profile)); }
        Source = source; ManifestSha256 = manifestSha256; Profile = profile;
    }
}
internal sealed record CpuPmLaunchFile(string Name, long Length, string Sha256);
internal sealed record CpuPmLaunchOwner(Guid Run, Guid Lifetime, string Source, string Manifest,
    CpuPmAuthorityPeer Parent, string ChildExecutable, string ChildSha256);
internal sealed record CpuPmLaunchChild(Guid Run, Guid Lifetime, string Source, string Manifest,
    string OwnerSha256, CpuPmAuthorityPeer Parent, CpuPmAuthorityPeer Child);
internal sealed record CpuPmLaunchEntry(string Name, bool Directory);
internal interface ICpuPmLaunchFilePin
{
    long Length { get; }
    string Sha256 { get; }
    byte[] ReadBounded(int maximumBytes);
}
internal interface ICpuPmLaunchFiles : IDisposable
{
    void PinDirectory(string path, bool privateAcl, bool protectedAcl);
    ICpuPmLaunchFilePin PinFile(string path, long maximumBytes, bool privateAcl);
    IEnumerable<CpuPmLaunchEntry> Enumerate(string path);
    void AssertAllPins();
}
internal interface ICpuPmLaunchProcesses : IDisposable
{
    // Current child must come from the actual current process, not a requested PID.
    CpuPmAuthorityPeer ReadCurrentChild();
    int ReadCurrentParentProcessId();
    void RetainLiveParent(CpuPmAuthorityPeer expected);
    bool IsSameLiveParent(CpuPmAuthorityPeer expected);
    // Native implementation roots the entire successful lease until process exit.
    void RetainToCurrentProcessExit(CpuPmAuthenticatedChildRun run);
}

internal static class CpuPmLaunchSchema
{
    internal const int RecordLimit = 16384, ManifestLimit = 65536, MaximumFiles = 256;
    // Three complete self-contained voltage-validation trees plus installed
    // Service pins need their own fixed bounds. Ordinary PM callers keep theirs.
    internal const int VoltageManifestLimit = 512 * 1024, VoltageMaximumFiles = 1536,
        VoltageMaximumPins = 4096, VoltageMaximumEntries = 2048;
    internal const long VoltageMaximumPayloadBytes = 1024L * 1024 * 1024;
    internal const long MaximumFileBytes = 128L * 1024 * 1024, MaximumPayloadBytes = 512L * 1024 * 1024;
    internal static bool IsHex(string? value, int length, bool lower) => value is not null && value.Length == length
        && value.All(c => c is >= '0' and <= '9' || (lower ? c is >= 'a' and <= 'f' : c is >= 'A' and <= 'F'));
    internal static bool RelativeName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 200 || name.Contains('\\') || name.StartsWith('/') || name.EndsWith('/')) { return false; }
        string[] parts = name.Split('/');
        if (parts.Length > 3) { return false; }
        foreach (string part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) { return false; }
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9') { return false; }
        }
        return true;
    }
    private static JsonDocument Document(byte[] bytes, int limit)
    {
        if (bytes.Length is 0 || bytes.Length > limit) { throw new InvalidDataException(); }
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
    }
    private static void Fields(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw new InvalidDataException(); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        { if (!seen.Add(property.Name) || !expected.Contains(property.Name, StringComparer.Ordinal)) { throw new InvalidDataException(); } }
        if (seen.Count != expected.Length) { throw new InvalidDataException(); }
    }
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new InvalidDataException();
    private static Guid Id(JsonElement value, string name)
    { string text = Text(value, name); return Guid.TryParseExact(text, "N", out var id) && id != Guid.Empty && text == id.ToString("N") ? id : throw new InvalidDataException(); }
    private static void Header(JsonElement value)
    {
        if (!value.GetProperty("version").TryGetInt32(out int version) || version != 1
            || !IsHex(Text(value, "source"), 40, true) || !IsHex(Text(value, "manifest"), 64, false)) { throw new InvalidDataException(); }
    }
    private static CpuPmAuthorityPeer Peer(JsonElement value)
    {
        Fields(value, "pid", "created", "executable", "sha256");
        if (!value.GetProperty("pid").TryGetInt32(out int pid) || !value.GetProperty("created").TryGetInt64(out long created)) { throw new InvalidDataException(); }
        var peer = new CpuPmAuthorityPeer(pid, created, Text(value, "executable"), Text(value, "sha256"));
        if (!peer.IsValid || peer.ExecutablePath.Length > 400 || peer.ExecutablePath.StartsWith(@"\\", StringComparison.Ordinal)
            || peer.ExecutablePath != Path.GetFullPath(peer.ExecutablePath)) { throw new InvalidDataException(); }
        return peer;
    }
    internal static CpuPmLaunchOwner ParseOwner(byte[] bytes)
    {
        using var doc = Document(bytes, RecordLimit); var root = doc.RootElement;
        Fields(root, "version", "run", "lifetime", "source", "manifest", "parent", "childExecutable", "childSha256", "serviceName", "serviceImage"); Header(root);
        if (Text(root, "serviceName") != WindowsCpuPmDriverRegistration.Name || Text(root, "serviceImage") != WindowsCpuPmDriverRegistration.Image
            || !IsHex(Text(root, "childSha256"), 64, false)) { throw new InvalidDataException(); }
        return new(Id(root, "run"), Id(root, "lifetime"), Text(root, "source"), Text(root, "manifest"), Peer(root.GetProperty("parent")), Text(root, "childExecutable"), Text(root, "childSha256"));
    }
    private static object PeerJson(CpuPmAuthorityPeer peer) => new { pid = peer.ProcessId, created = peer.CreationFileTimeUtc, executable = peer.ExecutablePath, sha256 = peer.ExecutableSha256 };
    internal static byte[] EncodeOwner(CpuPmLaunchOwner value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            run = value.Run.ToString("N"),
            lifetime = value.Lifetime.ToString("N"),
            source = value.Source,
            manifest = value.Manifest,
            parent = PeerJson(value.Parent),
            childExecutable = value.ChildExecutable,
            childSha256 = value.ChildSha256,
            serviceName = WindowsCpuPmDriverRegistration.Name,
            serviceImage = WindowsCpuPmDriverRegistration.Image
        });
        _ = ParseOwner(bytes); return bytes;
    }
    internal static byte[] EncodeChild(CpuPmLaunchChild value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            run = value.Run.ToString("N"),
            lifetime = value.Lifetime.ToString("N"),
            source = value.Source,
            manifest = value.Manifest,
            ownerSha256 = value.OwnerSha256,
            parent = PeerJson(value.Parent),
            child = PeerJson(value.Child)
        });
        _ = ParseChild(bytes); return bytes;
    }
    internal static CpuPmLaunchChild ParseChild(byte[] bytes)
    {
        using var doc = Document(bytes, RecordLimit); var root = doc.RootElement;
        Fields(root, "version", "run", "lifetime", "source", "manifest", "ownerSha256", "parent", "child"); Header(root);
        if (!IsHex(Text(root, "ownerSha256"), 64, false)) { throw new InvalidDataException(); }
        return new(Id(root, "run"), Id(root, "lifetime"), Text(root, "source"), Text(root, "manifest"), Text(root, "ownerSha256"), Peer(root.GetProperty("parent")), Peer(root.GetProperty("child")));
    }
    internal static IReadOnlyList<CpuPmLaunchFile> ParseManifest(byte[] bytes, CpuPmReviewedLaunchBootstrap trusted)
        => ParseManifest(bytes, trusted.Source, trusted.ManifestSha256, ["OpenBlade.Capture.exe", "OpenBlade.PmSupervisor.exe"]);
    internal static IReadOnlyList<CpuPmLaunchFile> ParseManifest(byte[] bytes, string source, string manifestHash,
        IReadOnlyList<string> requiredFiles, bool voltageValidation = false)
    {
        if (!IsHex(source, 40, true) || !IsHex(manifestHash, 64, false)
            || Convert.ToHexString(SHA256.HashData(bytes)) != manifestHash) { throw new InvalidDataException(); }
        using var doc = Document(bytes, voltageValidation ? VoltageManifestLimit : ManifestLimit); var root = doc.RootElement; Fields(root, "version", "source", "files");
        if (!root.GetProperty("version").TryGetInt32(out int version) || version != 1 || Text(root, "source") != source
            || root.GetProperty("files").ValueKind != JsonValueKind.Array) { throw new InvalidDataException(); }
        var files = new List<CpuPmLaunchFile>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var item in root.GetProperty("files").EnumerateArray())
        {
            Fields(item, "name", "length", "sha256"); string name = Text(item, "name"), hash = Text(item, "sha256");
            if (files.Count >= (voltageValidation ? VoltageMaximumFiles : MaximumFiles) || !RelativeName(name) || !names.Add(name) || name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                || !IsHex(hash, 64, false) || !item.GetProperty("length").TryGetInt64(out long length) || length is < 0 or > MaximumFileBytes
                || length > (voltageValidation ? VoltageMaximumPayloadBytes : MaximumPayloadBytes) - total) { throw new InvalidDataException(); }
            total += length; files.Add(new(name, length, hash));
        }
        if (files.Count == 0 || requiredFiles.Count == 0 || requiredFiles.Any(name => !names.Contains(name))) { throw new InvalidDataException(); }
        // A path cannot be both a file and a directory, including case aliases.
        foreach (var file in files)
        {
            string[] parts = file.Name.Split('/');
            for (int i = 1; i < parts.Length; i++) { if (names.Contains(string.Join('/', parts.Take(i)))) { throw new InvalidDataException(); } }
        }
        return files.AsReadOnly();
    }
}

internal sealed class CpuPmAuthenticatedChildRun : ICpuPmAuthenticatedCaptureRun
{
    private readonly ICpuPmLaunchFiles files;
    private readonly ICpuPmLaunchProcesses processes;
    private readonly CpuPmAuthorityPeer self;
    private readonly List<object> retainedEvidence = [];
    private bool failed, sinkCreated;
    public Guid RunId { get; }
    internal Guid LifetimeId { get; }
    internal CpuPmAuthorityPeer ParentIdentity { get; }
    private CpuPmAuthenticatedChildRun(CpuPmLaunchOwner owner, CpuPmAuthorityPeer self, ICpuPmLaunchFiles files, ICpuPmLaunchProcesses processes)
    { RunId = owner.Run; LifetimeId = owner.Lifetime; ParentIdentity = owner.Parent; this.self = self; this.files = files; this.processes = processes; }

    // Consumes files/process context even on failure. No native trust bootstrap
    // factory exists; a reviewed launcher must supply trusted independently.
    internal static CpuPmAuthenticatedChildRun? TryLoad(Guid run, CpuPmReviewedLaunchBootstrap trusted,
        ICpuPmLaunchFiles files, ICpuPmLaunchProcesses processes, CancellationToken token)
    {
        bool retained = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (run == Guid.Empty || trusted is null) { throw new InvalidDataException(); }
            string runPath = Path.Combine(WindowsCpuPmCaptureEvidenceFiles.FixedRoot, run.ToString("N"));
            PinAncestors(files, runPath); files.PinDirectory(runPath, true, true);
            string control = Path.Combine(runPath, "control"); files.PinDirectory(control, true, false); files.PinDirectory(Path.Combine(runPath, "evidence"), true, false);
            PinAncestors(files, trusted.PayloadPath); files.PinDirectory(trusted.PayloadPath, true, true);
            var manifestPin = files.PinFile(Path.Combine(trusted.PayloadPath, "manifest.json"), CpuPmLaunchSchema.ManifestLimit, true);
            var manifest = CpuPmLaunchSchema.ParseManifest(manifestPin.ReadBounded(CpuPmLaunchSchema.ManifestLimit), trusted);
            ValidatePayload(files, trusted.PayloadPath, manifest, token);
            token.ThrowIfCancellationRequested();
            var ownerPin = files.PinFile(Path.Combine(control, "owner.json"), CpuPmLaunchSchema.RecordLimit, true);
            var childPin = files.PinFile(Path.Combine(control, "child.json"), CpuPmLaunchSchema.RecordLimit, true);
            byte[] ownerBytes = ownerPin.ReadBounded(CpuPmLaunchSchema.RecordLimit), childBytes = childPin.ReadBounded(CpuPmLaunchSchema.RecordLimit);
            if (ownerPin.Sha256 != Convert.ToHexString(SHA256.HashData(ownerBytes)) || childPin.Sha256 != Convert.ToHexString(SHA256.HashData(childBytes))) { throw new InvalidDataException(); }
            var owner = CpuPmLaunchSchema.ParseOwner(ownerBytes);
            var child = CpuPmLaunchSchema.ParseChild(childBytes);
            string parentPath = Path.Combine(trusted.PayloadPath, "OpenBlade.PmSupervisor.exe"), childPath = Path.Combine(trusted.PayloadPath, "OpenBlade.Capture.exe");
            string parentHash = manifest.Single(f => f.Name == "OpenBlade.PmSupervisor.exe").Sha256, childHash = manifest.Single(f => f.Name == "OpenBlade.Capture.exe").Sha256;
            if (owner.Run != run || child.Run != run || owner.Lifetime != child.Lifetime || owner.Source != trusted.Source || child.Source != trusted.Source
                || owner.Manifest != trusted.ManifestSha256 || child.Manifest != trusted.ManifestSha256 || child.OwnerSha256 != ownerPin.Sha256
                || owner.Parent != child.Parent || owner.Parent.ExecutablePath != parentPath || owner.Parent.ExecutableSha256 != parentHash
                || owner.ChildExecutable != childPath || owner.ChildSha256 != childHash || child.Child.ExecutablePath != childPath || child.Child.ExecutableSha256 != childHash)
            { throw new InvalidDataException(); }
            var current = processes.ReadCurrentChild();
            if (current != child.Child || current.ProcessId == owner.Parent.ProcessId || processes.ReadCurrentParentProcessId() != owner.Parent.ProcessId) { throw new InvalidDataException(); }
            processes.RetainLiveParent(owner.Parent); token.ThrowIfCancellationRequested();
            if (!processes.IsSameLiveParent(owner.Parent) || processes.ReadCurrentChild() != current || processes.ReadCurrentParentProcessId() != owner.Parent.ProcessId) { throw new InvalidDataException(); }
            files.AssertAllPins(); token.ThrowIfCancellationRequested();
            if (!processes.IsSameLiveParent(owner.Parent) || processes.ReadCurrentChild() != current
                || processes.ReadCurrentParentProcessId() != owner.Parent.ProcessId) { throw new InvalidDataException(); }
            token.ThrowIfCancellationRequested();
            var result = new CpuPmAuthenticatedChildRun(owner, current, files, processes);
            // Root before returning. Pin validity is independent of pipe authority.
            processes.RetainToCurrentProcessExit(result); retained = true;
            token.ThrowIfCancellationRequested(); result.AssertPinnedRun(); token.ThrowIfCancellationRequested(); return result;
        }
        catch (Exception) { return null; }
        finally { if (!retained) { try { processes.Dispose(); } finally { files.Dispose(); } } }
    }
    internal static void PinAncestors(ICpuPmLaunchFiles files, string final)
    {
        var parents = new Stack<string>();
        for (string? path = Path.GetDirectoryName(final); path is not null; path = Path.GetDirectoryName(path)) { parents.Push(path); }
        while (parents.Count > 0) { files.PinDirectory(parents.Pop(), false, false); }
    }
    internal static void ValidatePayload(ICpuPmLaunchFiles files, string root, IReadOnlyList<CpuPmLaunchFile> manifest, CancellationToken token, bool voltageValidation = false)
    {
        var expected = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["manifest.json"] = false };
        foreach (var file in manifest)
        {
            expected.Add(file.Name, false); string[] parts = file.Name.Split('/');
            for (int i = 1; i < parts.Length; i++) { expected.TryAdd(string.Join('/', parts.Take(i)), true); }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var directories = new Queue<string>(); directories.Enqueue("");
        while (directories.TryDequeue(out string? relative))
        {
            token.ThrowIfCancellationRequested();
            string path = relative.Length == 0 ? root : Path.Combine(root, relative.Replace('/', '\\'));
            foreach (var entry in files.Enumerate(path))
            {
                token.ThrowIfCancellationRequested();
                if (!CpuPmLaunchSchema.RelativeName(entry.Name) || entry.Name.Contains('/')) { throw new InvalidDataException(); }
                string name = relative.Length == 0 ? entry.Name : relative + '/' + entry.Name;
                if (seen.Count >= (voltageValidation ? CpuPmLaunchSchema.VoltageMaximumEntries : 768) || !seen.Add(name) || !expected.TryGetValue(name, out bool directory) || directory != entry.Directory) { throw new InvalidDataException(); }
                string canonical = expected.Keys.Single(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (name != canonical) { throw new InvalidDataException(); }
                if (directory) { files.PinDirectory(Path.Combine(root, name.Replace('/', '\\')), true, false); directories.Enqueue(name); }
            }
        }
        if (seen.Count != expected.Count) { throw new InvalidDataException(); }
        foreach (var item in manifest)
        {
            token.ThrowIfCancellationRequested();
            var pin = files.PinFile(Path.Combine(root, item.Name.Replace('/', '\\')), CpuPmLaunchSchema.MaximumFileBytes, true);
            token.ThrowIfCancellationRequested();
            if (pin.Length != item.Length || pin.Sha256 != item.Sha256) { throw new InvalidDataException(); }
        }
        files.AssertAllPins();
    }
    public void AssertPinnedRun()
    {
        if (failed) { throw new IOException("Authenticated PM run pins unavailable."); }
        try { files.AssertAllPins(); if (processes.ReadCurrentChild() != self) { throw new IOException("Current capture identity changed."); } }
        catch { failed = true; throw; }
    }
    // A child cannot attest to its own terminal state while executing this code.
    // Parent cleanup uses its independently retained process handle instead.
    public void AssertWorkerTerminal() => throw new InvalidOperationException("Child pins remain retained until process exit.");
    internal CpuPmCaptureEvidenceSink CreateWindowsEvidenceSink()
    {
        AssertPinnedRun();
        if (sinkCreated) { throw new InvalidOperationException("PM evidence sink already created."); }
        sinkCreated = true;
        var evidence = new WindowsCpuPmCaptureEvidenceFiles(this); retainedEvidence.Add(evidence);
        var sink = new CpuPmCaptureEvidenceSink(this, evidence); retainedEvidence.Add(sink); return sink;
    }
}
