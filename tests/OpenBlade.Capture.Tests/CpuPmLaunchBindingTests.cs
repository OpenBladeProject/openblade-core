using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

// In-memory files and process observations only. No Windows provider is called.
public sealed class CpuPmLaunchBindingTests
{
    private static readonly Guid Run = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Lifetime = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [Fact]
    public void ExactRecordsAndReviewedCompletePayloadBindActualCurrentChild()
    {
        var f = new Fixture(); var run = f.Load(); Assert.NotNull(run);
        Assert.Equal(Run, run.RunId); Assert.Equal(Lifetime, run.LifetimeId); Assert.Equal(f.Parent, run.ParentIdentity);
        Assert.Same(run, f.Processes.RetainedRun); Assert.False(f.Files.Disposed); Assert.False(f.Processes.Disposed);
        run.AssertPinnedRun();
        // Channel loss/parent exit revokes invocation separately, not returned-batch persistence.
        f.Processes.ParentAlive = false; run.AssertPinnedRun();
        Assert.Throws<InvalidOperationException>(run.AssertWorkerTerminal); Assert.False(f.Files.Disposed);
    }

    [Theory]
    [InlineData("child-pid")]
    [InlineData("child-created")]
    [InlineData("child-image")]
    [InlineData("child-hash")]
    [InlineData("parent-dead")]
    [InlineData("parent-created")]
    [InlineData("ancestry")]
    [InlineData("late-self-change")]
    [InlineData("late-cancel")]
    public void RetainedAndCurrentProcessEvidenceCannotBeReplacedByRecords(string fault)
    {
        var f = new Fixture(); using var cancel = new CancellationTokenSource();
        if (fault == "child-pid") { f.Processes.Self = f.Child with { ProcessId = 33 }; }
        if (fault == "child-created") { f.Processes.Self = f.Child with { CreationFileTimeUtc = 201 }; }
        if (fault == "child-image") { f.Processes.Self = f.Child with { ExecutablePath = @"C:\other.exe" }; }
        if (fault == "child-hash") { f.Processes.Self = f.Child with { ExecutableSha256 = new('F', 64) }; }
        if (fault == "parent-dead") { f.Processes.ParentAlive = false; }
        if (fault == "parent-created") { f.Processes.Parent = f.Parent with { CreationFileTimeUtc = 101 }; }
        if (fault == "ancestry") { f.Processes.Ancestry = 999; }
        if (fault == "late-self-change") { f.Processes.OnRetainParent = () => f.Processes.Self = f.Child with { CreationFileTimeUtc = 201 }; }
        if (fault == "late-cancel") { f.Processes.OnRetainParent = cancel.Cancel; }
        Assert.Null(f.Load(cancel.Token)); Assert.Null(f.Processes.RetainedRun); Assert.True(f.Files.Disposed); Assert.True(f.Processes.Disposed);
    }

    [Theory]
    [InlineData("owner-run")]
    [InlineData("child-run")]
    [InlineData("lifetime")]
    [InlineData("source")]
    [InlineData("manifest")]
    [InlineData("owner-hash")]
    [InlineData("parent-record")]
    [InlineData("child-path")]
    public void BothRecordsMustMatchIndependentBootstrapAndEachOther(string fault)
    {
        var f = new Fixture();
        if (fault == "owner-run") { f.OwnerRecord = f.OwnerRecord with { Run = Lifetime }; }
        if (fault == "child-run") { f.ChildRecord = f.ChildRecord with { Run = Lifetime }; }
        if (fault == "lifetime") { f.ChildRecord = f.ChildRecord with { Lifetime = Run }; }
        if (fault == "source") { f.OwnerRecord = f.OwnerRecord with { Source = new('f', 40) }; }
        if (fault == "manifest") { f.OwnerRecord = f.OwnerRecord with { Manifest = new('F', 64) }; }
        if (fault == "parent-record") { f.ChildRecord = f.ChildRecord with { Parent = f.Parent with { ProcessId = 999 } }; }
        if (fault == "child-path") { f.OwnerRecord = f.OwnerRecord with { ChildExecutable = @"C:\forged.exe" }; }
        f.WriteRecords();
        if (fault == "owner-hash") { f.ChildRecord = f.ChildRecord with { OwnerSha256 = new('F', 64) }; f.Files.Data[f.ChildPath] = CpuPmLaunchSchema.EncodeChild(f.ChildRecord); }
        Assert.Null(f.Load()); Assert.True(f.Files.Disposed);
    }

