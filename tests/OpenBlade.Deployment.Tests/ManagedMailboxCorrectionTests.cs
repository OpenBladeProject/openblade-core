using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class ManagedMailboxCorrectionTests
{
    internal static WindowsNativeEpochFiles Bootstrap()
    {
        var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        files.Publish(Path.Combine(files.Root, "root.bin"), NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent"));
        files.EnsureOwnerStore();
        return files;
    }
    internal static WindowsNativeEpochFiles Handoff()
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
        { ManagedMailboxRecovery.Resolve(files, native, _ => { }); }
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
    internal sealed record SignedPair(WindowsNativePackage Native, WindowsManagedMailboxPackage Managed) : IDisposable
    { public void Dispose() { Managed.Dispose(); Native.Dispose(); } }
    internal static async Task<SignedPair> Packages(char nonce = 'b', string? sourceRevision = null, bool mutation = false)
    {
        byte[] nativeBytes = WindowsNativeInventoryBurnExtractorTests.NativeEpochBundle(WindowsNativeEpochTests.Image("service", nonce, schema: 2, mailbox: true, sourceRevision: sourceRevision, mutation: mutation), WindowsNativeEpochTests.Image("agent", nonce, schema: 2, mailbox: true, sourceRevision: sourceRevision, mutation: mutation), sourceRevision);
        using var signed = OpenBladeSignedReleaseVerifierTests.TestRelease.CreateWithInstallerBytes(nativeBytes, sourceRevision);
        var nativeFiles = WindowsNativeEpochFiles.CreatePrivateFixture();
        File.WriteAllBytes(Path.Combine(nativeFiles.Root, "package.exe"), nativeBytes);
        var native = await WindowsNativePackage.OpenLocalDevAsync(nativeFiles.OpenFile(Path.Combine(nativeFiles.Root, "package.exe")), nativeFiles,
            signed.ManifestBytes, signed.SignatureBytes, NativeLocalDevEnrollment.ForAlgorithmTests(new Guid("11111111-1111-1111-1111-111111111111"), signed.Policy), default);
        byte[] managedBytes = WindowsNativeInventoryBurnExtractorTests.ManagedMailboxBundle(nonce.ToString());
        using var managedSigned = signed.SignSiblingInstaller(managedBytes);
        var managedFiles = WindowsNativeEpochFiles.CreatePrivateFixture();
        File.WriteAllBytes(Path.Combine(managedFiles.Root, "package.exe"), managedBytes);
        var managed = await WindowsManagedMailboxPackage.OpenLocalDevAsync(managedFiles.OpenFile(Path.Combine(managedFiles.Root, "package.exe")), managedFiles,
            managedSigned.ManifestBytes, managedSigned.SignatureBytes, NativeLocalDevEnrollment.FromProtectedProjection(native.VerificationProjection), default);
        return new(native, managed);
    }
    internal static WindowsManagedMailboxTransfer SignedTransfer(WindowsNativeEpochFiles files, SignedPair packages, ManagedMailboxRun run, char request)
    {
        packages.Managed.Preserve(files);
        var binding = new ManagedMailboxBuildBinding(NativeCanonical.Digest(run.Encode()), packages.Managed.Installation, packages.Managed.PackageDigest, packages.Managed.EnrollmentDigest);
        using var owner = WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), run, binding: binding, projection: packages.Managed.VerificationProjection);
        var transfer = new WindowsManagedMailboxTransfer(files.RetainFixtureView(), run.Encode(), new(NativeCanonical.Digest(run.Encode()), packages.Managed.Installation, new string(request, 32), NativeCanonical.Digest(packages.Native.Record)));
        owner.PublishQuiescent(); owner.Dispose(); transfer.ConfirmOriginalExited(); return transfer;
    }
    private static Dictionary<string, byte[]> Snapshot(string root, bool unlockedOnly = false) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !unlockedOnly || !path.EndsWith(".lock", StringComparison.Ordinal))
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
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(first.Value.Request, "N"), managedTransfer: transfer, storage: Storage(files)));
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
            Assert.Null(await WindowsNativeInstalledSelection.OpenForSetupAsync(epochs.RetainFixtureView(), default, incoming: packages.Native, storage: Storage(files)));
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
        using var tx = WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(new string('c', 32), "N"), managedTransfer: third, storage: Storage(files));
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
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(selected.Value.Request, "N"), managedTransfer: transfer, storage: Storage(files)));
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
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(first.Value.Request, "N"), managedTransfer: transfer, storage: Storage(files)));
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
            Refused(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(new string('a', 32), "N"), managedTransfer: second, storage: Storage(files)));
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
            Assert.ThrowsAny<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, package, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: transfer, storage: Storage(files),
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
        using var tx = WindowsNativeEpochTransaction.Begin(epochs, package, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: transfer, storage: Storage(files),
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
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), package, Guid.ParseExact(selected.Value.Request, "N"), managedTransfer: transfer, storage: Storage(files)));
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
        using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), package, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: transfer, storage: Storage(files)))
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
        using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), package, Guid.ParseExact(new string('a', 32), "N"), managedTransfer: second, storage: Storage(files)))
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
        using var packages = await Packages(sourceRevision: reviewedSource ? ManagedMailboxLegacyRecovery.ExitBeforeBindingSource : new string('a', 40));
        var package = packages.Native;
        var run = Run();
        using var transfer = Transfer(current, package, run, '6');
        NativeEpochRecord original;
        using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), package, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: transfer, storage: Storage(current)))
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
        packages.Managed.Preserve(legacy);
        legacy.PublishCurrent(run.Encode());
        legacy.PublishCustody(Path.Combine(legacy.Root, "run-" + run.Run + ".bin"), run.Encode());
        var binding = new ManagedMailboxBuildBinding(NativeCanonical.Digest(run.Encode()), selected.Value.Installation,
            packages.Managed.PackageDigest, NativeCanonical.Digest(package.VerificationProjection));
        legacy.PublishCustody(Path.Combine(legacy.Root, "run-binding-" + run.Run + ".bin"), binding.Encode());
        legacy.PublishCustody(Path.Combine(legacy.Root, "request.bin"), selected.Request);
        legacy.PublishCustody(Path.Combine(legacy.Root, "receipt.bin"), ManagedMailboxRecovery.Receipt(selected));
        legacy.PublishCustody(Path.Combine(legacy.Root, "mapping.bin"), v1);
        var before = new[] { "current.bin", "request.bin", "receipt.bin", "mapping.bin" }.ToDictionary(n => n, n => File.ReadAllBytes(Path.Combine(legacy.Root, n)));
        using var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView());
        using var exclusion = legacy.AcquireServiceCustody(Path.Combine(legacy.Root, "exclusion.lock"));
        if (reviewedSource)
        {
            ManagedMailboxLegacyRecovery.Resolve(legacy, native);
            byte[] terminal = File.ReadAllBytes(Path.Combine(legacy.Root, "legacy-terminal-v2.bin"));
            ManagedMailboxLegacyRecovery.Resolve(legacy, native);
            Assert.Equal(terminal, File.ReadAllBytes(Path.Combine(legacy.Root, "legacy-terminal-v2.bin")));
            if (preparation)
            {
                exclusion.Dispose(); native.Dispose();
                using var nextFiles = Handoff(); using var incoming = await Packages('c');
                using (var startup = ManagedMailboxStartup.Open(() => ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()),
                    fresh => WindowsManagedMailboxRun.AcquireLegacyExclusion(fresh, Storage(nextFiles, legacy)),
                    _ => WindowsManagedMailboxRun.StartForTests(nextFiles.RetainFixtureView(), Run('8') with { Process = "21:104" },
                        binding: new(NativeCanonical.Digest((Run('8') with { Process = "21:104" }).Encode()), incoming.Managed.Installation, incoming.Managed.PackageDigest, incoming.Managed.EnrollmentDigest),
                        projection: incoming.Managed.VerificationProjection), _ => throw new Exception("enrolled fixture required")))
                {
                    Assert.Null(await WindowsNativeInstalledSelection.OpenForSetupAsync(epochs.RetainFixtureView(), default,
                        incoming: incoming.Native, storage: Storage(nextFiles, legacy)));
                }
                using var nextTransfer = SignedTransfer(nextFiles, incoming, Run('9') with { Process = "18:101" }, 'a');
                using var next = WindowsNativeEpochTransaction.Begin(epochs, incoming.Native, Guid.ParseExact(new string('a', 32), "N"), managedTransfer: nextTransfer,
                    storage: Storage(nextFiles, legacy));
                Assert.Equal(NativeEpochPhase.Provisional, next.Record.Phase);
                Assert.False(File.Exists(Path.Combine(epochs.Root, original.Epoch, "Retired.bin")));
            }
        }
        else
        {
            Assert.Throws<IOException>(() => ManagedMailboxLegacyRecovery.Resolve(legacy, native));
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

    [Fact]
    public void PrivateFixtureOmittedStorageRefusesBeforeInvokingProductResolver()
    {
        using var epochs = Bootstrap(); using var managed = Handoff();
        using var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView());
        int resolved = 0;
        ManagedMailboxStorage ProductResolver() { resolved++; throw new Exception("Product resolver must never run."); }
        var error = Assert.Throws<InvalidOperationException>(() => ManagedMailboxStorage.Resolve(native, null, ProductResolver));
        Assert.Equal("Private mailbox fixtures require explicit storage.", error.Message);
        Assert.Equal(0, resolved);
        var storage = Storage(managed);
        Assert.Same(storage, ManagedMailboxStorage.Resolve(native, storage, ProductResolver));
        Assert.Equal(0, resolved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothLegacyAdaptersRejectPrivateFixtureStorageOmission(bool terminalOnly)
    {
        using var epochs = Bootstrap();
        var before = Snapshot(epochs.Root);
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        {
            Action invoke = terminalOnly
                ? () => WindowsManagedMailboxRun.AuthenticateLegacyTerminal(native)
                : () => WindowsManagedMailboxRun.AcquireLegacyExclusion(native)?.Dispose();
            var error = Assert.Throws<InvalidOperationException>(invoke);
            Assert.Equal("Private mailbox fixtures require explicit storage.", error.Message);
        }
        Preserved(epochs.Root, before);
    }

    [Fact]
    public async Task MailboxBeginRejectsPrivateFixtureStorageOmissionBeforePublication()
    {
        using var files = Handoff(); using var epochs = Bootstrap();
        using var package = await WindowsNativeEpochTests.Package(epochs, schema: 2, mailbox: true);
        using var transfer = Transfer(files, package, Run(), '6');
        var before = Snapshot(epochs.Root); var mailboxBefore = Snapshot(files.Root);
        var error = Assert.Throws<InvalidOperationException>(() => WindowsNativeEpochTransaction.Begin(epochs, package,
            Guid.ParseExact(new string('6', 32), "N"), managedTransfer: transfer));
        Assert.Equal("Private mailbox fixtures require explicit storage.", error.Message);
        Assert.Equal(before.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, before);
        Assert.Equal(mailboxBefore.Keys.Order(), Snapshot(files.Root).Keys.Order()); Preserved(files.Root, mailboxBefore);
    }

    [Fact]
    public async Task MailboxPreflightRejectsPrivateFixtureStorageOmissionButMetadataOnlyInspectionDoesNotResolveStorage()
    {
        using var epochs = Bootstrap();
        using var package = await WindowsNativeEpochTests.Package(epochs, schema: 2, mailbox: true);
        var before = Snapshot(epochs.Root);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsNativeInstalledSelection.OpenForSetupAsync(
            epochs.RetainFixtureView(), CancellationToken.None, incoming: package));
        Assert.Equal("Private mailbox fixtures require explicit storage.", error.Message);
        Assert.Null(await WindowsNativeInstalledSelection.OpenForSetupAsync(epochs.RetainFixtureView(), CancellationToken.None));
        Assert.Equal(before.Keys.Order(), Snapshot(epochs.Root).Keys.Order()); Preserved(epochs.Root, before);
    }

    private static ManagedMailboxStorage Storage(WindowsNativeEpochFiles managed, WindowsNativeEpochFiles? legacy = null) =>
        new(() => legacy is not null, () => legacy!.RetainFixtureView(), () => managed.RetainFixtureView());

    [Theory]
    [InlineData("run")]
    [InlineData("binding")]
    [InlineData("package")]
    [InlineData("signature")]
    [InlineData("projection")]
    [InlineData("transfer-history")]
    public async Task LeadingUnreservedTerminalRevalidatesDistinctOriginalSignedBuild(string fault)
    {
        using var files = Handoff(); using var epochs = Bootstrap();
        using var leading = await Packages('d'); using var later = await Packages('b');
        using var firstTransfer = SignedTransfer(files, leading, Run('d') with { Process = "20:103" }, 'd');
        var first = ManagedMailboxRecovery.Selected(files)!;
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")))
        { ManagedMailboxRecovery.Resolve(files, native); }
        using var next = SignedTransfer(files, later, Run(), '6');
        Assert.NotEqual(leading.Managed.PackageDigest, later.Managed.PackageDigest);
        string run = ManagedMailboxRun.Parse(first.Run).Run;
        string cache = Path.Combine(files.Root, "build-" + leading.Managed.PackageDigest);
        switch (fault)
        {
            case "run": File.Delete(Path.Combine(files.Root, "run-" + run + ".bin")); break;
            case "binding": File.WriteAllBytes(Path.Combine(files.Root, "run-binding-" + run + ".bin"), "changed"u8.ToArray()); break;
            case "package": File.Delete(Path.Combine(cache, "package.exe")); break;
            case "signature": File.WriteAllBytes(Path.Combine(cache, "release.sig"), new byte[64]); break;
            case "projection": File.WriteAllBytes(ManagedMailboxHistory.RunPath(files, run), "changed"u8.ToArray()); break;
            case "transfer-history": File.WriteAllBytes(ManagedMailboxRecovery.PathFor(files, first, "history"), "changed"u8.ToArray()); break;
        }
        var before = Snapshot(epochs.Root); var history = Snapshot(files.Root);
        Refused(() => ManagedMailboxStartup.Open(() => ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()), _ => null, _ => null,
            native => WindowsManagedMailboxRun.AcquireUnenrolledExclusion(files.RetainFixtureView(), native)));
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        { Refused(() => { using var maintenance = WindowsNativeTrustLifecycle.AcquireManagedEnrollmentChangeCustody(files.RetainFixtureView(), native); }); }
        await Assert.ThrowsAnyAsync<Exception>(() => WindowsNativeInstalledSelection.OpenForSetupAsync(epochs.RetainFixtureView(), default,
            incoming: later.Native, storage: Storage(files)));
        Refused(() => WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), later.Native, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: next, storage: Storage(files)));
        Preserved(epochs.Root, before); Preserved(files.Root, history);
    }

    [Theory]
    [InlineData("run-history")]
    [InlineData("history")]
    public async Task OriginalProjectionSurvivesDifferentCurrentBuildAndInterruptedHistoryPublication(string cut)
    {
        using var files = Handoff(); using var epochs = Bootstrap();
        using var original = await Packages('d'); using var replacement = await Packages('b');
        using var transfer = SignedTransfer(files, original, Run('d') with { Process = "20:103" }, 'd');
        var first = ManagedMailboxRecovery.Selected(files)!;
        // Historical data without the new run envelope can upgrade from the
        // exact positively recovered original projection of its reserved epoch.
        if (cut == "run-history")
        {
            transfer.FilesForTests.PublishedForTests = path => { if (Path.GetFileName(path) == "preparation.bin") { throw new IOException("cut"); } };
            epochs.PublishedForTests = path => { if (Path.GetFileName(path) == "preparation.bin") { throw new IOException("cut"); } };
            Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, original.Native, Guid.ParseExact(first.Value.Request, "N"), managedTransfer: transfer, storage: Storage(files)));
            epochs.PublishedForTests = null;
            File.Delete(ManagedMailboxHistory.RunPath(files, ManagedMailboxRun.Parse(first.Run).Run));
        }
        string historyPath = cut == "history" ? ManagedMailboxRecovery.PathFor(files, first, "history")
            : ManagedMailboxHistory.RunPath(files, ManagedMailboxRun.Parse(first.Run).Run);
        files.PublishedForTests = path => { if (path == historyPath) { throw new IOException("new evidence renamed before failure"); } };
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")))
        { Assert.Throws<IOException>(() => ManagedMailboxRecovery.Resolve(files, native)); }
        files.PublishedForTests = null;
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")))
        { ManagedMailboxRecovery.Resolve(files, native); }
        byte[] terminal = File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, first, "terminal"));
        using var next = SignedTransfer(files, replacement, Run(), '6');
        Assert.NotEqual(original.Managed.EnrollmentDigest, replacement.Managed.EnrollmentDigest);
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        { ManagedMailboxRecovery.AuthenticateRetainedTerminals(files, native, ManagedMailboxRecovery.Selected(files)); }
        using var tx = WindowsNativeEpochTransaction.Begin(epochs, replacement.Native, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: next, storage: Storage(files));
        Assert.Equal(terminal, File.ReadAllBytes(ManagedMailboxRecovery.PathFor(files, first, "terminal")));
    }

    private static void RetiredOwner(WindowsNativeEpochFiles epochs, string epoch, string installation, string service)
    {
        string previous = new('0', 64); string[] phases = ["Reserved", "Active", "Retiring", "Retired"];
        for (int i = 0; i < phases.Length; i++)
        {
            byte[] bytes = NativeCanonical.Lines("OpenBlade.NativeOwner.v1", phases[i], (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                previous, installation, epoch, service, "17:1", new string('d', 64));
            epochs.Publish(Path.Combine(epochs.Root, "owner", $"owner-{i + 1:D20}.bin"), bytes);
            previous = NativeCanonical.Digest(bytes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredOwnerOnlyOrMixedMissingGenerationCannotResetNativeHistory(bool survivingPreparation)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var packages = await Packages();
        if (survivingPreparation)
        {
            using var old = SignedTransfer(files, packages, Run('d') with { Process = "20:103" }, 'd');
            epochs.PublishedForTests = path => { if (Path.GetFileName(path) == "preparation.bin") { throw new IOException("cut"); } };
            Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(new string('d', 32), "N"), managedTransfer: old, storage: Storage(files)));
            epochs.PublishedForTests = null;
            using var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView());
            using var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
            ManagedMailboxRecovery.Resolve(files, native);
        }
        using var next = SignedTransfer(files, packages, Run(), '6');
        RetiredOwner(epochs, new string('f', 32), packages.Managed.Installation, packages.Native.Service.Id);
        var before = Snapshot(epochs.Root);
        Refused(() => ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()));
        await Assert.ThrowsAnyAsync<IOException>(() => WindowsNativeInstalledSelection.OpenForSetupAsync(epochs.RetainFixtureView(), default,
            incoming: packages.Native, storage: Storage(files)));
        Refused(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: next, storage: Storage(files)));
        Preserved(epochs.Root, before); Assert.Null(epochs.ReadCurrent());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameDefaultLegacyAdaptersUsePrivateRootsWhileEnrolledRunOwnsLifetime(bool legacyPresent)
    {
        using var files = Handoff(); using var legacy = Handoff(); using var epochs = Bootstrap(); using var packages = await Packages();
        using var transfer = SignedTransfer(files, packages, Run('d') with { Process = "20:103" }, 'd');
        epochs.PublishedForTests = path => { if (Path.GetFileName(path) == "preparation.bin") { throw new IOException("cut"); } };
        var storage = Storage(files, legacyPresent ? legacy : null);
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(new string('d', 32), "N"), managedTransfer: transfer, storage: storage));
        epochs.PublishedForTests = null;
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")))
        { ManagedMailboxRecovery.Resolve(files, native); }
        var record = Run('9') with { Process = "18:101" };
        var binding = new ManagedMailboxBuildBinding(NativeCanonical.Digest(record.Encode()), packages.Managed.Installation, packages.Managed.PackageDigest, packages.Managed.EnrollmentDigest);
        using (var startup = ManagedMailboxStartup.Open(() => ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()),
            native => WindowsManagedMailboxRun.AcquireLegacyExclusion(native, storage),
            native => WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), record, binding: binding, projection: packages.Managed.VerificationProjection, native: native),
            _ => throw new Exception("fixture enrollment is required")))
        {
            Assert.IsType<WindowsManagedMailboxRun>(startup.Run);
            var before = Snapshot(files.Root, unlockedOnly: true); var legacyBefore = Snapshot(legacy.Root, unlockedOnly: true);
            Assert.Throws<IOException>(() => files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")));
            Assert.Null(await WindowsNativeInstalledSelection.OpenForSetupAsync(epochs.RetainFixtureView(), default, incoming: packages.Native, storage: storage));
            Assert.Null(await WindowsNativeInstalledSelection.OpenForSetupAsync(epochs.RetainFixtureView(), default, storage: storage));
            Assert.Equal(record.Encode(), files.ReadCurrent()); Preserved(files.Root, before); Preserved(legacy.Root, legacyBefore);
        }
    }

    [Theory]
    [InlineData("missing-predecessor")]
    [InlineData("missing-selected")]
    [InlineData("older-frontier-name")]
    [InlineData("older-frontier-phase")]
    public async Task SelectedHistoryRequiresEveryOriginalGenerationPredecessorAndExactFrontier(string fault)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var packages = await Packages();
        async Task<NativeEpochRecord> Retire(ManagedMailboxRun run, char request)
        {
            using var transfer = SignedTransfer(files, packages, run, request);
            using (var tx = WindowsNativeEpochTransaction.Begin(epochs.RetainFixtureView(), packages.Native, Guid.ParseExact(new string(request, 32), "N"), managedTransfer: transfer, storage: Storage(files)))
            {
                tx.MarkStartupSubmitted(); NativeRecoveryTests.AddAttempt(epochs, tx.Record, 17, 1, false);
                string launch = Path.Combine(epochs.Root, tx.Record.Epoch, "attempts", "launch-17-1.bin");
                epochs.Publish(Path.Combine(epochs.Root, tx.Record.Epoch, "attempts", "drain-17-1.bin"),
                    NativeCanonical.Lines("OpenBlade.NativeTransportDrain.v1", tx.Record.Epoch, NativeCanonical.Digest(File.ReadAllBytes(launch)), "17", "1"));
                tx.CommitAfterBoundReadiness(new string('e', 64));
            }
            using var selection = await WindowsNativeInstalledSelection.OpenAsync(epochs.RetainFixtureView(), default);
            selection.RetireRecoverableSelection(17, 1); var result = selection.Record;
            selection.Dispose();
            using var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView());
            using var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
            ManagedMailboxRecovery.Resolve(files, native); return result;
        }
        var first = await Retire(Run(), '6');
        var second = await Retire(Run('9') with { Process = "18:101" }, 'a');
        using var next = SignedTransfer(files, packages, Run('c') with { Process = "19:102" }, 'c');
        if (fault.StartsWith("missing-", StringComparison.Ordinal))
        {
            var removed = fault == "missing-selected" ? second : first;
            string directory = Path.GetFullPath(Path.Combine(epochs.Root, removed.Epoch));
            Assert.Equal(epochs.Root, Path.GetDirectoryName(directory));
            Directory.Delete(directory, recursive: true); File.Delete(directory + ".reserved");
            if (fault == "missing-predecessor")
            {
                foreach (string path in Directory.EnumerateFiles(epochs.Root, "selection-*.bin"))
                { if (NativeEpochRecord.Parse(File.ReadAllBytes(path)).Epoch == removed.Epoch) { File.Delete(path); } }
            }
        }
        else
        {
            string path = Path.Combine(epochs.Root, $"selection-{first.Ordinal:D20}-1.bin");
            if (fault == "older-frontier-name") { File.Move(path, Path.Combine(epochs.Root, "selection-00000000000000000000-1.bin")); }
            else { var committed = NativeEpochRecord.Parse(File.ReadAllBytes(path)); File.WriteAllBytes(path, (committed with { Readiness = new string('f', 64) }).Encode()); }
        }
        var before = Snapshot(epochs.Root);
        Refused(() => ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()));
        Refused(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(new string('c', 32), "N"), managedTransfer: next, storage: Storage(files)));
        Preserved(epochs.Root, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreservedTerminalUsesOriginalProjectionAfterCurrentEnrollmentRotationOrRemoval(bool removed)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages('d'); using var rotated = await Packages('b');
        using var transfer = SignedTransfer(files, original, Run(), '6');
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")))
        { ManagedMailboxRecovery.Resolve(files, native); }
        var before = Snapshot(files.Root); int currentReads = 0;
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")))
        {
            ManagedMailboxRecovery.Resolve(files, native, acquireEnrollment: () =>
            {
                currentReads++;
                if (removed) { throw new IOException("private current enrollment was removed"); }
                return NativeLocalDevEnrollment.FromProtectedProjection(rotated.Managed.VerificationProjection);
            });
        }
        Assert.Equal(0, currentReads); Preserved(files.Root, before);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("rotated")]
    [InlineData("removed")]
    public async Task OldUnreservedTerminalUpgradesOnlyFromExactRecoverableOriginalProjection(string current)
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var original = await Packages('d'); using var rotated = await Packages('b');
        using var transfer = SignedTransfer(files, original, Run(), '6');
        var selected = ManagedMailboxRecovery.Selected(files)!;
        using (var native = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView()))
        using (var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock")))
        { ManagedMailboxRecovery.Resolve(files, native); }
        // Private old-format fixture: parent terminal bytes carried no run or
        // transfer build projection. Keep those original terminal bytes exact.
        File.Delete(ManagedMailboxHistory.RunPath(files, Run().Run));
        File.Delete(ManagedMailboxRecovery.PathFor(files, selected, "history"));
        var before = Snapshot(files.Root); int currentReads = 0;
        using var fresh = ManagedMailboxNativeCustody.AcquireForTests(epochs.RetainFixtureView());
        using var custody = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
        void Resolve() => ManagedMailboxRecovery.Resolve(files, fresh, acquireEnrollment: () =>
        {
            currentReads++;
            if (current == "removed") { throw new IOException("private current enrollment missing"); }
            return NativeLocalDevEnrollment.FromProtectedProjection((current == "exact" ? original : rotated).Managed.VerificationProjection);
        });
        if (current == "exact") { Resolve(); Assert.True(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "history"))); }
        else { Assert.NotNull(Record.Exception(Resolve)); Assert.False(File.Exists(ManagedMailboxRecovery.PathFor(files, selected, "history"))); }
        Assert.Equal(1, currentReads);
        custody.Dispose(); Preserved(files.Root, before);
    }

    [Fact]
    public async Task ExplicitDigestCannotGrantMailboxNativeToNativeRecovery()
    {
        using var files = Handoff(); using var epochs = Bootstrap(); using var packages = await Packages();
        using var transfer = SignedTransfer(files, packages, Run(), '6');
        using (var tx = WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(new string('6', 32), "N"), managedTransfer: transfer, storage: Storage(files))) { }
        byte[] selected = epochs.ReadCurrent()!; var before = Snapshot(epochs.Root); var history = Snapshot(files.Root);
        Refused(() => WindowsNativeEpochTransaction.Begin(epochs, packages.Native, Guid.ParseExact(new string('6', 32), "N"),
            explicitlyRecoverCurrent: NativeCanonical.Digest(selected), managedTransfer: transfer, storage: Storage(files)));
        Preserved(epochs.Root, before); Preserved(files.Root, history);
    }
}
