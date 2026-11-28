using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class ManagedMailboxCorrectionTests
{
    private static void RemoveTerminalRepairProjections(WindowsNativeEpochFiles files, ManagedMailboxSelection selected)
    {
        foreach (string suffix in new[] { "selection", "request", "run" })
        { File.Delete(ManagedMailboxRecovery.PathFor(files, selected, suffix)); }
        File.Delete(Path.Combine(files.Root, "transfer-current.bin"));
    }

    private static void AssertTerminalRepairProjections(WindowsNativeEpochFiles files, ManagedMailboxSelection selected,
        Dictionary<string, byte[]> before)
    {
        var expected = new Dictionary<string, byte[]>(before, StringComparer.Ordinal)
        {
            [Path.GetRelativePath(files.Root, ManagedMailboxRecovery.PathFor(files, selected, "selection"))] = selected.Encode(),
            [Path.GetRelativePath(files.Root, ManagedMailboxRecovery.PathFor(files, selected, "request"))] = selected.Request,
            [Path.GetRelativePath(files.Root, ManagedMailboxRecovery.PathFor(files, selected, "run"))] = selected.Run,
            ["transfer-current.bin"] = selected.Encode()
        };
        Assert.Equal(expected.Keys.Order(), Snapshot(files.Root).Keys.Order());
        Preserved(files.Root, expected);
    }

    [Theory]
    [InlineData("malformed-terminal")]
    [InlineData("missing-completed-intent")]
    [InlineData("conflicting-completed-intent")]
    public async Task LatestCompletedTerminalInvalidEvidenceRefusesBeforeAnyProjectionRepair(string fault)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages();
        using var transfer = SignedTransfer(files, original, Run(), '6');
        ResolvePrivateHistory(files, epochs);
        var selected = ManagedMailboxRecovery.Selected(files)!;
        string terminal = ManagedMailboxRecovery.PathFor(files, selected, "terminal");
        string intent = ManagedMailboxRecovery.PathFor(files, selected, "terminal-intent");
        Assert.Equal(File.ReadAllBytes(terminal), File.ReadAllBytes(intent));
        if (fault == "malformed-terminal")
        {
            File.WriteAllBytes(terminal, "malformed completed terminal"u8.ToArray());
            File.WriteAllBytes(intent, File.ReadAllBytes(terminal));
        }
        else if (fault == "missing-completed-intent") { File.Delete(intent); }
        else
        {
            byte[] conflict = File.ReadAllBytes(intent);
            conflict[^2] = conflict[^2] == (byte)'a' ? (byte)'b' : (byte)'a';
            File.WriteAllBytes(intent, conflict);
        }
        RemoveTerminalRepairProjections(files, selected);
        var before = Snapshot(files.Root); var nativeBefore = Snapshot(epochs.Root);
        void AssertTerminalRefusal(Action action)
        {
            if (fault == "malformed-terminal") { Assert.Throws<InvalidDataException>(action); }
            else { Assert.Throws<IOException>(action); }
        }
        AssertTerminalRefusal(() => ManagedMailboxRecovery.Selected(files));
        Assert.Equal(before.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, before);
        AssertTerminalRefusal(() => ResolvePrivateHistory(files, epochs));
        Assert.Equal(before.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, before);
        Assert.Equal(nativeBefore.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, nativeBefore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LatestValidCompletedTerminalRepairsOnlyExactProjectionsAndPreservesProof(bool reserved)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages();
        using var transfer = SignedTransfer(files, original, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        if (reserved)
        {
            epochs.PublishedForTests = path => { if (Path.GetFileName(path) == "preparation.bin") { throw new IOException("private preparation cut"); } };
            Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, original.Native,
                Guid.ParseExact(selected.Value.Request, "N"), managedTransfer: transfer, storage: Storage(files)));
            epochs.PublishedForTests = null;
        }
        ResolvePrivateHistory(files, epochs);
        Assert.Equal(reserved, File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "mapping")));
        RemoveTerminalRepairProjections(files, selected);
        var before = Snapshot(files.Root); var nativeBefore = Snapshot(epochs.Root);
        Assert.Equal(selected.Encode(), ManagedMailboxRecovery.Selected(files)!.Encode());
        AssertTerminalRepairProjections(files, selected, before);
        ResolvePrivateHistory(files, epochs); ReadPrivatePredecessor(files, epochs);
        AssertTerminalRepairProjections(files, selected, before);
        Assert.Equal(nativeBefore.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, nativeBefore);
    }

    [Fact]
    public async Task LatestActiveWithoutTerminalRepairsMetadataWithoutCreatingResolutionAuthority()
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages();
        using var transfer = SignedTransfer(files, original, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        RemoveTerminalRepairProjections(files, selected);
        var before = Snapshot(files.Root); var nativeBefore = Snapshot(epochs.Root);
        Assert.Equal(selected.Encode(), ManagedMailboxRecovery.Selected(files)!.Encode());
        AssertTerminalRepairProjections(files, selected, before);
        foreach (string suffix in new[] { "terminal", "terminal-intent", "native-proof", "epoch-intent", "mapping" })
        { Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, suffix))); }
        Assert.Equal(nativeBefore.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, nativeBefore);
    }

    [Fact]
    public async Task ActualLatestIntentOnlyPublicationCutRepairsMetadataThenAuthenticatedRecoveryCompletes()
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages();
        using var transfer = SignedTransfer(files, original, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        string intent = ManagedMailboxRecovery.PathFor(files, selected, "terminal-intent");
        string terminal = ManagedMailboxRecovery.PathFor(files, selected, "terminal");
        files.PublishedForTests = path => { if (path == intent) { throw new IOException("actual cut after terminal intent publication"); } };
        var error = Assert.Throws<IOException>(() => ResolvePrivateHistory(files, epochs));
        files.PublishedForTests = null;
        Assert.Equal("actual cut after terminal intent publication", error.Message);
        Assert.True(File.Exists(intent)); Assert.False(File.Exists(terminal));
        Assert.True(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "native-proof")));
        RemoveTerminalRepairProjections(files, selected);
        var before = Snapshot(files.Root); var nativeBefore = Snapshot(epochs.Root);
        Assert.Equal(selected.Encode(), ManagedMailboxRecovery.Selected(files)!.Encode());
        AssertTerminalRepairProjections(files, selected, before);
        Assert.False(File.Exists(terminal));
        Assert.Equal(nativeBefore.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, nativeBefore);
        ResolvePrivateHistory(files, epochs);
        ManagedMailboxRecovery.ValidateTerminal(files, selected);
        Assert.Equal(File.ReadAllBytes(intent), File.ReadAllBytes(terminal));
        var completed = Snapshot(files.Root);
        Assert.Equal(before.Keys.Append(Path.GetRelativePath(files.Root, terminal)).Concat(new[]
        {
            Path.GetRelativePath(files.Root, ManagedMailboxRecovery.PathFor(files, selected, "selection")),
            Path.GetRelativePath(files.Root, ManagedMailboxRecovery.PathFor(files, selected, "request")),
            Path.GetRelativePath(files.Root, ManagedMailboxRecovery.PathFor(files, selected, "run")), "transfer-current.bin"
        }).Order(), completed.Keys.Order());
        Preserved(files.Root, before);
        Assert.Equal(nativeBefore.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, nativeBefore);
    }
}
