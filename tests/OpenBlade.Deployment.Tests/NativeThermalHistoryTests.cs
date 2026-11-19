using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Deployment.Tests;

// Private-root/SID closure: the fixture runner supplies both values; a plain run uses a
// fresh own-user temp root. No product discovery, service process, pipe, firmware,
// helper or default-root supplier is executed.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NativeThermalHistoryTests : IDisposable
{
    private readonly string? ownedRoot;
    public NativeThermalHistoryTests()
    {
        string? root = Environment.GetEnvironmentVariable("OPENBLADE_HISTORY_MANAGED_FIXTURE_ROOT");
        string? sid = Environment.GetEnvironmentVariable("OPENBLADE_THERMAL_FIXTURE_SID");
        if (root is null && sid is null)
        {
            // Plain lane: the fixture creates its own protected own-user directory below this root.
            ownedRoot = root = Directory.CreateTempSubdirectory("obh").FullName;
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            sid = identity.User!.Value;
        }
        if (root is null) { throw new InvalidOperationException("Explicit fixture root required."); }
        if (sid is null) { throw new InvalidOperationException("Explicit supplied own SID required."); }
        if (!Path.IsPathFullyQualified(root)) { throw new InvalidOperationException("Absolute fixture root required."); }
        WindowsNativeEpochFiles.FixtureFactoryForTests.Value = () => WindowsNativeEpochFiles.CreateSuppliedPrivateFixture(Path.Combine(root, "OpenBlade.NativeEpochFixture-" + Guid.NewGuid().ToString("N")), sid);
    }
    public void Dispose()
    {
        WindowsNativeEpochFiles.FixtureFactoryForTests.Value = null;
        if (ownedRoot is null) { return; }
        // Fixture views a test did not dispose still pin directories until finalized.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { Directory.Delete(ownedRoot, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static byte[] Rows(string nonce, int first, int count, bool confirmed = false)
    {
        using var stream = new MemoryStream();
        for (int n = first; n < first + count; n++)
        {
            foreach (int progress in confirmed ? new[] { 0, 1, 2, 3, 4, 5 } : new[] { 0, 6 })
            {
                byte[] row = JsonSerializer.SerializeToUtf8Bytes(new { request = $"aaaaaaaa-bbbb-cccc-dddd-{n:D12}", owner = nonce, user_sid = "S-1-5-21-111-222-333-1002", caller_pid = 42, session = 7, requested = (object?)null, destination = 1, intent = NativeCanonical.Digest("null"u8), progress });
                stream.Write(row); stream.WriteByte(10);
            }
        }
        return stream.ToArray();
    }
    private static byte[] Owner(WindowsNativeEpochFiles files, NativeEpochRecord record, string nonce, ulong start = 1, string? previous = null, string process = "17:1")
    {
        byte[] active = [];
        foreach (string phase in new[] { "Reserved", "Active", "Retiring", "Retired" })
        {
            byte[] bytes = NativeCanonical.Lines("OpenBlade.NativeOwner.v1", phase, start.ToString(CultureInfo.InvariantCulture), previous ?? new string('0', 64), record.Installation, record.Epoch, record.ServiceId, process, nonce);
            files.Publish(Path.Combine(files.Root, "owner", $"owner-{start:D20}.bin"), bytes);
            if (phase == "Active") { active = bytes; }
            start++; previous = NativeCanonical.Digest(bytes);
        }
        return active;
    }
    private static void Thermal(WindowsNativeEpochFiles files, byte[] tail) => File.WriteAllBytes(Path.Combine(files.Root, "owner", "thermal-mutation.journal"), tail);
    private static void Drain(WindowsNativeEpochFiles files, NativeEpochRecord record)
    {
        NativeRecoveryTests.AddAttempt(files, record, 17, 1, false);
        byte[] launch = File.ReadAllBytes(Path.Combine(files.Root, record.Epoch, "attempts", "launch-17-1.bin"));
        files.Publish(Path.Combine(files.Root, record.Epoch, "attempts", "drain-17-1.bin"), NativeCanonical.Lines("OpenBlade.NativeTransportDrain.v1", record.Epoch, NativeCanonical.Digest(launch), "17", "1"));
    }
    private static void Roll(WindowsNativeEpochFiles files, byte[] segment, byte[] tail)
    {
        string directory = Path.Combine(files.Root, "owner", "thermal-history");
        // Controlled private fixture construction; production uses original native
        // handle-relative metadata writer, tested independently in Rust.
        Directory.CreateDirectory(directory);
        ulong rows = (ulong)segment.Count(b => b == 10);
        byte[] cp = NativeCanonical.Lines("OpenBlade.ThermalCheckpoint.v1", "1", new string('0', 64), NativeCanonical.Digest(segment), segment.Length.ToString(CultureInfo.InvariantCulture), rows.ToString(CultureInfo.InvariantCulture), WindowsNativeThermalHistory.Contract);
        byte[] empty = WindowsNativeThermalHistory.Envelope(1, NativeCanonical.Digest(cp), []);
        File.WriteAllBytes(Path.Combine(directory, WindowsNativeThermalHistory.Name("segment", 1)), segment);
        files.Publish(Path.Combine(directory, WindowsNativeThermalHistory.Name("checkpoint", 1)), cp);
        files.Publish(Path.Combine(directory, WindowsNativeThermalHistory.Name("roll", 1)), NativeCanonical.Lines("OpenBlade.ThermalRoll.v1", NativeCanonical.Digest(segment), NativeCanonical.Digest(empty), NativeCanonical.Digest(cp), WindowsNativeThermalHistory.Contract));
        Thermal(files, WindowsNativeThermalHistory.Envelope(1, NativeCanonical.Digest(cp), tail));
    }
    [Fact]
    public void CanonicalRustManagedVectorsAndMalformedRowsUseProductionReader()
    {
        using var vector = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "thermal-history-v1.json")));
        var v = vector.RootElement;
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        string[] f = NativeCanonical.ParseLines(Bytes(v.GetProperty("owner").GetString()!), "OpenBlade.NativeOwner.v1", 9);
        var original = new WindowsNativeTrustLifecycle.OwnerReference(f[3], f[4], f[5], f[6], f[7], NativeCanonical.Digest(Bytes(v.GetProperty("owner").GetString()!)));
        byte[] segment = Bytes(v.GetProperty("segment").GetString()!);
        Thermal(files, segment);
        var legacy = WindowsNativeThermalHistory.Read(files, [original]);
        var record = new NativeEpochRecord(NativeEpochPhase.Retired, f[4], new string('0', 64), 1, f[3], new string('a', 64), new string('b', 32), f[5], new string('c', 64) + "-agent", new string('0', 64), new string('d', 64));
        var service = NativeArtifact.Inspect([.. WindowsNativeEpochTests.Image("service", nonce: '3', schema: 2, mutation: true)], "service", new string('a', 40));
        var launches = new Dictionary<string, byte[]> { ["17-1.bin"] = [] };
        Assert.Equal(v.GetProperty("generation").GetString(), legacy.Generation(record, service, [original], launches));
        foreach (int destination in new[] { 1, 2, 0 })
        {
            byte[] candidate = Bytes(Encoding.UTF8.GetString(segment).Replace("\"destination\":1", "\"destination\":" + destination.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            Thermal(files, candidate);
            if (destination == 0) { Assert.ThrowsAny<IOException>(() => WindowsNativeThermalHistory.Read(files, [original])); }
            else { _ = WindowsNativeThermalHistory.Read(files, [original]); }
        }
        Thermal(files, segment);
        Roll(files, segment, []);
        var segmented = WindowsNativeThermalHistory.Read(files, [original]);
        Assert.Equal(v.GetProperty("global").GetString(), segmented.Commitment);
        Thermal(files, WindowsNativeThermalHistory.Envelope(1, NativeCanonical.Digest(Bytes(v.GetProperty("checkpoint").GetString()!)), segment));
        Assert.ThrowsAny<IOException>(() => WindowsNativeThermalHistory.Read(files, [original]));
        Thermal(files, WindowsNativeThermalHistory.Envelope(1, NativeCanonical.Digest(Bytes(v.GetProperty("checkpoint").GetString()!)), []));
        Assert.Equal(legacy.Generation(record, service, [original], launches), segmented.Generation(record, service, [original], launches));
        Assert.ThrowsAny<IOException>(() => WindowsNativeThermalHistory.Read(files, []));
        // Actual previous reader's refusal boundary is retained explicitly.
        Assert.Throws<IOException>(() => WindowsNativeTrustLifecycle.RequireNoThermalJournal("thermal-mutation.journal"));
        foreach (var bad in v.GetProperty("rejectedRows").EnumerateArray())
        {
            using var badFiles = ManagedMailboxCorrectionTests.Bootstrap(); Thermal(badFiles, Bytes(bad.GetString()!));
            Assert.ThrowsAny<Exception>(() => WindowsNativeThermalHistory.Read(badFiles, [original]));
        }
    }
    [Theory]
    [InlineData("pending")]
    [InlineData("empty")]
    [InlineData("orphan-rollover")]
    [InlineData("unsettled")]
    [InlineData("foreign")]
    [InlineData("missing-drain")]
    [InlineData("wrong-process")]
    [InlineData("missing-segment")]
    [InlineData("unknown")]
    [InlineData("producer")]
    public async Task OriginalEvidenceAndIndependentDrainAreMandatoryBeforeRetirement(string fault)
    {
        using var epochs = ManagedMailboxCorrectionTests.Bootstrap(); using var managed = ManagedMailboxCorrectionTests.Handoff();
        using var packages = await ManagedMailboxCorrectionTests.Packages(mutation: fault != "producer");
        var run = new ManagedMailboxRun(new string('1', 32), "17:100", new string('2', 64), new string('3', 64), new string('4', 32));
        using var transfer = ManagedMailboxCorrectionTests.SignedTransfer(managed, packages, run, '6');
        var storage = new ManagedMailboxStorage(() => false, () => throw new InvalidOperationException("No legacy root"), () => managed.RetainFixtureView());
        NativeEpochRecord record;
        using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), packages.Native, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: transfer, storage: storage))
        { record = tx.Record; tx.MarkStartupSubmitted(); Drain(epochs, record); tx.CommitAfterBoundReadiness(new string('e', 64)); }
        string nonce = new('4', 64); _ = Owner(epochs, record, nonce, process: fault == "wrong-process" ? "18:2" : "17:1");
        byte[] rows = Rows(fault == "foreign" ? new string('5', 64) : nonce, 1, 1, confirmed: true);
        if (fault == "unsettled") { rows = rows[..(Array.LastIndexOf(rows, (byte)10, rows.Length - 2) + 1)]; }
        Thermal(epochs, rows);
        if (fault == "empty") { Thermal(epochs, []); }
        if (fault == "orphan-rollover") { File.WriteAllBytes(Path.Combine(epochs.Root, "owner", "thermal-mutation.journal.rollover"), "partial metadata"u8.ToArray()); }
        if (fault == "pending") { epochs.Publish(Path.Combine(epochs.Root, "owner", "thermal-mutation.journal.pending"), rows); }
        if (fault == "missing-drain") { File.Delete(Path.Combine(epochs.Root, record.Epoch, "attempts", "drain-17-1.bin")); }
        if (fault == "missing-segment") { Roll(epochs, rows, []); File.Delete(Path.Combine(epochs.Root, "owner", "thermal-history", WindowsNativeThermalHistory.Name("segment", 1))); }
        if (fault == "unknown") { epochs.Publish(Path.Combine(epochs.Root, "owner", "unknown.bin"), "unknown"u8.ToArray()); }
        byte[] before = File.ReadAllBytes(Path.Combine(epochs.Root, "owner", "thermal-mutation.journal"));
        using var selection = await WindowsNativeInstalledSelection.OpenAsync(epochs.RetainFixtureView(), default);
        Assert.ThrowsAny<IOException>(() => selection.RetireRecoverableSelection(17, 1));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(epochs.Root, "owner", "thermal-mutation.journal")));
        Assert.False(File.Exists(Path.Combine(epochs.Root, record.Epoch, "Retired.bin")));
    }
    [Fact]
    public async Task PositiveManagedNativeManagedGrowthKeepsOldGenerationProofAndCompatibleRollbackFresh()
    {
        using var epochs = ManagedMailboxCorrectionTests.Bootstrap(); using var managed = ManagedMailboxCorrectionTests.Handoff();
        using var packages = await ManagedMailboxCorrectionTests.Packages(mutation: true);
        var storage = new ManagedMailboxStorage(() => false, () => throw new InvalidOperationException("No legacy root"), () => managed.RetainFixtureView());
        var run = new ManagedMailboxRun(new string('1', 32), "17:100", new string('2', 64), new string('3', 64), new string('4', 32));
        using var firstTransfer = ManagedMailboxCorrectionTests.SignedTransfer(managed, packages, run, '6');
        NativeEpochRecord first;
        using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), packages.Native, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: firstTransfer, storage: storage))
        { first = tx.Record; tx.MarkStartupSubmitted(); Drain(epochs, first); tx.CommitAfterBoundReadiness(new string('e', 64)); }
        string nonce = new('4', 64); _ = Owner(epochs, first, nonce);
        byte[] segment = [.. Rows(nonce, 0, 999), .. Rows(nonce, 999, 1, confirmed: true)]; Assert.InRange(segment.Length, 1, WindowsNativeThermalHistory.SegmentBytes);
        Thermal(epochs, segment);
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(epochs.RetainFixtureView(), default)) { selection.RetireRecoverableSelection(17, 1); first = selection.Record; }
        Assert.ThrowsAny<IOException>(() => PinnedPredecessorThermalTrustLifecycle.RequireOwnerRetired(epochs));
        var firstManaged = ManagedMailboxRecovery.Selected(managed)!;
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView())) { ManagedMailboxRecovery.Resolve(managed, native, _ => { }); }
        string[] originalProof;
        using (var owner = WindowsNativeTrustLifecycle.AcquireRetiredOwner(epochs))
        { originalProof = ManagedMailboxNativeCustody.ParseRetirement(WindowsNativeAttemptHistory.RequireDrained(epochs, first, Path.Combine(epochs.Root, first.Epoch), packages.Native.Service, owner)); }
        Assert.Equal(8, originalProof.Length);
        byte[] oldTerminal = File.ReadAllBytes(ManagedMailboxRecovery.PathFor(managed, firstManaged, "native-proof"));
        using var secondTransfer = ManagedMailboxCorrectionTests.SignedTransfer(managed, packages, run with { Run = new string('9', 32), Process = "18:101" }, 'a');
        NativeEpochRecord second;
        using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), packages.Native, Guid.ParseExact(new string('a', 32), "N"), managedTransfer: secondTransfer, storage: storage))
        { second = tx.Record; tx.MarkStartupSubmitted(); Drain(epochs, second); tx.CommitAfterBoundReadiness(new string('e', 64)); }
        string predecessor = NativeCanonical.Digest(File.ReadAllBytes(Path.Combine(epochs.Root, "owner", "owner-00000000000000000004.bin")));
        _ = Owner(epochs, second, new string('5', 64), 5, predecessor);
        // Keep both per-file bounds while total retained history crosses 1 MiB.
        byte[] tail = Rows(new string('5', 64), 2000, 1000);
        Assert.InRange(tail.Length, 1, WindowsNativeThermalHistory.SegmentBytes); Assert.True(segment.Length + tail.Length > WindowsNativeThermalHistory.SegmentBytes);
        Roll(epochs, segment, tail);
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(epochs.RetainFixtureView(), default)) { selection.RetireRecoverableSelection(17, 1); }
        using (var owner = WindowsNativeTrustLifecycle.AcquireRetiredOwner(epochs))
        {
            var fresh = ManagedMailboxNativeCustody.ParseRetirement(WindowsNativeAttemptHistory.RequireDrained(epochs, first, Path.Combine(epochs.Root, first.Epoch), packages.Native.Service, owner));
            Assert.Equal(originalProof[7], fresh[7]); Assert.NotEqual(originalProof[6], fresh[6]);
        }
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        { native.RequirePreservedResolution(firstManaged, first, oldTerminal, "consumed-and-retired"); ManagedMailboxRecovery.Resolve(managed, native, _ => { }); }
        Assert.Equal(oldTerminal, File.ReadAllBytes(ManagedMailboxRecovery.PathFor(managed, firstManaged, "native-proof")));
        // Same authenticated package, fresh compatible reader, new rollback epoch.
        using var rollback = ManagedMailboxCorrectionTests.SignedTransfer(managed, packages, run with { Run = new string('c', 32), Process = "19:102" }, 'c');
        using var next = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), packages.Native, Guid.ParseExact(new string('c', 32), "N"), managedTransfer: rollback, storage: storage);
        Assert.Equal(3, next.Record.Ordinal); Assert.NotEqual(first.Epoch, next.Record.Epoch);
    }
    [Fact]
    public async Task GenuinePredecessorV2NoThermalTerminalRequiresFreshEmptyGenerationAndNeverAttestsLaterRows()
    {
        using var epochs = ManagedMailboxCorrectionTests.Bootstrap(); using var managed = ManagedMailboxCorrectionTests.Handoff();
        using var packages = await ManagedMailboxCorrectionTests.Packages(mutation: true);
        var storage = new ManagedMailboxStorage(() => false, () => throw new InvalidOperationException("No legacy root"), () => managed.RetainFixtureView());
        var run = new ManagedMailboxRun(new string('1', 32), "17:100", new string('2', 64), new string('3', 64), new string('4', 32));
        using var transfer = ManagedMailboxCorrectionTests.SignedTransfer(managed, packages, run, '6');
        NativeEpochRecord record;
        using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), packages.Native, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: transfer, storage: storage))
        { record = tx.Record; tx.MarkStartupSubmitted(); Drain(epochs, record); tx.CommitAfterBoundReadiness(new string('e', 64)); }
        string nonce = new('4', 64); _ = Owner(epochs, record, nonce);
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(epochs.RetainFixtureView(), default)) { selection.RetireRecoverableSelection(17, 1); record = selection.Record; }
        byte[] predecessor;
        using (var oldOwner = PinnedPredecessorThermalTrustLifecycle.AcquireRetiredOwner(epochs))
        { predecessor = PinnedPredecessorThermalAttemptHistory.RequireDrained(epochs, record, Path.Combine(epochs.Root, record.Epoch), packages.Native.Service, oldOwner.Commitment); }
        Assert.Equal(7, ManagedMailboxNativeCustody.ParseRetirement(predecessor).Length);
        var selected = ManagedMailboxRecovery.Selected(managed)!;
        byte[] legacyResolution;
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        {
            var outer = NativeCanonical.ParseLines(native.Resolve(selected, record).Proof, "OpenBlade.ManagedNativeResolution.v2", 7);
            var generation = NativeCanonical.ParseLines(Convert.FromBase64String(outer[5]), "OpenBlade.ManagedNativeGenerationProof.v2", 5);
            var space = NativeCanonical.ParseLines(Convert.FromBase64String(generation[3]), "OpenBlade.ManagedNativeNamespace.v2", 4);
            // Exact predecessor single-generation namespace commitment uses its
            // actual v2 proof, not the current fresh v3 proof's content address.
            byte[] oldSpace = NativeCanonical.Lines("OpenBlade.ManagedNativeNamespace.v2", space[0], space[1], NativeCanonical.Digest(Bytes(NativeCanonical.Digest(predecessor))));
            byte[] evidence = NativeCanonical.Lines("OpenBlade.ManagedNativeGenerationProof.v2", generation[0], Convert.ToBase64String(predecessor), generation[2], Convert.ToBase64String(oldSpace));
            legacyResolution = NativeCanonical.Lines("OpenBlade.ManagedNativeResolution.v2", outer[0], outer[1], outer[2], NativeCanonical.Digest(evidence), outer[4], Convert.ToBase64String(evidence));
            managed.Publish(ManagedMailboxRecovery.PathFor(managed, selected, "native-proof"), legacyResolution);
            using var exclusion = managed.AcquireServiceCustody(Path.Combine(managed.Root, "exclusion.lock"));
            PinnedPredecessorMailboxRecovery.Resolve(managed, native, _ => { });
        }
        byte[] terminal = File.ReadAllBytes(ManagedMailboxRecovery.PathFor(managed, selected, "terminal"));
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        { ManagedMailboxRecovery.Resolve(managed, native, _ => { }); }
        Assert.Equal(legacyResolution, File.ReadAllBytes(ManagedMailboxRecovery.PathFor(managed, selected, "native-proof")));
        Assert.Equal(terminal, File.ReadAllBytes(ManagedMailboxRecovery.PathFor(managed, selected, "terminal")));
        Thermal(epochs, Rows(nonce, 1, 1, confirmed: true));
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        { Assert.ThrowsAny<IOException>(() => ManagedMailboxRecovery.Resolve(managed, native, _ => { })); }
        Assert.Equal(legacyResolution, File.ReadAllBytes(ManagedMailboxRecovery.PathFor(managed, selected, "native-proof")));
        Assert.Equal(terminal, File.ReadAllBytes(ManagedMailboxRecovery.PathFor(managed, selected, "terminal")));
    }


    [Fact]
    public void FanV2CanonicalVectorRequiresModeProofAndOwnedAutomaticHandback()
    {
        using var v = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fan-history-v2.json")));
        var vector = v.RootElement;
        Assert.Equal(vector.GetProperty("canonical").GetString(), Encoding.UTF8.GetString(WindowsNativeFanHistory.Canonical(vector.GetProperty("fan"))));
        Assert.Equal(vector.GetProperty("intent").GetString(), NativeCanonical.Digest(WindowsNativeFanHistory.Canonical(vector.GetProperty("fan"))));
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        var original = new WindowsNativeTrustLifecycle.OwnerReference(new string('1', 32), new string('2', 32), new string('3', 64) + "-service", "17:1", new string('7', 64), new string('8', 64));
        Thermal(files, Bytes(vector.GetProperty("owned").GetString()!));
        Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [original]));
        Thermal(files, Bytes(vector.GetProperty("released").GetString()!));
        var history = WindowsNativeThermalHistory.Read(files, [original]);
        var record = new NativeEpochRecord(NativeEpochPhase.Retired, original.Epoch, new string('0', 64), 1, original.Installation, new string('a', 64), new string('b', 32), original.Service, new string('c', 64) + "-agent", new string('0', 64), new string('d', 64));
        var old = NativeArtifact.Inspect([.. WindowsNativeEpochTests.Image("service", nonce: '3', schema: 2, mutation: true)], "service", new string('a', 40));
        var compatible = NativeArtifact.Inspect([.. WindowsNativeEpochTests.Image("service", nonce: '3', schema: 2, fan: true)], "service", new string('a', 40));
        var launches = new Dictionary<string, byte[]> { ["17-1.bin"] = [] };
        Assert.Throws<IOException>(() => history.Generation(record, old, [original], launches));
        Assert.NotEmpty(history.Generation(record, compatible, [original], launches));
        Assert.Equal("service-fan-mutation-v2", compatible.StartupPolicy);
        // A later Prepared-only retired request cannot erase the old owner's
        // obligation; only its exact retained release may resume afterward.
        string owned = vector.GetProperty("owned").GetString()!;
        using var first = JsonDocument.Parse(owned.Split('\n')[0]);
        var next = System.Text.Json.Nodes.JsonNode.Parse(first.RootElement.GetRawText())!;
        next["request"] = "aaaaaaaa-bbbb-cccc-dddd-000000000002";
        next["progress"] = 0;
        string prepared = owned + next.ToJsonString() + "\n";
        next["progress"] = 6;
        string retired = prepared + next.ToJsonString() + "\n";
        Thermal(files, Bytes(retired)); Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [original]));
        string release = string.Join('\n', vector.GetProperty("released").GetString()!.Split('\n').Skip(10));
        Thermal(files, Bytes(retired + release)); _ = WindowsNativeThermalHistory.Read(files, [original]);
        Thermal(files, Bytes(prepared + release)); Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [original]));

        string raw = vector.GetProperty("released").GetString()!;
        foreach (string invalid in new[] { raw.Replace("linked-v1", "linked-v9", StringComparison.Ordinal), raw.Replace("\"progress\":50", "\"progress\":30", StringComparison.Ordinal), raw.Replace("\"rpm\":3700", "\"rpm\":3701", StringComparison.Ordinal) })
        {
            Thermal(files, Bytes(invalid)); Assert.ThrowsAny<Exception>(() => WindowsNativeThermalHistory.Read(files, [original]));
        }
    }
    [Theory]
    [InlineData("sameScope", true)]
    [InlineData("foreignSid", false)]
    [InlineData("foreignSession", false)]
    public void CompleteFanCallerScopeHistoryMustPassBothPublicReadAndRetiredOwner(string key, bool valid)
    {
        using var vector = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fan-history-v2.json")));
        byte[] raw = Bytes(vector.RootElement.GetProperty(key).GetString()!);
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        var record = new NativeEpochRecord(NativeEpochPhase.Retired, new string('2', 32), new string('0', 64), 1, new string('1', 32), new string('a', 64), new string('b', 32), new string('3', 64) + "-service", new string('c', 64) + "-agent", new string('0', 64), new string('d', 64));
        byte[] active = Owner(files, record, new string('7', 64));
        var original = new WindowsNativeTrustLifecycle.OwnerReference(record.Installation, record.Epoch, record.ServiceId, "17:1", new string('7', 64), NativeCanonical.Digest(active));
        Thermal(files, raw);
        if (valid)
        {
            _ = WindowsNativeThermalHistory.Read(files, [original]);
            using var retired = WindowsNativeTrustLifecycle.AcquireRetiredOwner(files);
        }
        else
        {
            Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [original]));
            Assert.Throws<NativeRecoveryException>(() => WindowsNativeTrustLifecycle.AcquireRetiredOwner(files));
        }
        Assert.Equal(raw, File.ReadAllBytes(Path.Combine(files.Root, "owner", "thermal-mutation.journal")));
    }
    [Fact]
    public void FanV3ObservedAutomaticProofPreservesLegacyBytesAndExactOwner()
    {
        using var vector = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fan-history-v3.json")));
        var v = vector.RootElement;
        Assert.Equal(v.GetProperty("canonical").GetString(), Encoding.UTF8.GetString(WindowsNativeFanHistory.Canonical(v.GetProperty("fan"))));
        Assert.Equal(v.GetProperty("intent").GetString(), NativeCanonical.Digest(WindowsNativeFanHistory.Canonical(v.GetProperty("fan"))));
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        var original = new WindowsNativeTrustLifecycle.OwnerReference(new string('1', 32), new string('2', 32), new string('3', 64) + "-service", "17:1", new string('7', 64), new string('8', 64));
        foreach (string key in new[] { "released", "observedAutomatic", "retiredThenObservedAutomatic" })
        {
            string raw = v.GetProperty(key).GetString()!;
            Thermal(files, Bytes(raw)); _ = WindowsNativeThermalHistory.Read(files, [original]);
            var rows = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var last = System.Text.Json.Nodes.JsonNode.Parse(rows[^1])!;
            last["caller_pid"] = 99;
            rows[^1] = last.ToJsonString();
            Thermal(files, Bytes(string.Join('\n', rows) + "\n"));
            Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [original]));
        }
        using var legacy = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fan-history-v2.json")));
        Assert.False(WindowsNativeFanHistory.ValidProgress(legacy.RootElement.GetProperty("fan"), 51));
        Assert.True(WindowsNativeFanHistory.ValidProgress(v.GetProperty("fan"), 51));
        Assert.False(WindowsNativeFanHistory.Follows(v.GetProperty("fan"), 31, 51));
    }

    public static IEnumerable<object[]> RetainedFanCallerScopeCases()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fan-caller-scope-chains.json")));
        foreach (var item in vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return [item.GetProperty("name").GetString()!, item.GetProperty("valid").GetBoolean()];
        }
    }
    [Theory]
    [MemberData(nameof(RetainedFanCallerScopeCases))]
    public void RetainedFanCallerScopeSurvivesRetiredRequestChains(string name, bool valid)
    {
        using var vectors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fan-caller-scope-chains.json")));
        var item = vectors.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        byte[] raw = Bytes(item.GetProperty("journal").GetString()!);
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        var record = new NativeEpochRecord(NativeEpochPhase.Retired, new string('2', 32), new string('0', 64), 1, new string('1', 32), new string('a', 64), new string('b', 32), new string('3', 64) + "-service", new string('c', 64) + "-agent", new string('0', 64), new string('d', 64));
        byte[] active = Owner(files, record, new string('7', 64));
        var original = new WindowsNativeTrustLifecycle.OwnerReference(record.Installation, record.Epoch, record.ServiceId, "17:1", new string('7', 64), NativeCanonical.Digest(active));
        Thermal(files, raw);
        if (valid)
        {
            var history = WindowsNativeThermalHistory.Read(files, [original]);
            using var retired = WindowsNativeTrustLifecycle.AcquireRetiredOwner(files);
            Assert.Equal(history.Commitment, retired.Thermal.Commitment);
        }
        else
        {
            Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [original]));
            Assert.Equal(raw, File.ReadAllBytes(Path.Combine(files.Root, "owner", "thermal-mutation.journal")));
            Assert.Throws<NativeRecoveryException>(() => WindowsNativeTrustLifecycle.AcquireRetiredOwner(files));
        }
        Assert.Equal(raw, File.ReadAllBytes(Path.Combine(files.Root, "owner", "thermal-mutation.journal")));
    }


    [Fact]
    public void BatteryV1CanonicalRowsRequireOriginalSignedFamilyAndPreserveFanHandback()
    {
        using var v = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "battery-history-v1.json")));
        var vector = v.RootElement;
        Assert.Equal(vector.GetProperty("canonical").GetString(), Encoding.UTF8.GetString(WindowsNativeBatteryHistory.Canonical(vector.GetProperty("battery"))));
        Assert.Equal(vector.GetProperty("intent").GetString(), NativeCanonical.Digest(WindowsNativeBatteryHistory.Canonical(vector.GetProperty("battery"))));
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        var original = new WindowsNativeTrustLifecycle.OwnerReference(new string('1', 32), new string('2', 32), new string('3', 64) + "-service", "17:1", new string('7', 64), new string('8', 64));
        var record = new NativeEpochRecord(NativeEpochPhase.Retired, original.Epoch, new string('0', 64), 1, original.Installation, new string('a', 64), new string('b', 32), original.Service, new string('c', 64) + "-agent", new string('0', 64), new string('d', 64));
        var old = NativeArtifact.Inspect([.. WindowsNativeEpochTests.Image("service", nonce: '3', schema: 2, fan: true)], "service", new string('a', 40));
        var producer = NativeArtifact.Inspect([.. WindowsNativeEpochTests.Image("service", nonce: '3', schema: 2, battery: true)], "service", new string('a', 40));
        Assert.Equal("service-battery-policy-v1", producer.StartupPolicy);
        var launches = new Dictionary<string, byte[]> { { "17-1.bin", [] } };
        foreach (string key in new[] { "standalone", "released" })
        {
            Thermal(files, Bytes(vector.GetProperty(key).GetString()!)); var history = WindowsNativeThermalHistory.Read(files, [original]);
            Assert.Throws<IOException>(() => history.Generation(record, old, [original], launches)); Assert.NotEmpty(history.Generation(record, producer, [original], launches));
        }
        foreach (string key in new[] { "interleaved", "legacyRejected" }) { Thermal(files, Bytes(vector.GetProperty(key).GetString()!)); Assert.ThrowsAny<IOException>(() => WindowsNativeThermalHistory.Read(files, [original])); }
        // The invalid byte has a matching canonical payload digest, so rejection
        // must enforce the byte range rather than merely detect a stale hash.
        string oversized = vector.GetProperty("oversizedBaselineRejected").GetString()!;
        using (var row = JsonDocument.Parse(oversized.Split('\n')[0]))
        {
            Assert.Equal(454, row.RootElement.GetProperty("battery").GetProperty("baseline").GetInt32());
            Assert.Equal(row.RootElement.GetProperty("intent").GetString(), NativeCanonical.Digest(Bytes(row.RootElement.GetProperty("battery").GetRawText())));
        }
        Thermal(files, Bytes(oversized));
        Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [original]));
        string released = vector.GetProperty("released").GetString()!;
        foreach (string bad in new[] { released.Replace("\"caller_pid\":44", "\"caller_pid\":0", StringComparison.Ordinal), released.Replace("\"progress\":62", "\"progress\":5", StringComparison.Ordinal), released.Replace("\"target\":198", "\"target\":-0", StringComparison.Ordinal) }) { Thermal(files, Bytes(bad)); Assert.ThrowsAny<Exception>(() => WindowsNativeThermalHistory.Read(files, [original])); }
    }
    public static IEnumerable<object[]> BatteryCorrectionCases()
    {
        using var v = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "battery-history-v1.json")));
        foreach (var item in v.RootElement.GetProperty("corrections").EnumerateArray())
        {
            foreach (bool rolled in new[] { false, true }) { yield return [item.GetProperty("name").GetString()!, item.GetProperty("valid").GetBoolean(), rolled]; }
        }
    }
    [Theory]
    [MemberData(nameof(BatteryCorrectionCases))]
    public void BatteryCorrectionCompleteRehashedVectorsUseOriginalProof(string name, bool valid, bool rolled)
    {
        using var v = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "battery-history-v1.json")));
        var item = v.RootElement.GetProperty("corrections").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        byte[] raw = Bytes(item.GetProperty("journal").GetString()!);
        Assert.Equal(item.GetProperty("sha256").GetString(), NativeCanonical.Digest(raw));
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        var record = new NativeEpochRecord(NativeEpochPhase.Retired, new string('2', 32), new string('0', 64), 1, new string('1', 32), new string('a', 64), new string('b', 32), new string('3', 64) + "-service", new string('c', 64) + "-agent", new string('0', 64), new string('d', 64));
        byte[] active = Owner(files, record, new string('7', 64));
        var original = new WindowsNativeTrustLifecycle.OwnerReference(record.Installation, record.Epoch, record.ServiceId, "17:1", new string('7', 64), NativeCanonical.Digest(active));
        var producer = NativeArtifact.Inspect([.. WindowsNativeEpochTests.Image("service", nonce: '3', schema: 2, battery: true)], "service", new string('a', 40));
        var launches = new Dictionary<string, byte[]> { { "17-1.bin", [] } };
        if (rolled) { Roll(files, Bytes(item.GetProperty("archivedJournal").GetString()!), []); } else { Thermal(files, raw); }
        var before = Directory.EnumerateFiles(Path.Combine(files.Root, "owner"), "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        if (valid)
        {
            var history = WindowsNativeThermalHistory.Read(files, [original]);
            Assert.NotEmpty(history.Generation(record, producer, [original], launches));
            using var retired = WindowsNativeTrustLifecycle.AcquireRetiredOwner(files);
            Assert.Equal(history.Commitment, retired.Thermal.Commitment);
        }
        else
        {
            Assert.Throws<IOException>(() => WindowsNativeThermalHistory.Read(files, [original]));
            Assert.Throws<NativeRecoveryException>(() => WindowsNativeTrustLifecycle.AcquireRetiredOwner(files));
        }
        foreach (var pair in before) { Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)); }
    }

    [Fact]
    public void TemporaryChargeV1RehashedHistoriesRequireOwnProducerAndPreserveOriginalCreator()
    {
        using var v = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "temporary-charge-history-v1.json")));
        var vector = v.RootElement;
        Assert.Equal(vector.GetProperty("canonical").GetString(), Encoding.UTF8.GetString(WindowsNativeTemporaryChargeHistory.Canonical(vector.GetProperty("tfc"))));
        Assert.Equal(vector.GetProperty("intent").GetString(), NativeCanonical.Digest(WindowsNativeTemporaryChargeHistory.Canonical(vector.GetProperty("tfc"))));
        using var files = ManagedMailboxCorrectionTests.Bootstrap();
        var original = new WindowsNativeTrustLifecycle.OwnerReference(new string('1', 32), new string('2', 32), new string('3', 64) + "-service", "17:1", new string('7', 64), new string('8', 64));
        var epoch = new NativeEpochRecord(NativeEpochPhase.Retired, original.Epoch, new string('0', 64), 1, original.Installation, new string('a', 64), new string('b', 32), original.Service, new string('c', 64) + "-agent", new string('0', 64), new string('d', 64));
        var old = NativeArtifact.Inspect([.. WindowsNativeEpochTests.Image("service", nonce: '3', schema: 2, battery: true)], "service", new string('a', 40));
        var producer = NativeArtifact.Inspect([.. WindowsNativeEpochTests.Image("service", nonce: '3', schema: 2, temporary: true)], "service", new string('a', 40));
        Assert.Equal("service-temporary-charge-v1", producer.StartupPolicy);
        var agent = NativeArtifact.Inspect([.. WindowsNativeEpochTests.Image("agent", nonce: '3', schema: 2, temporary: true)], "agent", new string('a', 40));
        Assert.False(agent.TemporaryChargeMutation); Assert.Equal("none", agent.Capabilities);
        var launches = new Dictionary<string, byte[]> { { "17-1.bin", [] } };
        foreach (var item in vector.GetProperty("cases").EnumerateArray())
        {
            byte[] raw = Bytes(item.GetProperty("journal").GetString()!); Thermal(files, raw);
            if (item.GetProperty("valid").GetBoolean())
            {
                var history = WindowsNativeThermalHistory.Read(files, [original]);
                Assert.Throws<IOException>(() => history.Generation(epoch, old, [original], launches));
                Assert.NotEmpty(history.Generation(epoch, producer, [original], launches));
                // Re-check after the compatible proof: no cached broadening.
                Assert.Throws<IOException>(() => history.Generation(epoch, old, [original], launches));
            }
            else { Assert.ThrowsAny<Exception>(() => WindowsNativeThermalHistory.Read(files, [original])); }
            Assert.Equal(raw, File.ReadAllBytes(Path.Combine(files.Root, "owner", "thermal-mutation.journal")));
        }
    }
}
