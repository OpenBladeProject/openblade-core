using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class ManagedMailboxCorrectionTests
{
    [Theory]
    [InlineData("malformed-intent")]
    [InlineData("inconsistent-intent")]
    [InlineData("missing-native-proof")]
    [InlineData("missing-original-exit")]
    [InlineData("changed-reserved-mapping")]
    public async Task LatestIntentOnlyInvalidEvidenceRefusesBeforeAnyProjectionRepair(string fault)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages();
        using var transfer = SignedTransfer(files, original, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        if (fault == "changed-reserved-mapping")
        {
            epochs.PublishedForTests = path => { if (Path.GetFileName(path) == "preparation.bin") { throw new IOException("private preparation cut"); } };
            Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, original.Native,
                Guid.ParseExact(selected.Value.Request, "N"), managedTransfer: transfer, storage: Storage(files)));
            epochs.PublishedForTests = null;
        }
        ResolvePrivateHistory(files, epochs);
        string intent = ManagedMailboxRecovery.PathFor(files, selected, "terminal-intent");
        string terminal = ManagedMailboxRecovery.PathFor(files, selected, "terminal");
        Assert.Equal(File.ReadAllBytes(terminal), File.ReadAllBytes(intent));
        // Model inconsistent protected history after corruption/partial restore,
        // not malformed bytes arising from an ordinary atomic publication cut.
        File.Delete(terminal);
        if (fault == "malformed-intent") { File.WriteAllBytes(intent, "malformed retained intent"u8.ToArray()); }
        else if (fault == "inconsistent-intent")
        {
            string canonical = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(intent));
            File.WriteAllBytes(intent, System.Text.Encoding.UTF8.GetBytes(canonical.Replace(
                NativeCanonical.Digest(selected.Encode()), new string('a', 64), StringComparison.Ordinal)));
            NativeCanonical.ParseLines(File.ReadAllBytes(intent), "OpenBlade.ManagedMailboxTerminal.v2", 7);
        }
        else if (fault == "changed-reserved-mapping")
        {
            string mapping = ManagedMailboxRecovery.PathFor(files, selected, "mapping");
            byte[] changed = File.ReadAllBytes(mapping); changed[^2] = changed[^2] == (byte)'a' ? (byte)'b' : (byte)'a';
            File.WriteAllBytes(mapping, changed);
        }
        else
        {
            File.Delete(ManagedMailboxRecovery.PathFor(files, selected,
                fault == "missing-native-proof" ? "native-proof" : "original-exit"));
        }
        RemoveTerminalRepairProjections(files, selected);
        var before = Snapshot(files.Root); var nativeBefore = Snapshot(epochs.Root);
        void AssertIntentRefusal(Action action)
        {
            if (fault == "malformed-intent") { Assert.Throws<InvalidDataException>(action); }
            else { Assert.Throws<IOException>(action); }
        }
        AssertIntentRefusal(() => ManagedMailboxRecovery.Selected(files));
        Assert.Equal(before.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, before);
        AssertIntentRefusal(() => ResolvePrivateHistory(files, epochs));
        Assert.Equal(before.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, before);
        Assert.Equal(nativeBefore.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, nativeBefore);
    }

    [Fact]
    public async Task ActualReservedIntentOnlyPublicationCutRepairsMetadataThenAuthenticatedRecoveryCompletes()
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages();
        using var transfer = SignedTransfer(files, original, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        epochs.PublishedForTests = path => { if (Path.GetFileName(path) == "preparation.bin") { throw new IOException("private preparation cut"); } };
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, original.Native,
            Guid.ParseExact(selected.Value.Request, "N"), managedTransfer: transfer, storage: Storage(files)));
        epochs.PublishedForTests = null;
        string intent = ManagedMailboxRecovery.PathFor(files, selected, "terminal-intent");
        string terminal = ManagedMailboxRecovery.PathFor(files, selected, "terminal");
        files.PublishedForTests = path => { if (path == intent) { throw new IOException("actual reserved cut after terminal intent publication"); } };
        var error = Assert.Throws<IOException>(() => ResolvePrivateHistory(files, epochs));
        files.PublishedForTests = null;
        Assert.Equal("actual reserved cut after terminal intent publication", error.Message);
        Assert.True(File.Exists(intent)); Assert.False(File.Exists(terminal));
        Assert.True(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "mapping")));
        Assert.True(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "native-proof")));
        RemoveTerminalRepairProjections(files, selected);
        var before = Snapshot(files.Root); var nativeBefore = Snapshot(epochs.Root);
        Assert.Equal(selected.Encode(), ManagedMailboxRecovery.Selected(files)!.Encode());
        AssertTerminalRepairProjections(files, selected, before); Assert.False(File.Exists(terminal));
        Assert.Equal(nativeBefore.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, nativeBefore);
        ResolvePrivateHistory(files, epochs); ManagedMailboxRecovery.ValidateTerminal(files, selected);
        Assert.Equal(File.ReadAllBytes(intent), File.ReadAllBytes(terminal));
        var completed = new Dictionary<string, byte[]>(before, StringComparer.Ordinal)
        { [Path.GetRelativePath(files.Root, terminal)] = File.ReadAllBytes(intent) };
        AssertTerminalRepairProjections(files, selected, completed);
        Assert.Equal(nativeBefore.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, nativeBefore);
    }
}
