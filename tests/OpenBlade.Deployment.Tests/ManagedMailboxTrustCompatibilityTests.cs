using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class ManagedMailboxCorrectionTests
{
    [Theory]
    [InlineData("preparation", false)]
    [InlineData("preparation", true)]
    [InlineData("terminal-resolve", false)]
    [InlineData("terminal-resolve", true)]
    [InlineData("terminal-admission", false)]
    [InlineData("terminal-admission", true)]
    public async Task ReservedNativeProjectionRemainsIndependentOfCoherentManagedHistory(string stage, bool substitute)
    {
        using var files = Handoff(); using var epochs = Bootstrap();
        using var original = await Packages('d'); using var other = await Packages('b');
        Assert.Equal(original.Managed.Installation, other.Managed.Installation);
        Assert.NotEqual(original.Managed.EnrollmentDigest, other.Managed.EnrollmentDigest);
        using var transfer = SignedTransfer(files, original, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        epochs.PublishedForTests = path => { if (Path.GetFileName(path) == "preparation.bin") { throw new IOException("private cut"); } };
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, original.Native,
            Guid.ParseExact(selected.Value.Request, "N"), managedTransfer: transfer, storage: Storage(files)));
        epochs.PublishedForTests = null;
        if (stage != "preparation") { ResolvePrivateHistory(files, epochs); }
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
        var managedBefore = Snapshot(files.Root); var nativeBefore = Snapshot(epochs.Root);
        using var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView());
        void Admit()
        {
            if (stage == "terminal-admission") { ManagedMailboxRecovery.AuthenticateRetainedTerminals(files, native); }
            else
            {
                using var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
                ManagedMailboxRecovery.Resolve(files, native);
            }
        }
        if (substitute)
        {
            var error = Assert.Throws<IOException>(Admit);
            Assert.Equal("Historical managed projection differs from native original projection.", error.Message);
            native.Dispose();
            Assert.Equal(managedBefore.Keys.Order(), Snapshot(files.Root).Keys.Order());
            Assert.Equal(nativeBefore.Keys.Order(), Snapshot(epochs.Root).Keys.Order());
        }
        else { Admit(); Assert.True(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "terminal"))); }
        native.Dispose(); Preserved(files.Root, managedBefore); Preserved(epochs.Root, nativeBefore);
    }

    internal static void ResolvePrivateHistory(WindowsNativeEpochFiles files, WindowsNativeEpochFiles epochs,
        Func<NativeLocalDevEnrollment>? enrollment = null)
    {
        using var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView());
        using var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
        ManagedMailboxRecovery.Resolve(files, native, acquireEnrollment: enrollment);
    }
    internal static void ReadPrivatePredecessor(WindowsNativeEpochFiles files, WindowsNativeEpochFiles epochs)
    {
        using var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView());
        using var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
        // The actual pinned full reader sees a complete unreserved terminal and
        // never acquires current enrollment. It executes ordinary private logic only.
        PinnedPredecessorMailboxRecovery.Resolve(files, native);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OmittedEnrollmentRefusesActiveAndTerminalPrivateOriginalBeforeProductResolver(bool terminal)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages();
        using var transfer = SignedTransfer(files, original, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        if (terminal) { ResolvePrivateHistory(files, epochs); }
        File.Delete(ManagedMailboxHistory.RunPath(files, Run().Run));
        File.Delete(ManagedMailboxHistory.TransferPath(files, selected));
        var before = Snapshot(files.Root); var nativeBefore = Snapshot(epochs.Root); int productReads = 0;
        var sentinel = Assert.Throws<InvalidOperationException>(() => WindowsManagedMailboxPackage.AcquireDefaultEnrollment(files, () =>
        { productReads++; throw new Exception("product resolver sentinel must never execute"); }));
        var omitted = Assert.Throws<InvalidOperationException>(() => ResolvePrivateHistory(files, epochs));
        Assert.Equal("Private mailbox fixtures require explicit enrollment.", omitted.Message);
        Assert.Equal(sentinel.Message, omitted.Message); Assert.Equal(0, productReads);
        Assert.Equal(before.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, before); Preserved(epochs.Root, nativeBefore);
        int explicitReads = 0;
        ResolvePrivateHistory(files, epochs, () => { explicitReads++; return NativeLocalDevEnrollment.FromProtectedProjection(original.Managed.VerificationProjection); });
        Assert.Equal(1, explicitReads); Assert.True(File.Exists(ManagedMailboxHistory.TransferPath(files, selected)));
        Assert.True(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "terminal"))); Preserved(files.Root, before);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("run")]
    [InlineData("transfer")]
    public async Task NewHistoryAdmissionRemainsReadableByActualPinnedPredecessorAfterEachPublicationCut(string cut)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages();
        using var transfer = SignedTransfer(files, original, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        File.Delete(ManagedMailboxHistory.RunPath(files, Run().Run));
        Assert.Equal(selected.Encode(), PinnedPredecessorMailboxRecovery.Selected(files)!.Encode());
        // Construct a healthy old-format terminal using the old algorithm's
        // explicit private authentication hook, then admit it with the new reader.
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")))
        {
            PinnedPredecessorMailboxRecovery.Resolve(files, native, s =>
            {
                using var cached = WindowsManagedMailboxPackage.OpenCachedHistorical(files, original.Managed.PackageDigest, original.Managed.VerificationProjection);
                Assert.Equal(original.Managed.EnrollmentDigest, cached.EnrollmentDigest);
            });
        }
        ReadPrivatePredecessor(files, epochs); var before = Snapshot(files.Root);
        string? cutPath = cut == "run" ? ManagedMailboxHistory.RunPath(files, Run().Run)
            : cut == "transfer" ? ManagedMailboxHistory.TransferPath(files, selected) : null;
        if (cutPath is not null)
        {
            files.PublishedForTests = path => { if (path == cutPath) { throw new IOException("private additive publication cut"); } };
            Assert.Throws<IOException>(() => ResolvePrivateHistory(files, epochs,
                () => NativeLocalDevEnrollment.FromProtectedProjection(original.Managed.VerificationProjection)));
            files.PublishedForTests = null;
            ReadPrivatePredecessor(files, epochs);
        }
        ResolvePrivateHistory(files, epochs, () => NativeLocalDevEnrollment.FromProtectedProjection(original.Managed.VerificationProjection));
        ReadPrivatePredecessor(files, epochs);
        Assert.False(File.Exists(Path.Combine(files.Root, "handoff-" + selected.Value.Request + "-history.bin")));
        Assert.True(File.Exists(ManagedMailboxHistory.TransferPath(files, selected))); Preserved(files.Root, before);
    }

    [Theory]
    [InlineData("matching")]
    [InlineData("legacy-only")]
    [InlineData("run-disagreement")]
    [InlineData("transfer-disagreement")]
    public async Task ExistingLegacyCandidateSidecarsStayAuthenticatedAndPreserved(string fault)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages();
        using var transfer = SignedTransfer(files, original, Run(), '6');
        ResolvePrivateHistory(files, epochs); var selected = ManagedMailboxRecovery.Selected(files)!;
        string runLegacy = Path.Combine(files.Root, "run-history-" + Run().Run + ".bin");
        string transferLegacy = Path.Combine(files.Root, "handoff-" + selected.Value.Request + "-history.bin");
        File.WriteAllBytes(runLegacy, File.ReadAllBytes(ManagedMailboxHistory.RunPath(files, Run().Run)));
        File.WriteAllBytes(transferLegacy, File.ReadAllBytes(ManagedMailboxHistory.TransferPath(files, selected)));
        if (fault == "legacy-only")
        {
            File.Delete(ManagedMailboxHistory.RunPath(files, Run().Run));
            File.Delete(ManagedMailboxHistory.TransferPath(files, selected));
        }
        // This candidate data was already incompatible; new admission never
        // renames/deletes it to manufacture a healthy predecessor.
        Assert.Throws<IOException>(() => PinnedPredecessorMailboxRecovery.Selected(files));
        if (fault == "run-disagreement") { File.WriteAllBytes(runLegacy, "different"u8.ToArray()); }
        if (fault == "transfer-disagreement") { File.WriteAllBytes(transferLegacy, "different"u8.ToArray()); }
        var before = Snapshot(files.Root);
        if (fault is "matching" or "legacy-only") { ResolvePrivateHistory(files, epochs); }
        else { Assert.Throws<IOException>(() => ResolvePrivateHistory(files, epochs)); }
        if (fault != "legacy-only") { Assert.Equal(before.Keys.Order(), Snapshot(files.Root).Keys.Order()); }
        Preserved(files.Root, before);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unmatched")]
    [InlineData("binding")]
    public async Task AdditiveNamespaceRefusesUnknownUnmatchedAndUnboundEvidence(string fault)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages();
        using var transfer = SignedTransfer(files, original, Run(), '6');
        if (fault == "unknown") { files.PublishCustody(Path.Combine(files.Root, "mailbox-history-unknown.bin"), "unknown"u8.ToArray()); }
        if (fault == "unmatched") { files.PublishCustody(Path.Combine(files.Root, "mailbox-history-transfer-" + new string('f', 32) + ".bin"), "unmatched"u8.ToArray()); }
        if (fault == "binding") { File.WriteAllBytes(ManagedMailboxHistory.RunPath(files, Run().Run), "unbound"u8.ToArray()); }
        var before = Snapshot(files.Root);
        Refused(() => ResolvePrivateHistory(files, epochs));
        Assert.Equal(before.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, before);
    }
}
