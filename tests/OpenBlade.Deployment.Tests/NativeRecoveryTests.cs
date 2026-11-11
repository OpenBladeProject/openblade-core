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
        using var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        Assert.Equal(NativeEpochPhase.Unresolved, selection!.Record.Phase);
        selection.RetireRecoverableSelection();
        Assert.Equal(NativeEpochPhase.Retired, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
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
