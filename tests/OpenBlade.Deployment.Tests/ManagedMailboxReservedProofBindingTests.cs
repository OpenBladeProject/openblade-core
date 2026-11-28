using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class ManagedMailboxCorrectionTests
{
    [Theory]
    [InlineData(false, "mapping")]
    [InlineData(false, "epoch-intent")]
    [InlineData(false, "both")]
    [InlineData(true, "mapping")]
    [InlineData(true, "epoch-intent")]
    [InlineData(true, "both")]
    public async Task ReservedProofOnlyMissingPredecessorsRefuseBeforeAnyRepair(bool consumed, string missing)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var packages = await Packages();
        using var transfer = SignedTransfer(files, packages, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        string Side(string suffix) => ManagedMailboxRecovery.PathFor(files, selected, suffix);
        if (consumed)
        {
            using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), packages.Native,
                Guid.ParseExact(selected.Value.Request, "N"), managedTransfer: transfer, storage: Storage(files)))
            {
                tx.MarkStartupSubmitted();
                NativeRecoveryTests.AddAttempt(epochs, tx.Record, 17, 1, false);
                string attempts = Path.Combine(epochs.Root, tx.Record.Epoch, "attempts");
                epochs.Publish(Path.Combine(attempts, "drain-17-1.bin"), NativeCanonical.Lines("OpenBlade.NativeTransportDrain.v1",
                    tx.Record.Epoch, NativeCanonical.Digest(File.ReadAllBytes(Path.Combine(attempts, "launch-17-1.bin"))), "17", "1"));
                tx.CommitAfterBoundReadiness(new string('e', 64));
            }
            using var selection = await WindowsNativeInstalledSelection.OpenAsync(epochs.RetainFixtureView(), default);
            selection!.RetireRecoverableSelection(17, 1);
        }
        else
        {
            epochs.PublishedForTests = path =>
            { if (Path.GetFileName(path) == "preparation.bin") { throw new IOException("positive private preparation cut"); } };
            Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native,
                Guid.ParseExact(selected.Value.Request, "N"), managedTransfer: transfer, storage: Storage(files)));
            epochs.PublishedForTests = null;
            Assert.False(File.Exists(Side("native-proof")));
        }
        files.PublishedForTests = path =>
        { if (path == Side("native-proof")) { throw new IOException("reserved proof-only publication cut"); } };
        var error = Assert.Throws<IOException>(() => ResolvePrivateHistory(files, epochs));
        Assert.Equal("reserved proof-only publication cut", error.Message);
        files.PublishedForTests = null;
        Assert.True(File.Exists(Side("epoch-intent"))); Assert.True(File.Exists(Side("mapping")));
        Assert.False(File.Exists(Side("terminal-intent"))); Assert.False(File.Exists(Side("terminal")));
        var proof = ManagedMailboxNativeCustody.ParseResolution(selected, null, File.ReadAllBytes(Side("native-proof")));
        Assert.NotEqual("not-reserved", proof.Epoch);
        Assert.Equal(consumed ? "consumed-and-retired" : "canceled-before-native-use", proof.Disposition);
        if (missing is "mapping" or "both") { File.Delete(Side("mapping")); }
        if (missing is "epoch-intent" or "both") { File.Delete(Side("epoch-intent")); }
        RemoveTerminalRepairProjections(files, selected);
        var before = Snapshot(files.Root); var nativeBefore = Snapshot(epochs.Root);
        void Unchanged()
        {
            Assert.Equal(before.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, before);
            Assert.Equal(nativeBefore.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, nativeBefore);
        }
        // Selected is the metadata-only path: it acquires no native custody.
        Refused(() => ManagedMailboxRecovery.Selected(files)); Unchanged();
        Refused(() => ResolvePrivateHistory(files, epochs)); Unchanged();
    }

    [Fact]
    public async Task NotReservedProofOnlyWithoutBindingsRepairsThenAuthenticates()
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var packages = await Packages();
        using var transfer = SignedTransfer(files, packages, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        string Side(string suffix) => ManagedMailboxRecovery.PathFor(files, selected, suffix);
        files.PublishedForTests = path =>
        { if (path == Side("native-proof")) { throw new IOException("unreserved proof-only publication cut"); } };
        var error = Assert.Throws<IOException>(() => ResolvePrivateHistory(files, epochs));
        Assert.Equal("unreserved proof-only publication cut", error.Message);
        files.PublishedForTests = null;
        Assert.False(File.Exists(Side("epoch-intent"))); Assert.False(File.Exists(Side("mapping")));
        Assert.False(File.Exists(Side("terminal-intent"))); Assert.False(File.Exists(Side("terminal")));
        Assert.Equal("not-reserved", ManagedMailboxNativeCustody.ParseResolution(selected, null,
            File.ReadAllBytes(Side("native-proof"))).Epoch);
        RemoveTerminalRepairProjections(files, selected);
        var before = Snapshot(files.Root); var nativeBefore = Snapshot(epochs.Root);
        Assert.Equal(selected.Encode(), ManagedMailboxRecovery.Selected(files)!.Encode());
        AssertTerminalRepairProjections(files, selected, before);
        Assert.Equal(nativeBefore.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, nativeBefore);
        ResolvePrivateHistory(files, epochs); ManagedMailboxRecovery.ValidateTerminal(files, selected);
        Assert.False(File.Exists(Side("epoch-intent"))); Assert.False(File.Exists(Side("mapping")));
    }
}
