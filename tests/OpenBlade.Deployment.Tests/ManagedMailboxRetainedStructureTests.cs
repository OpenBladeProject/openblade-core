using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class ManagedMailboxCorrectionTests
{
    // Structurally typed bytes, deliberately not a signing or enrollment authority.
    private static byte[] PrivateStructuralProjection() => NativeCanonical.Lines("OpenBlade.NativeVerification.v1", "LocalDev",
        "55555555-5555-5555-5555-555555555555", "private-fixture", NativeCanonical.Digest("private key"u8.ToArray()),
        Convert.ToBase64String("private key"u8.ToArray()), new string('8', 64));

    [Fact]
    public async Task PrivateFilesystemRunHistoryBeyondFormerCapAdmitsOneNewRunAndFreshSnapshotWithoutTransfers()
    {
        using var files = Handoff(); using var original = await Packages();
        using (files.AcquireWriter()) { }
        var initial = Snapshot(files.Root);
        byte[] projection = original.Managed.VerificationProjection;
        ManagedMailboxRun Seed(int index) => Run() with
        {
            Run = index.ToString("x32", System.Globalization.CultureInfo.InvariantCulture),
            Process = "17:" + (100 + index).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        ManagedMailboxBuildBinding Binding(ManagedMailboxRun run) => new(NativeCanonical.Digest(run.Encode()),
            original.Managed.Installation, original.Managed.PackageDigest, original.Managed.EnrollmentDigest);
        using (files.AcquireWriter())
        {
            for (int i = 1; i <= 8193; i++)
            {
                var run = Seed(i); byte[] binding = Binding(run).Encode();
                File.WriteAllBytes(Path.Combine(files.Root, "run-" + run.Run + ".bin"), run.Encode());
                File.WriteAllBytes(Path.Combine(files.Root, "run-binding-" + run.Run + ".bin"), binding);
                File.WriteAllBytes(ManagedMailboxHistory.RunPath(files, run.Run), ManagedMailboxRecovery.RunHistory(run.Encode(), binding, projection));
            }
        }
        Assert.Equal(8193, Directory.EnumerateFiles(files.Root, "mailbox-history-run-*.bin").Count());
        Assert.Empty(Directory.EnumerateFiles(files.Root, "mailbox-history-transfer-*.bin"));
        Assert.Empty(Directory.EnumerateFiles(files.Root, "transfer-*.bin"));
        var next = Seed(8194); var nextBinding = Binding(next);
        using (files.AcquireWriter())
        using (WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), next, binding: nextBinding, projection: projection))
        {
            Assert.Throws<IOException>(() => files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")));
        }
        using (files.AcquireWriter()) { Assert.Null(ManagedMailboxRecovery.Selected(files)); }
        Assert.Equal(8194, Directory.EnumerateFiles(files.Root, "mailbox-history-run-*.bin").Count());
        var expectedNames = initial.Keys.ToHashSet(StringComparer.Ordinal);
        expectedNames.Add("current.bin");
        for (int i = 1; i <= 8194; i++)
        {
            var run = Seed(i); byte[] binding = Binding(run).Encode();
            string runName = "run-" + run.Run + ".bin", bindingName = "run-binding-" + run.Run + ".bin";
            string historyName = Path.GetFileName(ManagedMailboxHistory.RunPath(files, run.Run));
            expectedNames.UnionWith([runName, bindingName, historyName]);
            Assert.Equal(run.Encode(), File.ReadAllBytes(Path.Combine(files.Root, runName)));
            Assert.Equal(binding, File.ReadAllBytes(Path.Combine(files.Root, bindingName)));
            Assert.Equal(ManagedMailboxRecovery.RunHistory(run.Encode(), binding, projection), File.ReadAllBytes(Path.Combine(files.Root, historyName)));
        }
        Assert.Equal(expectedNames.Order(), Directory.EnumerateFileSystemEntries(files.Root).Select(Path.GetFileName).Order());
        Preserved(files.Root, initial);
        Assert.Equal(next.Encode(), File.ReadAllBytes(Path.Combine(files.Root, "current.bin")));
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("exit")]
    [InlineData("exit-without-receipt")]
    [InlineData("epoch-schema")]
    [InlineData("epoch-selection")]
    [InlineData("epoch-without-exit")]
    [InlineData("mapping-without-intent")]
    [InlineData("mapping")]
    [InlineData("proof-schema")]
    [InlineData("proof-selection")]
    [InlineData("proof-generation")]
    [InlineData("proof-namespace")]
    [InlineData("completed-missing-mapping")]
    [InlineData("completed-missing-intent")]
    [InlineData("original-run")]
    [InlineData("original-binding")]
    [InlineData("legacy-run")]
    [InlineData("legacy-transfer")]
    [InlineData("legacy-disagreement")]
    [InlineData("projection-schema")]
    public async Task RetainedStructuralContradictionRefusesSelectedAndRecoveryWithoutRepair(string fault)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages();
        using var transfer = SignedTransfer(files, original, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        string Side(string suffix) => ManagedMailboxRecovery.PathFor(files, selected, suffix);
        bool reserved = fault.StartsWith("epoch", StringComparison.Ordinal) || fault.StartsWith("mapping", StringComparison.Ordinal)
            || fault.StartsWith("completed", StringComparison.Ordinal) || fault is "proof-generation" or "proof-namespace";
        if (reserved)
        {
            epochs.PublishedForTests = path => { if (Path.GetFileName(path) == "preparation.bin") { throw new IOException("structural private preparation cut"); } };
            Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, original.Native,
                Guid.ParseExact(selected.Value.Request, "N"), managedTransfer: transfer, storage: Storage(files)));
            epochs.PublishedForTests = null;
        }
        if (reserved || fault.StartsWith("proof", StringComparison.Ordinal) || fault.StartsWith("legacy", StringComparison.Ordinal))
        {
            ResolvePrivateHistory(files, epochs);
            if (!fault.StartsWith("completed", StringComparison.Ordinal))
            {
                File.Delete(Side("terminal")); File.Delete(Side("terminal-intent"));
                if (!fault.StartsWith("proof", StringComparison.Ordinal)) { File.Delete(Side("native-proof")); }
            }
        }
        void Wrong(string suffix) => File.WriteAllBytes(Side(suffix), "wrong retained bytes"u8.ToArray());
        switch (fault)
        {
            case "receipt": Wrong("receipt"); break;
            case "exit": Wrong("original-exit"); break;
            case "exit-without-receipt": File.Delete(Side("receipt")); break;
            case "epoch-schema": Wrong("epoch-intent"); break;
            case "epoch-selection":
                var ef = NativeCanonical.ParseLines(File.ReadAllBytes(Side("epoch-intent")), "OpenBlade.ManagedMailboxEpochIntent.v2", 3);
                File.WriteAllBytes(Side("epoch-intent"), NativeCanonical.Lines("OpenBlade.ManagedMailboxEpochIntent.v2", new string('a', 64), ef[1])); break;
            case "epoch-without-exit": File.Delete(Side("original-exit")); break;
            case "mapping-without-intent": File.Delete(Side("epoch-intent")); break;
            case "mapping": Wrong("mapping"); break;
            case "proof-schema": Wrong("native-proof"); break;
            case "proof-selection":
            case "proof-generation":
            case "proof-namespace":
                var pf = NativeCanonical.ParseLines(File.ReadAllBytes(Side("native-proof")), "OpenBlade.ManagedNativeResolution.v2", 7);
                if (fault == "proof-selection") { pf[0] = new string('a', 64); }
                else
                {
                    var gf = NativeCanonical.ParseLines(Convert.FromBase64String(pf[5]), "OpenBlade.ManagedNativeGenerationProof.v2", 5);
                    if (fault == "proof-generation") { gf[2] = "invalid-reservation-digest"; }
                    else { gf[3] = Convert.ToBase64String(NativeCanonical.Lines("OpenBlade.ManagedNativeNamespace.v2", "verified-bootstrap", new string('a', 64), new string('b', 64))); }
                    byte[] evidence = NativeCanonical.Lines("OpenBlade.ManagedNativeGenerationProof.v2", gf);
                    pf[3] = NativeCanonical.Digest(evidence); pf[5] = Convert.ToBase64String(evidence);
                }
                File.WriteAllBytes(Side("native-proof"), NativeCanonical.Lines("OpenBlade.ManagedNativeResolution.v2", pf)); break;
            case "completed-missing-mapping":
                File.Delete(Side("mapping"));
                // Keep the checksum internally consistent: only the causal
                // completed-reservation requirement can reject this snapshot.
                var terminalFields = NativeCanonical.ParseLines(File.ReadAllBytes(Side("terminal")), "OpenBlade.ManagedMailboxTerminal.v2", 7);
                terminalFields[4] = new string('0', 64);
                byte[] candidate = NativeCanonical.Lines("OpenBlade.ManagedMailboxTerminal.v2", terminalFields);
                File.WriteAllBytes(Side("terminal"), candidate); File.WriteAllBytes(Side("terminal-intent"), candidate); break;
            case "completed-missing-intent": File.Delete(Side("epoch-intent")); break;
            case "original-run": File.WriteAllBytes(Path.Combine(files.Root, "run-" + Run().Run + ".bin"), Run('9').Encode()); break;
            case "original-binding":
                string bindingPath = Path.Combine(files.Root, "run-binding-" + Run().Run + ".bin");
                var badBinding = ManagedMailboxBuildBinding.Parse(File.ReadAllBytes(bindingPath)) with { RunDigest = new string('a', 64) };
                File.WriteAllBytes(bindingPath, badBinding.Encode()); break;
            case "legacy-run":
            case "legacy-transfer":
            case "legacy-disagreement":
                string runHistory = ManagedMailboxHistory.RunPath(files, Run().Run);
                string transferHistory = ManagedMailboxHistory.TransferPath(files, selected);
                File.Move(runHistory, Path.Combine(files.Root, "run-history-" + Run().Run + ".bin"));
                File.Move(transferHistory, Path.Combine(files.Root, "handoff-" + selected.Value.Request + "-history.bin"));
                string legacyRun = Path.Combine(files.Root, "run-history-" + Run().Run + ".bin");
                string legacyTransfer = Path.Combine(files.Root, "handoff-" + selected.Value.Request + "-history.bin");
                if (fault == "legacy-run") { File.WriteAllBytes(legacyRun, "malformed history"u8.ToArray()); }
                else if (fault == "legacy-transfer") { File.WriteAllBytes(legacyTransfer, "malformed transfer"u8.ToArray()); }
                else { File.WriteAllBytes(legacyRun, ManagedMailboxRecovery.RunHistory(selected.Run, File.ReadAllBytes(Path.Combine(files.Root, "run-binding-" + Run().Run + ".bin")), PrivateStructuralProjection())); }
                break;
            case "projection-schema":
                byte[] projection = "invalid public projection"u8.ToArray();
                string bp = Path.Combine(files.Root, "run-binding-" + Run().Run + ".bin");
                byte[] binding = (ManagedMailboxBuildBinding.Parse(File.ReadAllBytes(bp)) with { Enrollment = NativeCanonical.Digest(projection) }).Encode();
                File.WriteAllBytes(bp, binding);
                File.WriteAllBytes(ManagedMailboxHistory.RunPath(files, Run().Run), ManagedMailboxRecovery.RunHistory(selected.Run, binding, projection)); break;
        }
        // These original predecessors must be checked even without additive history.
        if (fault is "original-run" or "original-binding") { File.Delete(ManagedMailboxHistory.RunPath(files, Run().Run)); }
        RemoveTerminalRepairProjections(files, selected);
        var before = Snapshot(files.Root); var nativeBefore = Snapshot(epochs.Root);
        Refused(() => ManagedMailboxRecovery.Selected(files));
        Assert.Equal(before.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, before);
        Refused(() => ResolvePrivateHistory(files, epochs));
        Assert.Equal(before.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, before);
        Assert.Equal(nativeBefore.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, nativeBefore);
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("original-exit")]
    [InlineData("epoch-intent")]
    [InlineData("mapping")]
    [InlineData("history")]
    [InlineData("native-proof")]
    [InlineData("terminal-intent")]
    [InlineData("terminal")]
    public async Task ActualRetainedPublicationCutsRepairOnlyProjectionsBeforeAuthenticatedCompletion(string stage)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages();
        var run = Run(); original.Managed.Preserve(files);
        var binding = new ManagedMailboxBuildBinding(NativeCanonical.Digest(run.Encode()), original.Managed.Installation,
            original.Managed.PackageDigest, original.Managed.EnrollmentDigest);
        var ownerFiles = files.RetainFixtureView();
        using var owner = WindowsManagedMailboxRun.StartForTests(ownerFiles, run, binding: binding,
            projection: original.Managed.VerificationProjection);
        using var transfer = new WindowsManagedMailboxTransfer(files.RetainFixtureView(), run.Encode(),
            new(NativeCanonical.Digest(run.Encode()), original.Managed.Installation, new string('6', 32), NativeCanonical.Digest(original.Native.Record)));
        var selected = ManagedMailboxRecovery.Selected(files)!;
        string cut = ManagedMailboxRecovery.PathFor(files, selected, stage);
        void Cut(string path) { if (path == cut) { throw new IOException("actual retained publication cut: " + stage); } }
        if (stage == "receipt")
        {
            ownerFiles.PublishedForTests = Cut;
            var error = Assert.Throws<IOException>(owner.PublishQuiescent);
            Assert.Equal("actual retained publication cut: " + stage, error.Message);
            ownerFiles.PublishedForTests = null; owner.Dispose();
            Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "original-exit")));
        }
        else
        {
            owner.PublishQuiescent(); owner.Dispose();
            if (stage == "original-exit")
            {
                transfer.FilesForTests.PublishedForTests = Cut;
                var error = Assert.Throws<IOException>(transfer.ConfirmOriginalExited);
                Assert.Equal("actual retained publication cut: " + stage, error.Message);
                transfer.FilesForTests.PublishedForTests = null;
            }
            else
            {
                transfer.ConfirmOriginalExited();
                epochs.PublishedForTests = path => { if (Path.GetFileName(path) == "preparation.bin") { throw new IOException("positive native preparation cut"); } };
                Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, original.Native,
                    Guid.ParseExact(selected.Value.Request, "N"), managedTransfer: transfer, storage: Storage(files)));
                epochs.PublishedForTests = null;
                // This is a real native preparation before either managed binding record.
                Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "epoch-intent")));
                Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "mapping")));
                Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "native-proof")));
                files.PublishedForTests = Cut;
                var error = Assert.Throws<IOException>(() => ResolvePrivateHistory(files, epochs));
                Assert.Equal("actual retained publication cut: " + stage, error.Message);
                files.PublishedForTests = null;
            }
        }
        Assert.True(File.Exists(cut));
        if (stage == "native-proof")
        {
            Assert.True(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "epoch-intent")));
            Assert.True(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "mapping")));
        }
        if (stage is "epoch-intent" or "mapping")
        { Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "native-proof"))); }
        string? future = stage switch
        {
            "receipt" => "original-exit",
            "original-exit" or "history" => "epoch-intent",
            "epoch-intent" => "mapping",
            "mapping" => "native-proof",
            "native-proof" => "terminal-intent",
            "terminal-intent" => "terminal",
            _ => null
        };
        if (future is not null) { Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, future))); }
        RemoveTerminalRepairProjections(files, selected);
        var before = Snapshot(files.Root); var nativeBefore = Snapshot(epochs.Root);
        Assert.Equal(selected.Encode(), ManagedMailboxRecovery.Selected(files)!.Encode());
        AssertTerminalRepairProjections(files, selected, before);
        Assert.Equal(nativeBefore.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, nativeBefore);
        if (stage == "receipt")
        {
            var admitted = Snapshot(files.Root);
            Refused(() => ResolvePrivateHistory(files, epochs));
            Assert.Equal(admitted.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, admitted);
            transfer.ConfirmOriginalExited();
        }
        ResolvePrivateHistory(files, epochs); ManagedMailboxRecovery.ValidateTerminal(files, selected);
    }

    [Fact]
    public async Task ReferencedLegacyOnlyHistoryAdmitsStructuralRepairWithoutCurrentRunEquality()
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages();
        using var transfer = SignedTransfer(files, original, Run(), '6');
        ResolvePrivateHistory(files, epochs);
        var selected = ManagedMailboxRecovery.Selected(files)!;
        File.Move(ManagedMailboxHistory.RunPath(files, Run().Run), Path.Combine(files.Root, "run-history-" + Run().Run + ".bin"));
        File.Move(ManagedMailboxHistory.TransferPath(files, selected), Path.Combine(files.Root, "handoff-" + selected.Value.Request + "-history.bin"));
        var next = Run('9') with { Process = "18:101" };
        var binding = new ManagedMailboxBuildBinding(NativeCanonical.Digest(next.Encode()), original.Managed.Installation,
            original.Managed.PackageDigest, original.Managed.EnrollmentDigest);
        using (WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), next, binding: binding,
            projection: original.Managed.VerificationProjection)) { }
        RemoveTerminalRepairProjections(files, selected);
        var before = Snapshot(files.Root); var nativeBefore = Snapshot(epochs.Root);
        Assert.Equal(selected.Encode(), ManagedMailboxRecovery.Selected(files)!.Encode());
        AssertTerminalRepairProjections(files, selected, before);
        Assert.Equal(next.Encode(), files.ReadCurrent());
        Assert.Equal(nativeBefore.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, nativeBefore);
        ResolvePrivateHistory(files, epochs); ManagedMailboxRecovery.ValidateTerminal(files, selected);
    }
}