    [Theory]
    [InlineData("missing-child-record")]
    [InlineData("extra-payload")]
    [InlineData("extra-directory")]
    [InlineData("missing-runtime")]
    [InlineData("changed-bytes")]
    [InlineData("file-as-directory")]
    [InlineData("case-alias")]
    [InlineData("reparse")]
    [InlineData("pin-loss")]
    [InlineData("owner-read-hash")]
    [InlineData("unexpected-read-error")]
    public void CompleteTreeAndRetainedPinsRejectPartialOrChangedEvidence(string fault)
    {
        var f = new Fixture(); string payload = f.Trusted.PayloadPath;
        if (fault == "missing-child-record") { f.Files.Data.Remove(f.ChildPath); }
        if (fault == "extra-payload") { f.Files.Data[Path.Combine(payload, "injected.dll")] = [1]; }
        if (fault == "extra-directory") { f.Files.ExtraEntry = new("foreign", true); }
        if (fault == "missing-runtime") { f.Files.Data.Remove(Path.Combine(payload, "runtimes", "native.dll")); }
        if (fault == "changed-bytes") { f.Files.Data[Path.Combine(payload, "OpenBlade.Capture.exe")] = [9]; }
        if (fault == "file-as-directory") { f.Files.TransformEntry = entry => entry.Name == "OpenBlade.Capture.exe" ? entry with { Directory = true } : entry; }
        if (fault == "case-alias") { f.Files.TransformEntry = entry => entry.Name == "OpenBlade.Capture.exe" ? entry with { Name = "openblade.capture.exe" } : entry; }
        if (fault == "reparse") { f.Files.FailPinPath = Path.Combine(payload, "runtimes"); }
        if (fault == "pin-loss") { f.Files.PinsValid = false; }
        if (fault == "owner-read-hash") { f.Files.WrongHashPath = f.OwnerPath; }
        if (fault == "unexpected-read-error") { f.Files.ThrowReadPath = f.OwnerPath; }
        Assert.Null(f.Load()); Assert.True(f.Files.Disposed); Assert.Null(f.Processes.RetainedRun);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("depth")]
    [InlineData("oversized")]
    [InlineData("numeric-shape")]
    public void StrictRecordSchemaRejectsMalformedInputBeforeBinding(string fault)
    {
        var f = new Fixture(); string text = Encoding.UTF8.GetString(f.Files.Data[f.OwnerPath]);
        f.Files.Data[f.OwnerPath] = fault switch
        {
            "duplicate" => Encoding.UTF8.GetBytes(text.Replace("\"version\":1", "\"version\":1,\"version\":1")),
            "unknown" => Encoding.UTF8.GetBytes(text[..^1] + ",\"active\":true}"),
            "missing" => Encoding.UTF8.GetBytes(text.Replace("\"version\":1,", "")),
            "depth" => Encoding.UTF8.GetBytes("{\"parent\":{\"a\":{\"b\":{\"c\":{\"d\":1}}}}}"),
            "numeric-shape" => Encoding.UTF8.GetBytes(text.Replace("\"pid\":31", "\"pid\":31.0")),
            _ => new byte[16385],
        };
        Assert.Null(f.Load()); Assert.Null(f.Processes.RetainedRun);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("x\\escape.dll")]
    [InlineData("/absolute.dll")]
    [InlineData("x:y.dll")]
    [InlineData("trailing.")]
    [InlineData("CON.dll")]
    [InlineData("LPT1")]
    [InlineData("a/b/c/d.dll")]
    [InlineData("x//y.dll")]
    public void ManifestNamesCannotEscapeOrAliasWindowsPaths(string name) => Assert.False(CpuPmLaunchSchema.RelativeName(name));

