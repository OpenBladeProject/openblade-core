using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NativeRecoveryTests
{
    [Fact]
    public async Task VerifiedBootstrapReachesNewEpochWithoutResettingHistory()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        Directory.CreateDirectory(Path.Combine(files.Root, "owner"));
        if (!File.Exists(Path.Combine(files.Root, "owner", "writer.lock"))) { File.WriteAllBytes(Path.Combine(files.Root, "owner", "writer.lock"), []); }
        files.Publish(Path.Combine(files.Root, "root.bin"), NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent"));
        Assert.Null(await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None));
        using var package = await WindowsNativeEpochTests.Package(files);
        using var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid());
        Assert.Equal(1, tx.Record.Ordinal);
    }

    [Fact]
    public async Task FreshBeginRechecksBootstrapAfterAnotherWriterLeftAReservation()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        Directory.CreateDirectory(Path.Combine(files.Root, "owner"));
        if (!File.Exists(Path.Combine(files.Root, "owner", "writer.lock"))) { File.WriteAllBytes(Path.Combine(files.Root, "owner", "writer.lock"), []); }
        files.Publish(Path.Combine(files.Root, "root.bin"), NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent"));
        Assert.Null(await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None));
        byte[] reservation = "preserved"u8.ToArray();
        File.WriteAllBytes(Path.Combine(files.Root, "intervening.reserved"), reservation);
        using var package = await WindowsNativeEpochTests.Package(files);
        Assert.Equal(NativeRecoveryReason.UnknownBootstrapHistory,
            Assert.Throws<NativeRecoveryException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid())).Reason);
        Assert.Equal(reservation, File.ReadAllBytes(Path.Combine(files.Root, "intervening.reserved")));
        Assert.Null(files.ReadCurrent());
    }

    [Theory]
    [InlineData("abandoned.reserved")]
    [InlineData("package.bin")]
    [InlineData("unknown-owner")]
    public async Task NullCurrentWithPreservedHistoryIsNeverCleanBootstrap(string name)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        Directory.CreateDirectory(Path.Combine(files.Root, "owner"));
        if (!File.Exists(Path.Combine(files.Root, "owner", "writer.lock"))) { File.WriteAllBytes(Path.Combine(files.Root, "owner", "writer.lock"), []); }
        files.Publish(Path.Combine(files.Root, "root.bin"), NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent"));
        byte[] bytes = "preserve-exactly"u8.ToArray();
        string path = Path.Combine(files.Root, name == "unknown-owner" ? "owner/unknown.bin" : name);
        File.WriteAllBytes(path, bytes);
        await Assert.ThrowsAnyAsync<IOException>(() => WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Null(files.ReadCurrent());
    }

    [Theory]
    [InlineData((int)NativeEpochPhase.Provisional, false)]
    [InlineData((int)NativeEpochPhase.Unresolved, false)]
    [InlineData((int)NativeEpochPhase.Committed, true)]
    [InlineData((int)NativeEpochPhase.Unresolved, true)]
    public async Task ExactStoppedAndInterruptedAttemptsRetireThenUseFreshEpoch(int phase, bool submitted)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        string epoch;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            epoch = tx.Record.Epoch;
            if (submitted) { tx.MarkStartupSubmitted(); AddAttempt(files, tx.Record, 17, 1, true); }
            if (phase == (int)NativeEpochPhase.Committed) { tx.CommitAfterBoundReadiness(new string('e', 64)); }
            if (phase == (int)NativeEpochPhase.Unresolved) { tx.MarkUnresolved(); }
        }
        Directory.CreateDirectory(Path.Combine(files.Root, "owner"));
        if (!File.Exists(Path.Combine(files.Root, "owner", "writer.lock"))) { File.WriteAllBytes(Path.Combine(files.Root, "owner", "writer.lock"), []); }
        using (var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None))
        {
            selection!.RetireRecoverableSelection();
            Assert.Equal(NativeEpochPhase.Retired, selection.Record.Phase);
        }
        using var replacement = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid());
        Assert.NotEqual(epoch, replacement.Record.Epoch);
        Assert.Equal(2, replacement.Record.Ordinal);
        Assert.True(File.Exists(Path.Combine(files.Root, epoch + ".reserved")));
    }

    [Fact]
    public async Task ExactFrontierPublicationIsRecoveredBeforeStoppedAttemptRetirement()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid(), testCrashBoundary: b =>
        { if (b == NativeEpochBoundary.FrontierWritten) { throw new IOException("crash"); } }));
        Assert.Null(files.ReadCurrent());
        Directory.CreateDirectory(Path.Combine(files.Root, "owner"));
        if (!File.Exists(Path.Combine(files.Root, "owner", "writer.lock"))) { File.WriteAllBytes(Path.Combine(files.Root, "owner", "writer.lock"), []); }
        using var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        Assert.Equal(NativeEpochPhase.Unresolved, selection!.Record.Phase);
        selection.RetireRecoverableSelection();
        Assert.Equal(NativeEpochPhase.Retired, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
    }

    [Theory]
    [InlineData((int)NativeEpochBoundary.PhaseWritten)]
    [InlineData((int)NativeEpochBoundary.FrontierWritten)]
    [InlineData((int)NativeEpochBoundary.SelectionPublished)]
    public async Task InterruptedCommitKeepsReadinessContradictionAcrossUnresolvedRecovery(int boundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        string epoch;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            epoch = tx.Record.Epoch;
            tx.MarkStartupSubmitted();
            // Injected metadata readiness contradicts absent actual launch evidence.
            Assert.Throws<IOException>(() => tx.CommitAfterBoundReadiness(new string('e', 64), b =>
            { if ((int)b == boundary) { throw new IOException("commit publication crash"); } }));
        }
        using var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        byte[] original = File.ReadAllBytes(Path.Combine(files.Root, epoch, "Committed.bin"));
        Assert.Equal(NativeRecoveryReason.UnknownAttemptHistory,
            Assert.Throws<NativeRecoveryException>(() => selection!.RetireRecoverableSelection()).Reason);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(files.Root, epoch, "Committed.bin")));
        Assert.NotEqual(NativeEpochPhase.Retired, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
    }

    [Theory]
    [InlineData("missing-drain")]
    [InlineData("later-restart")]
    [InlineData("wrong-process")]
    [InlineData("wrong-package")]
    [InlineData("wrong-request")]
    [InlineData("wrong-epoch")]
    [InlineData("wrong-drain")]
    [InlineData("partial")]
    [InlineData("unknown")]
    [InlineData("firmware-owner")]
    [InlineData("active-custody")]
    public async Task AmbiguousAttemptHistoryNeverRetiresOrReplays(string fault)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        NativeEpochRecord record;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        { tx.MarkStartupSubmitted(); record = tx.Record; AddAttempt(files, record, 17, 1, fault != "missing-drain"); tx.CommitAfterBoundReadiness(new string('e', 64)); }
        Directory.CreateDirectory(Path.Combine(files.Root, "owner"));
        if (!File.Exists(Path.Combine(files.Root, "owner", "writer.lock"))) { File.WriteAllBytes(Path.Combine(files.Root, "owner", "writer.lock"), []); }
        string attempts = Path.Combine(files.Root, record.Epoch, "attempts");
        if (fault is "wrong-package" or "wrong-request" or "wrong-epoch")
        {
            string launchPath = Path.Combine(attempts, "launch-17-1.bin");
            byte[] original = File.ReadAllBytes(launchPath);
            string[] f = NativeCanonical.ParseLines(original, "OpenBlade.NativeServiceLaunch.v1", 11);
            int index = fault == "wrong-package" ? 3 : fault == "wrong-request" ? 2 : 0;
            f[index] = new string('f', f[index].Length);
            File.WriteAllBytes(launchPath, NativeCanonical.Lines("OpenBlade.NativeServiceLaunch.v1", f));
        }
        if (fault == "wrong-drain") { File.WriteAllBytes(Path.Combine(attempts, "drain-17-1.bin"), NativeCanonical.Lines("OpenBlade.NativeStatusDrain.v2", record.Epoch, new string('e', 64), "99", "9")); }
        if (fault == "later-restart") { AddAttempt(files, record, 18, 2, false); }
        if (fault == "partial") { File.WriteAllBytes(Path.Combine(attempts, "launch-19-3.bin"), "partial"u8.ToArray()); }
        if (fault == "unknown") { File.WriteAllBytes(Path.Combine(attempts, "unknown.bin"), []); }
        if (fault == "firmware-owner") { File.WriteAllBytes(Path.Combine(files.Root, "owner", "unknown.bin"), "active-or-unknown"u8.ToArray()); }
        byte[] selected = files.ReadCurrent()!;
        using var selection = await WindowsNativeInstalledSelection.OpenAsync(files, CancellationToken.None);
        using var custody = fault == "active-custody" ? files.AcquireServiceCustody(Path.Combine(attempts, "custody.lock")) : null;
        Assert.ThrowsAny<IOException>(() => selection.RetireRecoverableSelection(fault == "wrong-process" ? 99u : null, fault == "wrong-process" ? 9L : null));
        Assert.Equal(selected, files.ReadCurrent());
        Assert.True(File.Exists(Path.Combine(attempts, "launch-17-1.bin")));
    }

    [Theory]
    [InlineData("proof")]
    [InlineData("phase-change")]
    [InlineData("Retired.bin")]
    [InlineData("frontier")]
    [InlineData("current.bin")]
    public async Task RetirementPublicationCrashRetainsSnapshotsAndRecomputesCurrentHistory(string boundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        NativeEpochRecord record;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            tx.MarkStartupSubmitted(); AddAttempt(files, tx.Record, 17, 1, true);
            tx.CommitAfterBoundReadiness(new string('e', 64)); record = tx.Record;
        }
        string directory = Path.Combine(files.Root, record.Epoch);
        bool crashed = false;
        files.PublishedForTests = path =>
        {
            if (!crashed && (boundary is "proof" or "phase-change" && Path.GetDirectoryName(path) == Path.Combine(directory, "retirement-proofs")
                || boundary == "frontier" && Path.GetFileName(path) == $"selection-{record.Ordinal:D20}-3.bin"
                || Path.GetFileName(path) == boundary))
            { crashed = true; throw new IOException("injected publication crash"); }
        };
        using (var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None))
        {
            Assert.Equal(NativeRecoveryReason.RetirementPublicationFailed,
                Assert.Throws<NativeRecoveryException>(() => selection!.RetireRecoverableSelection()).Reason);
        }
        Assert.True(crashed);
        files.PublishedForTests = null;
        var first = Directory.GetFiles(Path.Combine(directory, "retirement-proofs")).ToDictionary(p => p, File.ReadAllBytes);
        if (boundary == "proof")
        {
            // Still Committed: one more legitimate admitted launch/drain changes
            // the snapshot without rewriting or deleting the original proof.
            Assert.Equal(NativeEpochPhase.Committed, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
            AddAttempt(files, record, 18, 2, true);
        }
        if (boundary == "phase-change")
        {
            WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, NativeCanonical.Digest(files.ReadCurrent()!));
            Assert.Equal(NativeEpochPhase.Unresolved, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
        }
        using var recovered = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        recovered!.RetireRecoverableSelection();
        Assert.Equal(NativeEpochPhase.Retired, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
        foreach (var (path, bytes) in first) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
        if (boundary is "proof" or "phase-change") { Assert.Equal(2, Directory.GetFiles(Path.Combine(directory, "retirement-proofs")).Length); }
        Assert.Equal(1, record.Ordinal);
    }

    [Theory]
    [InlineData("attempts", (int)NativeRecoveryReason.UnknownAttemptHistory)]
    [InlineData("custody-content", (int)NativeRecoveryReason.ActiveOrUnknownCustody)]
    [InlineData("custody-missing", (int)NativeRecoveryReason.ActiveOrUnknownCustody)]
    [InlineData("malformed-launch", (int)NativeRecoveryReason.UnknownAttemptHistory)]
    [InlineData("snapshot", (int)NativeRecoveryReason.RetirementPublicationFailed)]
    [InlineData("intent", (int)NativeRecoveryReason.RetirementPublicationFailed)]
    public async Task RecoveryBoundaryRetainsTypedProvenanceAndReleasesCustody(string fault, int reason)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        NativeEpochRecord record;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        { tx.MarkStartupSubmitted(); record = tx.Record; AddAttempt(files, record, 17, 1, true); }
        string directory = Path.Combine(files.Root, record.Epoch);
        string attempts = Path.Combine(directory, "attempts");
        string custody = Path.Combine(attempts, "custody.lock");
        if (fault == "attempts") { Directory.Move(attempts, attempts + "-preserved"); }
        if (fault == "custody-content") { File.WriteAllBytes(custody, "malformed"u8.ToArray()); }
        if (fault == "custody-missing") { File.Move(custody, custody + ".preserved"); }
        if (fault == "malformed-launch") { File.WriteAllBytes(Path.Combine(attempts, "launch-17-1.bin"), "malformed"u8.ToArray()); }
        if (fault == "snapshot") { files.CreateDirectoryOnce(Path.Combine(directory, "retirement-proofs")); files.Publish(Path.Combine(directory, "retirement-proofs", "unknown.bin"), "malformed"u8.ToArray()); }
        if (fault == "intent") { files.Publish(Path.Combine(directory, "Retired.bin"), "conflicting"u8.ToArray()); }
        var evidence = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        byte[] current = files.ReadCurrent()!;
        string[] frontier = Directory.GetFiles(files.Root, "selection-*.bin");
        using var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        var refusal = Assert.Throws<NativeRecoveryException>(() => selection!.RetireRecoverableSelection());
        Assert.Equal((NativeRecoveryReason)reason, refusal.Reason);
        Assert.NotNull(refusal.InnerException);
        Assert.Equal(current, files.ReadCurrent());
        Assert.Equal(frontier, Directory.GetFiles(files.Root, "selection-*.bin"));
        foreach (var (path, bytes) in evidence) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
        if (File.Exists(custody)) { using var released = files.AcquireServiceCustody(custody); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullyDrainedFinalBudgetAttemptCanRetireAndOversizedHistoryIsPreserved(bool oversized)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        NativeEpochRecord record;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        { tx.MarkStartupSubmitted(); record = tx.Record; }
        // Use private data writes for the large history; retirement reads and
        // authenticates every record through the actual production boundary.
        for (uint i = 1; i <= WindowsNativeAttemptHistory.MaximumAttempts; i++) { WriteAttemptData(files, record, i, true); }
        using var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        string extra = Path.Combine(files.Root, record.Epoch, "attempts", "launch-4097-4097.bin");
        if (oversized)
        {
            File.WriteAllBytes(extra, "over-limit-preserved"u8.ToArray());
            Assert.Equal(NativeRecoveryReason.AttemptHistoryOverLimit,
                Assert.Throws<NativeRecoveryException>(() => selection!.RetireRecoverableSelection()).Reason);
            Assert.Equal("over-limit-preserved"u8.ToArray(), File.ReadAllBytes(extra));
            Assert.Equal(8194, Directory.GetFiles(Path.Combine(files.Root, record.Epoch, "attempts")).Length);
            return;
        }
        selection!.RetireRecoverableSelection();
        Assert.Equal(NativeEpochPhase.Retired, selection.Record.Phase);
        Assert.Equal(8193, Directory.GetFiles(Path.Combine(files.Root, record.Epoch, "attempts")).Length);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("changed")]
    [InlineData("malformed")]
    public async Task OldSnapshotCannotCertifyChangedOrIncompleteHistory(string fault)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        NativeEpochRecord record;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        { tx.MarkStartupSubmitted(); AddAttempt(files, tx.Record, 17, 1, true); tx.CommitAfterBoundReadiness(new string('e', 64)); record = tx.Record; }
        files.PublishedForTests = path => { if (Path.GetFileName(Path.GetDirectoryName(path)) == "retirement-proofs") { throw new IOException("proof crash"); } };
        using (var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None))
        { Assert.Throws<NativeRecoveryException>(() => selection!.RetireRecoverableSelection()); }
        files.PublishedForTests = null;
        string directory = Path.Combine(files.Root, record.Epoch);
        string proof = Directory.GetFiles(Path.Combine(directory, "retirement-proofs")).Single();
        byte[] preserved = File.ReadAllBytes(proof);
        if (fault == "incomplete") { AddAttempt(files, record, 18, 2, false); }
        if (fault == "changed") { File.WriteAllBytes(Path.Combine(directory, "attempts", "drain-17-1.bin"), "changed"u8.ToArray()); }
        if (fault == "malformed") { files.Publish(Path.Combine(directory, "attempts", "unknown.bin"), "malformed"u8.ToArray()); }
        byte[] selected = files.ReadCurrent()!;
        using var recovery = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        Assert.Throws<NativeRecoveryException>(() => recovery!.RetireRecoverableSelection());
        Assert.Equal(selected, files.ReadCurrent());
        Assert.Equal(preserved, File.ReadAllBytes(proof));
        Assert.False(File.Exists(Path.Combine(directory, "Retired.bin")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialUnpublishedSnapshotIsPreservedAndCancellationIsNotReclassified(bool cancel)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        NativeEpochRecord record;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid())) { record = tx.Record; }
        string snapshots = Path.Combine(files.Root, record.Epoch, "retirement-proofs");
        files.CreateDirectoryOnce(snapshots);
        string partial = Path.Combine(snapshots, "receipt-" + new string('a', 32) + ".tmp");
        File.WriteAllBytes(partial, "partial"u8.ToArray());
        using var recovery = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        files.PublishedForTests = path => { if (cancel) { throw new OperationCanceledException("injected cancellation"); } };
        if (cancel)
        {
            byte[] selected = files.ReadCurrent()!;
            Assert.Throws<OperationCanceledException>(() => recovery!.RetireRecoverableSelection());
            Assert.Equal(selected, files.ReadCurrent());
            using var custody = files.AcquireServiceCustody(Path.Combine(files.Root, record.Epoch, "attempts", "custody.lock"));
        }
        else { recovery!.RetireRecoverableSelection(); Assert.Equal(NativeEpochPhase.Retired, recovery.Record.Phase); }
        Assert.Equal("partial"u8.ToArray(), File.ReadAllBytes(partial));
    }

    private static void WriteAttemptData(WindowsNativeEpochFiles files, NativeEpochRecord record, uint pid, bool drain)
    {
        string directory = Path.Combine(files.Root, record.Epoch);
        byte[] origin = File.ReadAllBytes(Path.Combine(directory, "origin.bin"));
        byte[] launch = NativeCanonical.Lines("OpenBlade.NativeServiceLaunch.v1", record.Epoch, NativeCanonical.Digest(origin), record.Request,
            record.Package, record.ServiceId, pid.ToString(), pid.ToString(), "0", "LocalSystem-primary-session0",
            NativeStartupOwner.EncodePath(Path.Combine(directory, "app", "OpenBlade.Service.exe")));
        File.WriteAllBytes(Path.Combine(directory, "attempts", $"launch-{pid}-{pid}.bin"), launch);
        if (drain) { File.WriteAllBytes(Path.Combine(directory, "attempts", $"drain-{pid}-{pid}.bin"), NativeCanonical.Lines("OpenBlade.NativeStatusDrain.v2", record.Epoch, NativeCanonical.Digest(launch), pid.ToString(), pid.ToString())); }
    }

    internal static void AddAttempt(WindowsNativeEpochFiles files, NativeEpochRecord record, uint pid, long created, bool drain)
    {
        string directory = Path.Combine(files.Root, record.Epoch);
        byte[] origin = File.ReadAllBytes(Path.Combine(directory, "origin.bin"));
        byte[] launch = NativeCanonical.Lines("OpenBlade.NativeServiceLaunch.v1", record.Epoch, NativeCanonical.Digest(origin), record.Request,
            record.Package, record.ServiceId, pid.ToString(), created.ToString(), "0", "LocalSystem-primary-session0",
            NativeStartupOwner.EncodePath(Path.Combine(directory, "app", "OpenBlade.Service.exe")));
        files.Publish(Path.Combine(directory, "attempts", $"launch-{pid}-{created}.bin"), launch);
        if (drain) { files.Publish(Path.Combine(directory, "attempts", $"drain-{pid}-{created}.bin"), NativeCanonical.Lines("OpenBlade.NativeStatusDrain.v2", record.Epoch, NativeCanonical.Digest(launch), pid.ToString(), created.ToString())); }
    }
}
