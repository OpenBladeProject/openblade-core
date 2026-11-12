using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class ManagedMailboxCorrectionTests
{
    // These records are generated one at a time, and the iterator refuses to
    // advance until production validation has consumed the previous record.
    // No directory/process restart loop or collection of historical runs.
    private static void ValidateStream(WindowsNativeEpochFiles files, int count, string fault = "none",
        IReadOnlyDictionary<string, ManagedMailboxSelection>? transfers = null)
    {
        int consumed = 0; bool readCurrent = false; string? active = null;
        byte[] projection = PrivateStructuralProjection();
        ManagedMailboxRun? run = null; byte[]? binding = null; byte[]? history = null;
        IEnumerable<string> Entries()
        {
            for (int i = 1; i <= count; i++)
            {
                active = i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
                run = Run() with { Run = active };
                binding = new ManagedMailboxBuildBinding(NativeCanonical.Digest(run.Encode()), new string('5', 32),
                    new string('7', 64), NativeCanonical.Digest(projection)).Encode();
                history = ManagedMailboxRecovery.RunHistory(run.Encode(), binding, projection);
                readCurrent = false;
                yield return Path.Combine(files.Root, "mailbox-history-run-" + active + ".bin");
                Assert.True(readCurrent, "The production reader must validate each yielded record before advancing.");
                consumed++;
            }
            active = null;
            foreach (string path in Directory.EnumerateFileSystemEntries(files.Root, "mailbox-history-*")) { yield return path; }
            if (fault == "malformed") { yield return Path.Combine(files.Root, "mailbox-history-unknown.bin"); }
            if (fault == "unmatched") { yield return Path.Combine(files.Root, "mailbox-history-transfer-" + new string('f', 32) + ".bin"); }
        }
        byte[]? Read(string path)
        {
            if (active is null) { return ManagedMailboxRecovery.ReadOptional(files, path); }
            string name = Path.GetFileName(path);
            if (name == "run-" + active + ".bin") { return run!.Encode(); }
            if (name == "run-binding-" + active + ".bin")
            {
                readCurrent = true;
                return consumed == count - 1 && fault == "missing-binding" ? null : binding;
            }
            if (name == "mailbox-history-run-" + active + ".bin") { return history; }
            Assert.Equal("run-history-" + active + ".bin", name);
            return consumed == count - 1 && fault == "legacy-disagreement" ? "different legacy"u8.ToArray() : null;
        }
        ManagedMailboxHistory.ValidateEntries(files.Root, Entries(), transfers ?? new Dictionary<string, ManagedMailboxSelection>(), Read);
        Assert.Equal(count, consumed);
    }

    [Fact]
    public void StreamingBeyondFormerRunCapComposesWithAnotherPrivateRunAndNextAdmission()
    {
        using var files = Handoff();
        ValidateStream(files, 8200);
        Assert.Null(ManagedMailboxRecovery.Selected(files));
        byte[] projection = PrivateStructuralProjection();
        var run = Run();
        var binding = new ManagedMailboxBuildBinding(NativeCanonical.Digest(run.Encode()), new string('5', 32),
            new string('7', 64), NativeCanonical.Digest(projection));
        using (WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), run, binding: binding, projection: projection)) { }
        ValidateStream(files, 8200);
        Assert.Null(ManagedMailboxRecovery.Selected(files));
        var next = Run('9') with { Process = "18:101" };
        using (WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), next,
            binding: binding with { RunDigest = NativeCanonical.Digest(next.Encode()) }, projection: projection)) { }
        ValidateStream(files, 8200);
        Assert.Null(ManagedMailboxRecovery.Selected(files));
        Assert.Equal(next.Encode(), files.ReadCurrent());
        Assert.Equal(2, Directory.EnumerateFiles(files.Root, "mailbox-history-run-*.bin").Count());
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("unmatched")]
    [InlineData("missing-binding")]
    [InlineData("legacy-disagreement")]
    public void LateInvalidStreamingEvidenceStillRefusesWithoutPublication(string fault)
    {
        using var files = Handoff();
        var before = Snapshot(files.Root);
        Refused(() => ValidateStream(files, 8200, fault));
        Assert.Equal(before.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManyRunsAndRetainedTransferKeepSignedNativeAnchorsAndPinnedReader(bool substitute)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages('d');
        using var other = await Packages('b');
        using var transfer = SignedTransfer(files, original, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        epochs.PublishedForTests = path => { if (Path.GetFileName(path) == "preparation.bin") { throw new IOException("private cut"); } };
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, original.Native,
            Guid.ParseExact(selected.Value.Request, "N"), managedTransfer: transfer, storage: Storage(files)));
        epochs.PublishedForTests = null;
        ResolvePrivateHistory(files, epochs);
        for (int i = 1; i <= 24; i++)
        {
            var retained = Run() with { Run = i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture) };
            byte[] binding = new ManagedMailboxBuildBinding(NativeCanonical.Digest(retained.Encode()), original.Managed.Installation,
                original.Managed.PackageDigest, original.Managed.EnrollmentDigest).Encode();
            files.PublishCustody(Path.Combine(files.Root, "run-" + retained.Run + ".bin"), retained.Encode());
            files.PublishCustody(Path.Combine(files.Root, "run-binding-" + retained.Run + ".bin"), binding);
            ManagedMailboxRecovery.PreserveRunHistory(files, retained.Encode(), binding, original.Managed.VerificationProjection);
        }
        ValidateStream(files, 8200, transfers: new Dictionary<string, ManagedMailboxSelection> { [selected.Value.Request] = selected });
        ReadPrivatePredecessor(files, epochs);
        if (substitute)
        {
            other.Managed.Preserve(files);
            byte[] binding = new ManagedMailboxBuildBinding(NativeCanonical.Digest(selected.Run), other.Managed.Installation,
                other.Managed.PackageDigest, other.Managed.EnrollmentDigest).Encode();
            File.WriteAllBytes(Path.Combine(files.Root, "run-binding-" + Run().Run + ".bin"), binding);
            byte[] history = ManagedMailboxRecovery.RunHistory(selected.Run, binding, other.Managed.VerificationProjection);
            File.WriteAllBytes(ManagedMailboxHistory.RunPath(files, Run().Run), history);
            File.WriteAllBytes(ManagedMailboxHistory.TransferPath(files, selected), NativeCanonical.Lines(
                "OpenBlade.ManagedMailboxTransferHistory.v1", NativeCanonical.Digest(selected.Encode()), Convert.ToBase64String(history)));
        }
        var before = Snapshot(files.Root); var nativeBefore = Snapshot(epochs.Root);
        if (substitute)
        {
            var error = Assert.Throws<IOException>(() => ResolvePrivateHistory(files, epochs));
            Assert.Equal("Historical managed projection differs from native original projection.", error.Message);
        }
        else { ResolvePrivateHistory(files, epochs); ReadPrivatePredecessor(files, epochs); }
        Assert.Equal(before.Keys.Order(), Snapshot(files.Root).Keys.Order());
        Preserved(files.Root, before); Preserved(epochs.Root, nativeBefore);
    }

    private static ManagedMailboxSelection InterruptCreate(WindowsNativeEpochFiles files, SignedPair original,
        ManagedMailboxRun run, char request, int ordinal = 1)
    {
        original.Managed.Preserve(files);
        var binding = new ManagedMailboxBuildBinding(NativeCanonical.Digest(run.Encode()), original.Managed.Installation,
            original.Managed.PackageDigest, original.Managed.EnrollmentDigest);
        using var owner = WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), run, binding: binding,
            projection: original.Managed.VerificationProjection);
        string immutable = Path.Combine(files.Root, $"transfer-{ordinal:D20}.bin");
        files.PublishedForTests = path => { if (path == immutable) { throw new IOException("after durable immutable transfer"); } };
        var error = Assert.Throws<IOException>(() => new WindowsManagedMailboxTransfer(files, run.Encode(),
            new(NativeCanonical.Digest(run.Encode()), original.Managed.Installation, new string(request, 32), NativeCanonical.Digest(original.Native.Record))));
        files.PublishedForTests = null;
        Assert.Equal("after durable immutable transfer", error.Message);
        var selected = ManagedMailboxSelection.Parse(File.ReadAllBytes(immutable));
        foreach (string suffix in new[] { "selection", "request", "run", "receipt", "original-exit", "terminal", "native-proof" })
        { Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, suffix))); }
        if (ordinal == 1) { Assert.False(File.Exists(Path.Combine(files.Root, "transfer-current.bin"))); }
        return selected;
    }

    [Theory]
    [InlineData("malformed-additive")]
    [InlineData("unmatched-additive")]
    [InlineData("missing-binding")]
    [InlineData("legacy-disagreement")]
    [InlineData("malformed-handoff")]
    [InlineData("invalid-current")]
    public async Task ImmutableTransferOnlyInvalidSnapshotNeverRepairsProjections(string fault)
    {
        using var files = Handoff(); using var original = await Packages();
        var selected = InterruptCreate(files, original, Run(), '6');
        if (fault == "malformed-additive") { files.PublishCustody(Path.Combine(files.Root, "mailbox-history-unknown.bin"), "bad"u8.ToArray()); }
        if (fault == "unmatched-additive") { files.PublishCustody(Path.Combine(files.Root, "mailbox-history-transfer-" + new string('f', 32) + ".bin"), "bad"u8.ToArray()); }
        if (fault == "missing-binding") { File.Delete(Path.Combine(files.Root, "run-binding-" + Run().Run + ".bin")); }
        if (fault == "legacy-disagreement") { files.PublishCustody(Path.Combine(files.Root, "run-history-" + Run().Run + ".bin"), "bad"u8.ToArray()); }
        if (fault == "malformed-handoff") { files.PublishCustody(Path.Combine(files.Root, "handoff-" + selected.Value.Request + "-unknown.bin"), "bad"u8.ToArray()); }
        if (fault == "invalid-current") { files.PublishCustody(Path.Combine(files.Root, "transfer-current.bin"), "bad"u8.ToArray()); }
        var before = Snapshot(files.Root);
        Refused(() => ManagedMailboxRecovery.Selected(files));
        Assert.Equal(before.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, before);
        foreach (string suffix in new[] { "selection", "request", "run" })
        { Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, suffix))); }
    }

    [Fact]
    public async Task GenuineImmutableTransferOnlyInterruptionRepairsExactMetadataWithoutInventingAuthority()
    {
        using var files = Handoff(); using var original = await Packages();
        var selected = InterruptCreate(files, original, Run(), '6'); var before = Snapshot(files.Root);
        Assert.Equal(selected.Encode(), ManagedMailboxRecovery.Selected(files)!.Encode());
        Assert.Equal(selected.Encode(), File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, selected, "selection")));
        Assert.Equal(selected.Request, File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, selected, "request")));
        Assert.Equal(selected.Run, File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, selected, "run")));
        Assert.Equal(selected.Encode(), File.ReadAllBytes(Path.Combine(files.Root, "transfer-current.bin")));
        Assert.Equal(before.Count + 4, Snapshot(files.Root).Count); Preserved(files.Root, before);
        foreach (string suffix in new[] { "receipt", "original-exit", "terminal", "epoch-intent", "mapping", "native-proof" })
        { Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, suffix))); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterProjectionOrPredecessorConflictCannotRepairEarlierMissingProjection(bool terminalFault)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages();
        using var transfer = SignedTransfer(files, original, Run(), '6');
        ResolvePrivateHistory(files, epochs); var first = ManagedMailboxRecovery.Selected(files)!;
        var second = InterruptCreate(files, original, Run('9') with { Process = "18:101" }, 'a', ordinal: 2);
        File.Delete(ManagedMailboxRecovery.PathFor(files, first, "selection"));
        File.WriteAllBytes(ManagedMailboxRecovery.PathFor(files, terminalFault ? first : second, terminalFault ? "terminal" : "run"), "conflict"u8.ToArray());
        var before = Snapshot(files.Root);
        Refused(() => ManagedMailboxRecovery.Selected(files));
        Assert.Equal(before.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, before);
        Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, first, "selection")));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("race")]
    public async Task ValidatedRepairRetainsTruthfulPartialPublicationAndConflictOnFailure(string fault)
    {
        using var files = Handoff(); using var original = await Packages();
        var selected = InterruptCreate(files, original, Run(), '6'); var before = Snapshot(files.Root);
        files.PublishedForTests = path =>
        {
            if (path != ManagedMailboxRecovery.PathFor(files, selected, "selection")) { return; }
            if (fault == "io") { throw new IOException("private repair IO cut"); }
            files.PublishCustody(ManagedMailboxRecovery.PathFor(files, selected, "request"), "racing conflict"u8.ToArray());
        };
        Assert.Throws<IOException>(() => ManagedMailboxRecovery.Selected(files)); files.PublishedForTests = null;
        Preserved(files.Root, before);
        Assert.Equal(selected.Encode(), File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, selected, "selection")));
        Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "run")));
        Assert.False(File.Exists(Path.Combine(files.Root, "transfer-current.bin")));
        if (fault == "io") { Assert.Equal(selected.Encode(), ManagedMailboxRecovery.Selected(files)!.Encode()); }
        else
        {
            var partial = Snapshot(files.Root);
            Assert.Throws<IOException>(() => ManagedMailboxRecovery.Selected(files));
            Assert.Equal(partial.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, partial);
        }
        Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "receipt")));
        Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "original-exit")));
        Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "terminal")));
    }
}