    [Theory]
    [InlineData("duplicate")]
    [InlineData("self-hash")]
    [InlineData("wrong-source")]
    [InlineData("file-directory")]
    [InlineData("oversized")]
    [InlineData("missing-exe")]
    public void ReviewedManifestStillRequiresBoundedUnambiguousSchema(string fault)
    {
        string source = new('1', 40); var entries = new List<object>
        { new { name = "OpenBlade.Capture.exe", length = 1, sha256 = new string('A', 64) }, new { name = "OpenBlade.PmSupervisor.exe", length = 1, sha256 = new string('B', 64) } };
        if (fault == "duplicate") { entries.Add(new { name = "openblade.capture.exe", length = 1, sha256 = new string('A', 64) }); }
        if (fault == "self-hash") { entries.Add(new { name = "manifest.json", length = 1, sha256 = new string('A', 64) }); }
        if (fault == "file-directory") { entries.Add(new { name = "OpenBlade.Capture.exe/runtime.dll", length = 1, sha256 = new string('A', 64) }); }
        if (fault == "oversized") { entries.Add(new { name = "huge.dll", length = 134217729, sha256 = new string('A', 64) }); }
        if (fault == "missing-exe") { entries.RemoveAt(0); }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, source = fault == "wrong-source" ? new('f', 40) : source, files = entries });
        var trust = new CpuPmReviewedLaunchBootstrap(source, Hash(bytes));
        Assert.ThrowsAny<Exception>(() => CpuPmLaunchSchema.ParseManifest(bytes, trust));
    }

    [Fact]
    public void AuthenticatedRunPermanentlyRefusesPinLossAndCannotClaimOwnTerminal()
    {
        var f = new Fixture(); var run = f.Load()!; f.Files.PinsValid = false;
        Assert.Throws<IOException>(run.AssertPinnedRun); f.Files.PinsValid = true;
        Assert.Throws<IOException>(run.AssertPinnedRun); Assert.Throws<InvalidOperationException>(run.AssertWorkerTerminal);
        Assert.Same(run, f.Processes.RetainedRun); Assert.False(f.Files.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringFinalPinOrProcessCheckRefusesSuccessAndRetainsPublishedLease(bool duringProcessRead)
    {
        var f = new Fixture(); using var cancellation = new CancellationTokenSource();
        if (duringProcessRead) { f.Processes.OnReadCurrentChild = () => { if (f.Processes.RetainedRun is not null) { cancellation.Cancel(); } }; }
        else { f.Files.OnAssertPins = () => { if (f.Processes.RetainedRun is not null) { cancellation.Cancel(); } }; }
        Assert.Null(f.Load(cancellation.Token)); Assert.NotNull(f.Processes.RetainedRun);
        Assert.False(f.Files.Disposed); Assert.False(f.Processes.Disposed);
    }
    private sealed class Fixture
    {
        internal readonly Files Files = new(); internal readonly Processes Processes; internal readonly CpuPmReviewedLaunchBootstrap Trusted;
        internal readonly CpuPmAuthorityPeer Parent, Child; internal readonly string OwnerPath, ChildPath;
        internal CpuPmLaunchOwner OwnerRecord; internal CpuPmLaunchChild ChildRecord;
        internal Fixture()
        {
            string source = new('1', 40); byte[] capture = [1, 2], supervisor = [3, 4], runtime = [5, 6];
            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                source,
                files = new[] {
                new { name = "OpenBlade.Capture.exe", length = capture.Length, sha256 = Hash(capture) },
                new { name = "OpenBlade.PmSupervisor.exe", length = supervisor.Length, sha256 = Hash(supervisor) },
                new { name = "runtimes/native.dll", length = runtime.Length, sha256 = Hash(runtime) } }
            });
            Trusted = new(source, Hash(manifest));
            Parent = new(31, 100, Path.Combine(Trusted.PayloadPath, "OpenBlade.PmSupervisor.exe"), Hash(supervisor));
            Child = new(32, 200, Path.Combine(Trusted.PayloadPath, "OpenBlade.Capture.exe"), Hash(capture));
            Files.Payload = Trusted.PayloadPath; Files.Data[Path.Combine(Trusted.PayloadPath, "manifest.json")] = manifest;
            Files.Data[Parent.ExecutablePath] = supervisor; Files.Data[Child.ExecutablePath] = capture; Files.Data[Path.Combine(Trusted.PayloadPath, "runtimes", "native.dll")] = runtime;
            string control = Path.Combine(WindowsCpuPmCaptureEvidenceFiles.FixedRoot, Run.ToString("N"), "control");
            OwnerPath = Path.Combine(control, "owner.json"); ChildPath = Path.Combine(control, "child.json");
            OwnerRecord = new(Run, Lifetime, source, Trusted.ManifestSha256, Parent, Child.ExecutablePath, Child.ExecutableSha256);
            ChildRecord = new(Run, Lifetime, source, Trusted.ManifestSha256, new('0', 64), Parent, Child);
            WriteRecords(); Processes = new(Child, Parent);
        }
        internal void WriteRecords()
        {
            Files.Data[OwnerPath] = CpuPmLaunchSchema.EncodeOwner(OwnerRecord);
            ChildRecord = ChildRecord with { OwnerSha256 = Hash(Files.Data[OwnerPath]) };
            Files.Data[ChildPath] = CpuPmLaunchSchema.EncodeChild(ChildRecord);
        }
        internal CpuPmAuthenticatedChildRun? Load(CancellationToken token = default) => CpuPmAuthenticatedChildRun.TryLoad(Run, Trusted, Files, Processes, token);
    }
    internal sealed class Files : ICpuPmLaunchFiles
    {
        internal readonly Dictionary<string, byte[]> Data = new(StringComparer.Ordinal); internal string Payload = "";
        internal Action? OnAssertPins; internal bool Disposed, PinsValid = true; internal string? FailPinPath, WrongHashPath, ThrowReadPath;
        internal CpuPmLaunchEntry? ExtraEntry; internal Func<CpuPmLaunchEntry, CpuPmLaunchEntry>? TransformEntry;
        public void PinDirectory(string path, bool privateAcl, bool protectedAcl) { if (path == FailPinPath) { throw new IOException(); } }
        public ICpuPmLaunchFilePin PinFile(string path, long maximumBytes, bool privateAcl)
        {
            if (!Data.TryGetValue(path, out byte[]? data) || data.Length > maximumBytes || path == FailPinPath) { throw new IOException(); }
            return new Pin((byte[])data.Clone(), path == WrongHashPath, path == ThrowReadPath);
        }
        public IEnumerable<CpuPmLaunchEntry> Enumerate(string path)
        {
            var values = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string key in Data.Keys.Where(k => k.StartsWith(path + '\\', StringComparison.Ordinal)))
            { string suffix = key[(path.Length + 1)..]; string[] parts = suffix.Split('\\'); values[parts[0]] = parts.Length > 1; }
            foreach (var value in values) { var entry = new CpuPmLaunchEntry(value.Key, value.Value); yield return TransformEntry?.Invoke(entry) ?? entry; }
            if (path == Payload && ExtraEntry is not null) { yield return ExtraEntry; }
        }
        public void AssertAllPins() { OnAssertPins?.Invoke(); if (!PinsValid || Disposed) { throw new IOException(); } }
        public void Dispose() => Disposed = true;
        private sealed class Pin(byte[] bytes, bool wrongHash, bool throwRead) : ICpuPmLaunchFilePin
        {
            public long Length => bytes.Length;
            public string Sha256 => wrongHash ? new('F', 64) : Hash(bytes);
            public byte[] ReadBounded(int maximumBytes) => !throwRead && bytes.Length <= maximumBytes ? (byte[])bytes.Clone() : throw new IOException();
        }
    }
    private sealed class Processes(CpuPmAuthorityPeer self, CpuPmAuthorityPeer parent) : ICpuPmLaunchProcesses
    {
        internal CpuPmAuthorityPeer Self = self, Parent = parent; internal int Ancestry = parent.ProcessId;
        internal bool ParentAlive = true, Disposed; internal Action? OnRetainParent, OnReadCurrentChild; internal CpuPmAuthenticatedChildRun? RetainedRun;
        public CpuPmAuthorityPeer ReadCurrentChild() { OnReadCurrentChild?.Invoke(); return Self; }
        public int ReadCurrentParentProcessId() => Ancestry;
        public void RetainLiveParent(CpuPmAuthorityPeer expected) { if (expected != Parent || !ParentAlive) { throw new IOException(); } OnRetainParent?.Invoke(); }
        public bool IsSameLiveParent(CpuPmAuthorityPeer expected) => ParentAlive && expected == Parent;
        public void RetainToCurrentProcessExit(CpuPmAuthenticatedChildRun run) => RetainedRun = run;
        public void Dispose() => Disposed = true;
    }
}
