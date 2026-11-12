using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ManagedMailboxCorrectionTests
{
    private static WindowsNativeEpochFiles Bootstrap()
    {
        var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        files.Publish(Path.Combine(files.Root, "root.bin"), NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent"));
        files.EnsureOwnerStore();
        return files;
    }
    private static WindowsNativeEpochFiles Handoff()
    {
        var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        files.PublishCustody(Path.Combine(files.Root, "exclusion.lock"), NativeCanonical.Lines("OpenBlade.MailboxExclusion.v1"));
        return files;
    }
    private static ManagedMailboxRun Run(char value = '1') => new(new string(value, 32), "17:100", new string('2', 64), new string('3', 64), new string('4', 32));
    private static ManagedMailboxRequest Request(ManagedMailboxRun run, char id = '6') => new(NativeCanonical.Digest(run.Encode()), new string('5', 32), new string(id, 32), new string('7', 64));
    private static ManagedMailboxSelection Quiescent(WindowsNativeEpochFiles files, ManagedMailboxRun run, char id = '6')
    {
        using var owner = WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), run);
        using var transfer = new WindowsManagedMailboxTransfer(files.RetainFixtureView(), run.Encode(), Request(run, id));
        owner.PublishQuiescent(); owner.Dispose(); transfer.ConfirmOriginalExited();
        return ManagedMailboxRecovery.Selected(files)!;
    }
    [Fact]
    public void CanonicalV2SelectionReceiptOriginalAndMappingMatchTheSharedRecordVector()
    {
        using var files = Handoff();
        var selected = Quiescent(files, Run());
        using var vector = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "managed-mailbox-handoff-v2.json")));
        void Match(string key, byte[] bytes) => Assert.Equal(vector.RootElement.GetProperty(key).GetString(), System.Text.Encoding.ASCII.GetString(bytes));
        foreach (var rejected in vector.RootElement.GetProperty("rejectedTransfers").EnumerateArray())
        {
            Assert.Throws<InvalidDataException>(() => ManagedMailboxSelection.Parse(System.Text.Encoding.ASCII.GetBytes(rejected.GetProperty("selection").GetString()!)));
        }
        Match("selection", selected.Encode()); Match("receipt", ManagedMailboxRecovery.Receipt(selected)); Match("originalExit", ManagedMailboxRecovery.OriginalExit(selected));
        var request = selected.Value;
        Match("mapping", ManagedMailboxRecovery.Mapping(selected, new(NativeEpochPhase.Provisional, new string('e', 32), new string('0', 64), 1,
            request.Installation, request.Package, request.Request, new string('a', 64) + "-service", new string('b', 64) + "-agent", new string('0', 64), new string('f', 64))));
    }
    [Fact]
    public void CancellationTerminalReplayPreservesOldBytesAndRequiresANewRunRequestAndReceipt()
    {
        using var files = Handoff(); using var epochs = Bootstrap();
        var first = Quiescent(files, Run());
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var custody = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")))
        { ManagedMailboxRecovery.Resolve(files, native, _ => { }); }
        byte[] terminal = File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, first, "terminal"));
        byte[] receipt = File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, first, "receipt"));
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var custody = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")))
        { ManagedMailboxRecovery.Resolve(files, native, _ => throw new Exception("established terminal does not reacquire removed enrollment")); }
        Assert.Equal(terminal, File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, first, "terminal")));
        var second = Quiescent(files, Run('9') with { Process = "18:101" }, 'a');
        Assert.Equal(2, second.Ordinal);
        Assert.Equal(NativeCanonical.Digest(terminal), second.PreviousTerminal);
        Assert.NotEqual(first.Value.Request, second.Value.Request);
        Assert.NotEqual(receipt, File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, second, "receipt")));
        Assert.Equal(receipt, File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, first, "receipt")));
    }
    private static WindowsManagedMailboxTransfer Transfer(WindowsNativeEpochFiles files, WindowsNativePackage package, ManagedMailboxRun run, char request)
    {
        string installation = package.Release.Manifest.InstallationId.ToString("N");
        using var owner = WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), run, installation);
        var transfer = new WindowsManagedMailboxTransfer(files.RetainFixtureView(), run.Encode(),
            new(NativeCanonical.Digest(run.Encode()), installation, new string(request, 32), NativeCanonical.Digest(package.Record)));
        owner.PublishQuiescent(); owner.Dispose(); transfer.ConfirmOriginalExited(); return transfer;
    }
    private sealed record SignedPair(WindowsNativePackage Native, WindowsManagedMailboxPackage Managed) : IDisposable
    { public void Dispose() { Managed.Dispose(); Native.Dispose(); } }
    private static async Task<SignedPair> Packages(char nonce = 'b')
    {
        byte[] nativeBytes = WindowsNativeInventoryBurnExtractorTests.NativeEpochBundle(WindowsNativeEpochTests.Image("service", nonce, schema: 2, mailbox: true), WindowsNativeEpochTests.Image("agent", nonce, schema: 2, mailbox: true));
        using var signed = OpenBladeSignedReleaseVerifierTests.TestRelease.CreateWithInstallerBytes(nativeBytes);
        var nativeFiles = WindowsNativeEpochFiles.CreatePrivateFixture();
        File.WriteAllBytes(Path.Combine(nativeFiles.Root, "package.exe"), nativeBytes);
        var native = await WindowsNativePackage.OpenLocalDevAsync(nativeFiles.OpenFile(Path.Combine(nativeFiles.Root, "package.exe")), nativeFiles,
            signed.ManifestBytes, signed.SignatureBytes, NativeLocalDevEnrollment.ForAlgorithmTests(new Guid("11111111-1111-1111-1111-111111111111"), signed.Policy), default);
        byte[] managedBytes = WindowsNativeInventoryBurnExtractorTests.ManagedMailboxBundle();
        using var managedSigned = signed.SignSiblingInstaller(managedBytes);
        var managedFiles = WindowsNativeEpochFiles.CreatePrivateFixture();
        File.WriteAllBytes(Path.Combine(managedFiles.Root, "package.exe"), managedBytes);
        var managed = await WindowsManagedMailboxPackage.OpenLocalDevAsync(managedFiles.OpenFile(Path.Combine(managedFiles.Root, "package.exe")), managedFiles,
            managedSigned.ManifestBytes, managedSigned.SignatureBytes, NativeLocalDevEnrollment.FromProtectedProjection(native.VerificationProjection), default);
        return new(native, managed);
    }
    private static WindowsManagedMailboxTransfer SignedTransfer(WindowsNativeEpochFiles files, SignedPair packages, ManagedMailboxRun run, char request)
    {
        packages.Managed.Preserve(files);
        var binding = new ManagedMailboxBuildBinding(NativeCanonical.Digest(run.Encode()), packages.Managed.Installation, packages.Managed.PackageDigest, packages.Managed.EnrollmentDigest);
        using var owner = WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), run, binding: binding);
        var transfer = new WindowsManagedMailboxTransfer(files.RetainFixtureView(), run.Encode(), new(NativeCanonical.Digest(run.Encode()), packages.Managed.Installation, new string(request, 32), NativeCanonical.Digest(packages.Native.Record)));
        owner.PublishQuiescent(); owner.Dispose(); transfer.ConfirmOriginalExited(); return transfer;
    }
    private static Dictionary<string, byte[]> Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(root, p), File.ReadAllBytes, StringComparer.Ordinal);
    private static void Preserved(string root, Dictionary<string, byte[]> before)
    { foreach (var (path, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, path))); } }

    [Theory]
    [InlineData("epoch-intent", false)]
    [InlineData("mapping", false)]
    [InlineData("origin", false)]
    [InlineData("preparation", false)]
    [InlineData("epoch-intent", true)]
    public async Task CanceledPreparationRenewsThroughFreshStartupMaintenanceSetupSelectionAndActualBegin(string cut, bool leadingCancellation)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var packages = await Packages();
        if (leadingCancellation)
        {
            using var unreserved = SignedTransfer(files, packages, Run('d') with { Process = "20:103" }, 'd');
            using var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView());
            using var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
            ManagedMailboxRecovery.Resolve(files, native, _ =>
            { using var signed = WindowsManagedMailboxPackage.OpenCachedHistorical(files, packages.Managed.PackageDigest, packages.Native.VerificationProjection); });
        }
        using var transfer = SignedTransfer(files, packages, Run(), '6');
        var first = ManagedMailboxRecovery.Selected(files)!;
        transfer.FilesForTests.PublishedForTests = path =>
        { if (path == ManagedMailboxRecovery.PathFor(files, first, cut)) { throw new IOException("exact private publication cut"); } };
        epochs.PublishedForTests = path => { if ((cut == "origin" && Path.GetFileName(path) == "origin.bin" || cut == "preparation" && Path.GetFileName(path) == "preparation.bin")) { throw new IOException("origin-to-intent cut"); } };
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(first.Value.Request, "N"), managedTransfer: transfer));
        epochs.PublishedForTests = null; transfer.FilesForTests.PublishedForTests = null;
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")))
        { ManagedMailboxRecovery.Resolve(files, native); }
        var oldNative = Snapshot(epochs.Root); var oldTransfer = Snapshot(files.Root);
        // Actual production startup composition, with only storage/process data injected.
        using (var startup = ManagedMailboxStartup.Open(() => ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()), _ => null,
            _ => null, native => WindowsManagedMailboxRun.AcquireUnenrolledExclusion(files.RetainFixtureView(), native)))
        {
            // Setup's pre-stop selection must authenticate terminal history while
            // the new managed startup STILL owns exclusion; no self-reacquisition.
            Assert.Null(await WindowsNativeInstalledSelection.OpenForSetupAsync(epochs.RetainFixtureView(), default, () => files.RetainFixtureView()));
        }
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var maintenance = WindowsNativeTrustLifecycle.AcquireManagedEnrollmentChangeCustody(files.RetainFixtureView(), native)) { native.RequireSafe(); }
        using var second = SignedTransfer(files, packages, Run('9') with { Process = "18:101" }, 'a');
        var active = ManagedMailboxRecovery.Selected(files)!;
        Assert.Equal(leadingCancellation ? 3 : 2, active.Ordinal);
        // A fresh startup may cancel the second not-yet-reserved transfer and
        // authenticate the first prefix, without forgetting either original.
        using (var startup = ManagedMailboxStartup.Open(() => ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()), _ => null,
            _ => null, native => WindowsManagedMailboxRun.AcquireUnenrolledExclusion(files.RetainFixtureView(), native, _ =>
            { using var signed = WindowsManagedMailboxPackage.OpenCachedHistorical(files, packages.Managed.PackageDigest, packages.Native.VerificationProjection); }))) { }
        using var third = SignedTransfer(files, packages, Run('b') with { Process = "19:102" }, 'c');
        using var tx = WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(new string('c', 32), "N"), managedTransfer: third);
        Assert.Equal(NativeEpochPhase.Provisional, tx.Record.Phase); Assert.Equal(1, tx.Record.Ordinal);
        Assert.False(File.Exists(Path.Combine(epochs.Root, tx.Record.Epoch, "submission.bin")));
        Assert.All(oldNative.Keys.Where(p => Path.GetFileName(p) == "origin.bin"), path => Assert.False(File.Exists(Path.Combine(epochs.Root, Path.GetDirectoryName(path)!, "Retired.bin"))));
        tx.Dispose();
        Preserved(epochs.Root, oldNative);
        Preserved(files.Root, oldTransfer.Where(p => p.Key != "current.bin" && p.Key != "transfer-current.bin").ToDictionary());
    }

    [Theory]
    [InlineData("cache")]
    [InlineData("binding")]
    [InlineData("exit")]
    public async Task PreparationEnvelopeNeverRepairsPublicationBeforeOriginalAuthentication(string fault)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var packages = await Packages();
        using var transfer = SignedTransfer(files, packages, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        epochs.PublishedForTests = path => { if (Path.GetFileName(path) == "preparation.bin") { throw new IOException("positive atomic preparation cut"); } };
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(selected.Value.Request, "N"), managedTransfer: transfer));
        epochs.PublishedForTests = null;
        string directory = Directory.EnumerateDirectories(epochs.Root).Single(p => Path.GetFileName(p) != "owner");
        if (fault == "cache") { File.WriteAllBytes(Path.Combine(files.Root, "build-" + packages.Managed.PackageDigest, "release.sig"), new byte[64]); }
        else { File.Delete(fault == "binding" ? Path.Combine(files.Root, "run-binding-" + Run().Run + ".bin") : ManagedMailboxRecovery.PathFor(files, selected, "original-exit")); }
        var beforeNative = Snapshot(epochs.Root); var beforeManaged = Snapshot(files.Root);
        Refused(() =>
        {
            using var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView());
            using var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
            ManagedMailboxRecovery.Resolve(files, native);
        });
        Preserved(epochs.Root, beforeNative); Preserved(files.Root, beforeManaged);
        Assert.Equal(beforeNative.Keys.Order(), Snapshot(epochs.Root).Keys.Order());
        Assert.Equal(beforeManaged.Keys.Order(), Snapshot(files.Root).Keys.Order());
        Assert.False(File.Exists(Path.Combine(directory, "origin.bin")));
        Assert.Null(ManagedMailboxRecovery.ReadOptional(files, ManagedMailboxRecovery.PathFor(files, selected, "epoch-intent")));
        Assert.Null(ManagedMailboxRecovery.ReadOptional(files, ManagedMailboxRecovery.PathFor(files, selected, "mapping")));
    }

    private static void Refused(Action action)
    {
        var error = Record.Exception(action);
        Assert.NotNull(error);
        Assert.True(error is IOException or InvalidDataException or OpenBladeReleaseVerificationException or System.Security.Cryptography.CryptographicException, error.ToString());
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("original-exit")]
    [InlineData("epoch-intent")]
    [InlineData("mapping")]
    [InlineData("native-proof")]
    [InlineData("terminal-intent")]
    [InlineData("run-binding")]
    [InlineData("signed-cache")]
    [InlineData("native-origin")]
    [InlineData("preparation-envelope")]
    [InlineData("unknown-reservation")]
    [InlineData("submission")]
    [InlineData("attempt")]
    [InlineData("frontier")]
    [InlineData("active")]
    [InlineData("custody")]
    public async Task HistoricalPreparationTamperOrPossibleUseCannotRenewOrPublishAnotherEpoch(string fault)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var packages = await Packages();
        using var transfer = SignedTransfer(files, packages, Run(), '6');
        var first = ManagedMailboxRecovery.Selected(files)!;
        transfer.FilesForTests.PublishedForTests = path => { if (path == ManagedMailboxRecovery.PathFor(files, first, "mapping")) { throw new IOException("cut"); } };
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(first.Value.Request, "N"), managedTransfer: transfer));
        transfer.FilesForTests.PublishedForTests = null;
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"))) { ManagedMailboxRecovery.Resolve(files, native); }
        var epoch = NativeEpochRecord.Parse(Convert.FromBase64String(NativeCanonical.ParseLines(File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, first, "epoch-intent")), "OpenBlade.ManagedMailboxEpochIntent.v2", 3)[1]));
        using var second = SignedTransfer(files, packages, Run('9') with { Process = "18:101" }, 'a');
        var active = ManagedMailboxRecovery.Selected(files)!;
        string directory = Path.Combine(epochs.Root, epoch.Epoch);
        IDisposable? held = null;
        switch (fault)
        {
            case "run-binding": File.WriteAllBytes(Path.Combine(files.Root, "run-binding-" + Run().Run + ".bin"), "tampered"u8.ToArray()); break;
            case "signed-cache": File.WriteAllBytes(Path.Combine(files.Root, "build-" + packages.Managed.PackageDigest, "release.sig"), new byte[64]); break;
            case "native-origin": File.Delete(Path.Combine(directory, "origin.bin")); break;
            case "preparation-envelope": File.WriteAllBytes(Path.Combine(directory, "preparation.bin"), "tampered"u8.ToArray()); break;
            case "unknown-reservation": File.WriteAllBytes(Path.Combine(epochs.Root, new string('f', 32) + ".reserved"), []); break;
            case "submission": File.WriteAllBytes(Path.Combine(directory, "submission.bin"), "unknown-submission"u8.ToArray()); break;
            case "attempt": File.WriteAllBytes(Path.Combine(directory, "attempts", "launch-unknown.bin"), "unknown-attempt"u8.ToArray()); break;
            case "frontier": epochs.Publish(Path.Combine(epochs.Root, "selection-00000000000000000001-0.bin"), epoch.Encode()); break;
            case "active": epochs.Publish(Path.Combine(epochs.Root, "selection-00000000000000000001-0.bin"), epoch.Encode()); epochs.PublishCurrent(epoch.Encode()); break;
            case "custody": held = epochs.AcquireServiceCustody(Path.Combine(directory, "attempts", "custody.lock")); break;
            default: File.Delete(ManagedMailboxRecovery.PathFor(files, first, fault)); break;
        }
        try
        {
            var before = Directory.EnumerateDirectories(epochs.Root).Order().ToArray();
            Refused(() => ManagedMailboxStartup.Open(() => ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()), _ => null, _ => null,
                native => WindowsManagedMailboxRun.AcquireUnenrolledExclusion(files.RetainFixtureView(), native)));
            Refused(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(new string('a', 32), "N"), managedTransfer: second));
            Assert.Equal(before, Directory.EnumerateDirectories(epochs.Root).Order());
            Assert.False(File.Exists(Path.Combine(directory, "Retired.bin")));
            Assert.Null(ManagedMailboxRecovery.ReadOptional(files, ManagedMailboxRecovery.PathFor(files, active, "epoch-intent")));
        }
        finally { held?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeFirstBeginPublicationCannotOrphanPreparation(bool keepStartupExclusion)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var package = await WindowsNativeEpochTests.Package(epochs, schema: 2, mailbox: true);
        using var transfer = Transfer(files, package, Run(), '6');
        var before = Snapshot(epochs.Root);
        ManagedMailboxStartup.Admission? startup = null;
        try
        {
            Assert.ThrowsAny<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, package, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: transfer,
                testCrashBoundary: boundary =>
                {
                    if (boundary != NativeEpochBoundary.AdmissionRequested) { return; }
                    startup = ManagedMailboxStartup.Open(() => ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()), _ => null, _ => null,
                        native => WindowsManagedMailboxRun.AcquireUnenrolledExclusion(files.RetainFixtureView(), native, _ => { }));
                    if (!keepStartupExclusion) { startup.Dispose(); startup = null; }
                }));
            Assert.Equal(before.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, before);
            var selected = ManagedMailboxRecovery.Selected(files)!;
            Assert.Null(ManagedMailboxRecovery.ReadOptional(files, ManagedMailboxRecovery.PathFor(files, selected, "epoch-intent")));
            Assert.Null(ManagedMailboxRecovery.ReadOptional(files, ManagedMailboxRecovery.PathFor(files, selected, "mapping")));
        }
        finally { startup?.Dispose(); }
    }

    [Fact]
    public async Task BeginRetainsExactHandoffExclusionAcrossEveryPreparationPublication()
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var package = await WindowsNativeEpochTests.Package(epochs, schema: 2, mailbox: true);
        using var transfer = Transfer(files, package, Run(), '6');
        int checkedBoundaries = 0;
        using var tx = WindowsNativeEpochTransaction.Begin(epochs, package, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: transfer,
            testCrashBoundary: boundary =>
            {
                if (boundary == NativeEpochBoundary.AdmissionRequested) { return; }
                checkedBoundaries++;
                Assert.ThrowsAny<IOException>(() => files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")));
            });
        Assert.True(checkedBoundaries >= 8);
    }

    [Theory]
    [InlineData("epoch-intent")]
    [InlineData("mapping")]
    [InlineData("terminal-intent")]
    public async Task ActualSignedFirstEpochPartialBindingAndTerminalPublicationRecoverWithoutRetiredFabrication(string cut)
    {
        using var files = Handoff(); using var epochs = Bootstrap();
        using var package = await WindowsNativeEpochTests.Package(epochs, schema: 2, mailbox: true);
        using var transfer = Transfer(files, package, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        transfer.FilesForTests.PublishedForTests = path =>
        {
            if (path == ManagedMailboxRecovery.PathFor(files, selected, cut == "terminal-intent" ? "mapping" : cut))
            { throw new IOException("preserved exact publication cut"); }
        };
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), package, Guid.ParseExact(selected.Value.Request, "N"), managedTransfer: transfer));
        transfer.FilesForTests.PublishedForTests = null;
        string epoch = NativeEpochRecord.Parse(Convert.FromBase64String(NativeCanonical.ParseLines(
            File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, selected, "epoch-intent")), "OpenBlade.ManagedMailboxEpochIntent.v2", 3)[1])).Epoch;
        if (cut == "terminal-intent")
        {
            files.PublishedForTests = path => { if (path == ManagedMailboxRecovery.PathFor(files, selected, "terminal-intent")) { throw new IOException("preserved terminal cut"); } };
            using (var first = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
            { Assert.Throws<IOException>(() => ManagedMailboxRecovery.Resolve(files, first, _ => { })); }
            files.PublishedForTests = null;
        }
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        { ManagedMailboxRecovery.Resolve(files, native, _ => { }); }
        Assert.False(File.Exists(Path.Combine(epochs.Root, epoch, "Retired.bin")));
        Assert.Null(epochs.ReadCurrent());
        ManagedMailboxRecovery.ValidateTerminal(files, selected);
        Assert.Equal(File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, selected, "mapping")), File.ReadAllBytes(Path.Combine(epochs.Root, epoch, "managed-handoff.bin")));
    }
    [Fact]
    public async Task ActualSignedConsumedRetirementAllowsNewManagedPredecessorAndSecondEpochButNeverOldReceiptRemapping()
    {
        using var files = Handoff(); using var epochs = Bootstrap();
        using var packages = await Packages();
        var package = packages.Native;
        using var transfer = SignedTransfer(files, packages, Run(), '6');
        NativeEpochRecord original;
        using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), package, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: transfer))
        {
            original = tx.Record; tx.MarkStartupSubmitted();
            NativeRecoveryTests.AddAttempt(epochs, original, 17, 1, false);
            string launchPath = Path.Combine(epochs.Root, original.Epoch, "attempts", "launch-17-1.bin");
            epochs.Publish(Path.Combine(epochs.Root, original.Epoch, "attempts", "drain-17-1.bin"),
                NativeCanonical.Lines("OpenBlade.NativeTransportDrain.v1", original.Epoch, NativeCanonical.Digest(File.ReadAllBytes(launchPath)), "17", "1"));
            tx.CommitAfterBoundReadiness(new string('e', 64));
        }
        using (var selection = await WindowsNativeInstalledSelection.OpenAsync(epochs.RetainFixtureView(), default))
        { selection.RetireRecoverableSelection(17, 1); }
        var first = ManagedMailboxRecovery.Selected(files)!;
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        { ManagedMailboxRecovery.Resolve(files, native, _ => { }); }
        byte[] oldReceipt = File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, first, "receipt"));
        using var second = SignedTransfer(files, packages, Run('9') with { Process = "18:101" }, 'a');
        NativeEpochRecord next;
        using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), package, Guid.ParseExact(new string('a', 32), "N"), managedTransfer: second))
        { next = tx.Record; }
        Assert.NotEqual(original.Epoch, next.Epoch);
        Assert.Equal(2, next.Ordinal);
        Assert.Equal(oldReceipt, File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, first, "receipt")));
        Assert.ThrowsAny<IOException>(() => transfer.BindEpoch(epochs, next));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task IntactLegacyTransferUsesOnlyItsPinnedHistoricalExitContractAndFreshNativeRetirement(bool reviewedSource, bool preparation)
    {
        using var current = Handoff(); using var legacy = Handoff(); using var epochs = Bootstrap();
        using var preserved = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(epochs, schema: 2, mailbox: true,
            sourceRevision: reviewedSource ? ManagedMailboxLegacyRecovery.ExitBeforeBindingSource : new string('a', 40));
        var run = Run();
        using var transfer = Transfer(current, package, run, '6');
        NativeEpochRecord original;
        using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), package, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: transfer))
        { original = tx.Record; }
        string nativeMapping = Path.Combine(epochs.Root, original.Epoch, "managed-handoff.bin");
        byte[] v2 = File.ReadAllBytes(nativeMapping);
        preserved.Publish(Path.Combine(preserved.Root, "original-v2-fixture-input.bin"), v2);
        var fields = NativeCanonical.ParseLines(v2, "OpenBlade.ManagedMailboxEpoch.v2", 9);
        byte[] v1 = NativeCanonical.Lines("OpenBlade.ManagedMailboxEpoch.v1", fields[..6]);
        // Historical fixture data only: identical request/receipt/epoch fields,
        // represented by the old codec. No role is launched and no receipt is
        // assigned another epoch. Preserve the source vector separately.
        // The pinned historical producer predates the preparation envelope.
        File.Delete(Path.Combine(epochs.Root, original.Epoch, "preparation.bin"));
        File.WriteAllBytes(nativeMapping, v1);
        if (preparation)
        {
            // Private historical partial-Begin fixture: no frontier/selection
            // was published, while the original signed origin/mapping survived.
            File.Delete(Path.Combine(epochs.Root, "current.bin"));
            foreach (string path in Directory.EnumerateFiles(epochs.Root, "selection-*.bin")) { File.Delete(path); }
            File.Delete(Path.Combine(epochs.Root, original.Epoch, "Provisional.bin"));
        }
        else
        {
            using var installed = await WindowsNativeInstalledSelection.OpenAsync(epochs.RetainFixtureView(), default);
            installed.RetireRecoverableSelection();
        }
        var selected = ManagedMailboxRecovery.Selected(current)!;
        legacy.PublishCurrent(run.Encode());
        legacy.PublishCustody(Path.Combine(legacy.Root, "run-" + run.Run + ".bin"), run.Encode());
        var binding = new ManagedMailboxBuildBinding(NativeCanonical.Digest(run.Encode()), selected.Value.Installation,
            new string('7', 64), NativeCanonical.Digest(package.VerificationProjection));
        legacy.PublishCustody(Path.Combine(legacy.Root, "run-binding-" + run.Run + ".bin"), binding.Encode());
        legacy.PublishCustody(Path.Combine(legacy.Root, "request.bin"), selected.Request);
        legacy.PublishCustody(Path.Combine(legacy.Root, "receipt.bin"), ManagedMailboxRecovery.Receipt(selected));
        legacy.PublishCustody(Path.Combine(legacy.Root, "mapping.bin"), v1);
        var before = new[] { "current.bin", "request.bin", "receipt.bin", "mapping.bin" }.ToDictionary(n => n, n => File.ReadAllBytes(Path.Combine(legacy.Root, n)));
        using var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView());
        using var exclusion = legacy.AcquireServiceCustody(Path.Combine(legacy.Root, "exclusion.lock"));
        if (reviewedSource)
        {
            ManagedMailboxLegacyRecovery.Resolve(legacy, native, (_, _, _) => { });
            byte[] terminal = File.ReadAllBytes(Path.Combine(legacy.Root, "legacy-terminal-v2.bin"));
            ManagedMailboxLegacyRecovery.Resolve(legacy, native, (_, _, _) => { });
            Assert.Equal(terminal, File.ReadAllBytes(Path.Combine(legacy.Root, "legacy-terminal-v2.bin")));
            if (preparation)
            {
                exclusion.Dispose(); native.Dispose();
                using var nextFiles = Handoff(); using var incoming = await Packages('c');
                using (var startup = ManagedMailboxStartup.Open(() => ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()),
                    fresh => WindowsManagedMailboxRun.AcquireLegacyExclusion(legacy.RetainFixtureView(), fresh, (_, _, _) => { }), _ => null,
                    fresh => WindowsManagedMailboxRun.AcquireUnenrolledExclusion(nextFiles.RetainFixtureView(), fresh)))
                {
                    Assert.Null(await WindowsNativeInstalledSelection.OpenForSetupAsync(epochs.RetainFixtureView(), default,
                        () => nextFiles.RetainFixtureView(), fresh => WindowsManagedMailboxRun.AuthenticateLegacyTerminal(legacy, fresh, (_, _, _) => { })));
                }
                using var nextTransfer = SignedTransfer(nextFiles, incoming, Run('9') with { Process = "18:101" }, 'a');
                using var next = WindowsNativeEpochTransaction.Begin(epochs, incoming.Native, Guid.ParseExact(new string('a', 32), "N"), managedTransfer: nextTransfer,
                    acquireLegacy: fresh => WindowsManagedMailboxRun.AcquireLegacyExclusion(legacy.RetainFixtureView(), fresh, (_, _, _) => { }));
                Assert.Equal(NativeEpochPhase.Provisional, next.Record.Phase);
                Assert.False(File.Exists(Path.Combine(epochs.Root, original.Epoch, "Retired.bin")));
            }
        }
        else
        {
            Assert.Throws<IOException>(() => ManagedMailboxLegacyRecovery.Resolve(legacy, native, (_, _, _) => { }));
            Assert.False(File.Exists(Path.Combine(legacy.Root, "legacy-terminal-v2.bin")));
        }
        foreach (var (name, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(legacy.Root, name))); }
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("original-exit")]
    public void MissingOriginalEvidenceNeverResolvesFromAbsentCurrentOrMapping(string missing)
    {
        using var files = Handoff(); using var epochs = Bootstrap();
        var selected = Quiescent(files, Run());
        // A private fixture moves only its own record to model absent input; the
        // bytes remain preserved and no product recovery deletes anything.
        File.Move(ManagedMailboxRecovery.PathFor(files, selected, missing), ManagedMailboxRecovery.PathFor(files, selected, missing) + ".preserved");
        using var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView());
        Assert.Throws<IOException>(() => ManagedMailboxRecovery.Resolve(files, native, _ => { }));
        Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "terminal")));
    }
    [Fact]
    public void MappingWithoutIntentAndUnmatchedNativeReservationRemainFenced()
    {
        using var files = Handoff(); using var epochs = Bootstrap();
        var selected = Quiescent(files, Run());
        files.PublishCustody(ManagedMailboxRecovery.PathFor(files, selected, "mapping"), "unmatched preserved mapping"u8.ToArray());
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        { Assert.Throws<IOException>(() => ManagedMailboxRecovery.Resolve(files, native, _ => { })); }
        File.WriteAllBytes(Path.Combine(epochs.Root, new string('e', 32) + ".reserved"), []);
        Assert.ThrowsAny<IOException>(() => ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()));
    }
    [Theory]
    [InlineData((int)ManagedBootstrapBoundary.IntentPublished)]
    [InlineData((int)ManagedBootstrapBoundary.DirectoryCreated)]
    [InlineData((int)ManagedBootstrapBoundary.ExclusionCreated)]
    [InlineData((int)ManagedBootstrapBoundary.RootPublished)]
    [InlineData((int)ManagedBootstrapBoundary.Completed)]
    public void AuthenticatedInterruptedBootstrapRecoversItsExactCreationPrefix(int boundary)
    {
        using var anchor = WindowsNativeEpochFiles.CreatePrivateFixture();
        anchor.BootstrapCrashForTests = (ManagedBootstrapBoundary)boundary;
        Assert.Throws<IOException>(() => anchor.CreateOrOpenManagedHandoffChild());
        anchor.BootstrapCrashForTests = null;
        using var child = anchor.CreateOrOpenManagedHandoffChild();
        using var exclusion = child.AcquireServiceCustody(Path.Combine(child.Root, "exclusion.lock"));
        Assert.Equal(0, exclusion.Length);
        Assert.True(File.Exists(Path.Combine(child.Root, "handoff-root.bin")));
    }
    [Fact]
    public void PrecreatedUnknownLeafIsPreservedAndCannotBecomeAnEnrolledStore()
    {
        using var anchor = WindowsNativeEpochFiles.CreatePrivateFixture();
        string leaf = Path.Combine(anchor.Root, "ManagedHandoff"); Directory.CreateDirectory(leaf);
        File.WriteAllBytes(Path.Combine(leaf, "unknown.bin"), [1, 2, 3]);
        Assert.Throws<IOException>(() => anchor.CreateOrOpenManagedHandoffChild());
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(leaf, "unknown.bin")));
    }
    [Fact]
    public void ActualStartupKeepsUnenrolledDefaultAvailableAndDoesNotHoldNativeLocksAgainstFirstEnrollment()
    {
        using var files = Handoff(); using var epochs = Bootstrap();
        using var startup = ManagedMailboxStartup.Open(() => ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()), _ => null,
            _ => throw new InvalidDataException("optional unenrolled build"), native => WindowsManagedMailboxRun.AcquireUnenrolledExclusion(files.RetainFixtureView(), native));
        Assert.Null(startup.Run);
        using var firstEnrollmentNative = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView());
        firstEnrollmentNative.RequireSafe();
        Assert.ThrowsAny<IOException>(() => files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")));
    }
    [Fact]
    public void LegacyTransferWithMissingOriginalCustodyCannotTakeOptionalStorageFallback()
    {
        using var legacy = WindowsNativeEpochFiles.CreatePrivateFixture(); using var epochs = Bootstrap();
        legacy.PublishCustody(Path.Combine(legacy.Root, "request.bin"), "preserved unmatched transfer"u8.ToArray());
        using var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView());
        Assert.Throws<IOException>(() => WindowsManagedMailboxRun.AcquireLegacyExclusion(legacy.RetainFixtureView(), native));
        Assert.Equal("preserved unmatched transfer"u8.ToArray(), File.ReadAllBytes(Path.Combine(legacy.Root, "request.bin")));
    }

    [Fact]
    public void ActualStartupNeverTreatsUnknownNativeCustodyAsOptionalEnrollmentFailure()
    {
        using var epochs = Bootstrap();
        File.WriteAllBytes(Path.Combine(epochs.Root, "unmatched.reserved"), []);
        bool ordinary = false;
        Assert.ThrowsAny<IOException>(() => ManagedMailboxStartup.Open(() => ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()),
            _ => null, _ => null, _ => { ordinary = true; return null; }));
        Assert.False(ordinary);
    }
}
