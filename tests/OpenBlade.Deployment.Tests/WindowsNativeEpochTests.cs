using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Deployment.Tests;

// This exact-name class is an offline allowlist: algorithmic crypto, inert PE/CAB
// bytes, and private current-token filesystem only. No product/child/IPC/SCM calls.
[SupportedOSPlatform("windows")]
public sealed class WindowsNativeEpochTests
{
    private const string Features = "installed-service-candidate,native-agent-entry-compile-check";

    internal static byte[] Image(string role, char nonce = 'b', byte marker = 1)
    {
        byte[] bytes = new byte[0xA00];
        void U16(int n, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(n), v);
        void U32(int n, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(n), v);
        "MZ"u8.CopyTo(bytes); U32(60, 0x80); "PE\0\0"u8.CopyTo(bytes.AsSpan(0x80));
        U16(0x84, 0x8664); U16(0x86, 2); U16(0x94, 240); U16(0x96, 2); U16(0x98, 0x20b);
        U32(0x98 + 32, 0x1000); U32(0x98 + 36, 0x200); U32(0x98 + 56, 0x3000); U32(0x98 + 60, 0x400); U32(0x98 + 108, 16);
        U32(0x98 + 120, 0x2000); U32(0x98 + 124, 40);
        byte[] metadata = NativeCanonical.Lines("OpenBlade.NativeArtifact.v1", new string(nonce, 64) + "-" + role,
            role, "x86_64-pc-windows-msvc", new string('a', 40), Features, "rustc 1.99.1 (inert test data)", "debug");
        int section = 0x98 + 240;
        ".obid"u8.CopyTo(bytes.AsSpan(section)); U32(section + 8, (uint)metadata.Length); U32(section + 12, 0x1000); U32(section + 16, 0x400); U32(section + 20, 0x400); U32(section + 36, 0x40000040);
        ".idata"u8.CopyTo(bytes.AsSpan(section + 40)); U32(section + 48, 0x200); U32(section + 52, 0x2000); U32(section + 56, 0x200); U32(section + 60, 0x800); U32(section + 76, 0x40000040);
        metadata.CopyTo(bytes, 0x400); U32(0x800 + 12, 0x2040); "KERNEL32.dll\0"u8.CopyTo(bytes.AsSpan(0x840)); bytes[^1] = marker;
        return bytes;
    }

    internal static async Task<WindowsNativePackage> Package(WindowsNativeEpochFiles files, char nonce = 'b', byte marker = 1)
    {
        byte[] bytes = WindowsNativeInventoryBurnExtractorTests.NativeEpochBundle(Image("service", nonce, marker), Image("agent", nonce, 2));
        using var signed = OpenBladeSignedReleaseVerifierTests.TestRelease.CreateWithInstallerBytes(bytes, new string('a', 40));
        string path = Path.Combine(files.Root, Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, bytes);
        var held = files.OpenFile(path);
        var release = OpenBladeSignedReleaseVerifier.Verify(signed.ManifestBytes, signed.SignatureBytes, signed.Policy);
        return await WindowsNativePackage.OpenLocalDevAsync(held, new EmptyLease(), signed.ManifestBytes,
            signed.SignatureBytes, NativeLocalDevEnrollment.ForAlgorithmTests(release.Manifest.InstallationId, signed.Policy), CancellationToken.None);
    }

    [Fact]
    public void CompiledIdentityBindsRoleTargetFeaturesAndFullDigest()
    {
        byte[] bytes = Image("service");
        var artifact = NativeArtifact.Inspect([.. bytes], "service", new string('a', 40));
        Assert.EndsWith("-service", artifact.Id);
        Assert.Equal("kernel32.dll", artifact.Imports);
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. bytes], "agent", new string('a', 40)));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. bytes], "service", new string('c', 40)));
        bytes[^1] ^= 1;
        Assert.NotEqual(artifact.Sha256, NativeArtifact.Inspect([.. bytes], "service", new string('a', 40)).Sha256);
    }

    [Fact]
    public void SameSourceBuildsHaveDistinctArtifactIdentities()
    {
        var a = NativeArtifact.Inspect([.. Image("service", 'b')], "service", new string('a', 40));
        var b = NativeArtifact.Inspect([.. Image("service", 'c')], "service", new string('a', 40));
        Assert.NotEqual(a.Id, b.Id); Assert.NotEqual(a.Sha256, b.Sha256);
    }

    [Fact]
    public void UnboundIdentityAndAdditionalLoaderContractsDeny()
    {
        byte[] bytes = Image("service");
        bytes[0x400] ^= 1;
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. bytes], "service", new string('a', 40)));
        bytes = Image("service");
        "foreignx.dll\0"u8.CopyTo(bytes.AsSpan(0x840));
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. bytes], "service", new string('a', 40)));
        bytes = Image("service"); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + 216), 1);
        Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. bytes], "service", new string('a', 40)));
    }

    [Fact]
    public async Task RealAuthenticatedPackageYieldsArtifactAndClosureCommitment()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        var f = NativeCanonical.ParseLines(package.Record, "OpenBlade.NativePackage.v1", 12);
        Assert.Equal("LocalDev", f[0]); Assert.Equal(package.Release.Manifest.InstallerSha256, f[2]);
        Assert.Equal(package.Service.Encode(), Convert.FromBase64String(f[8]));
        Assert.Equal(package.Agent.Encode(), Convert.FromBase64String(f[9]));
        Assert.Equal("two-executables-system-runtime-imports-v1", f[10]);
    }

    [Fact]
    public async Task ProvisionalOwnerLossPersistsDeniedPhaseAndRequiresExplicitRecovery()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        string epoch;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid())) { epoch = tx.Record.Epoch; }
        var record = NativeEpochRecord.Parse(files.ReadCurrent()!);
        Assert.Equal(epoch, record.Epoch); Assert.Equal(NativeEpochPhase.Provisional, record.Phase);
        Assert.Throws<InvalidDataException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()));
        WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, NativeCanonical.Digest(files.ReadCurrent()!));
        Assert.Equal(NativeEpochPhase.Unresolved, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
    }

    [Fact]
    public async Task CommitRetireAndIdenticalByteRollbackUseFreshEpochAndPredecessor()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        string prior; byte[] committed;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            prior = tx.Record.Epoch;
            Assert.Throws<InvalidDataException>(() => tx.CommitAfterBoundReadiness(new string('0', 64)));
            tx.CommitAfterBoundReadiness(new string('e', 64)); committed = files.ReadCurrent()!;
            Assert.Equal(NativeEpochPhase.Committed, NativeEpochRecord.Parse(committed).Phase);
            tx.RetirePackageSelection();
        }
        byte[] retired = files.ReadCurrent()!;
        using var rollback = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid());
        Assert.NotEqual(prior, rollback.Record.Epoch); Assert.Equal(2, rollback.Record.Ordinal);
        Assert.Equal(NativeCanonical.Digest(retired), rollback.Record.Predecessor);
        Assert.Equal(NativeEpochRecord.Parse(committed).Package, rollback.Record.Package);
    }

    [Fact]
    public async Task ConflictingArtifactMappingDeniesEvenAcrossAbandonedPreparations()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var first = await Package(files);
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(files, first, Guid.NewGuid(), testCrashBoundary:
            reached => { if (reached == NativeEpochBoundary.PackageWritten) { throw new IOException("abandoned preparation"); } }));
        using var conflicting = await Package(files, marker: 9);
        Assert.Throws<InvalidDataException>(() => WindowsNativeEpochTransaction.Begin(files, conflicting, Guid.NewGuid()));
        Assert.Single(Directory.EnumerateFiles(files.Root, "package.bin", SearchOption.AllDirectories));
        using var retriedOriginal = WindowsNativeEpochTransaction.Begin(files, first, Guid.NewGuid());
        Assert.Equal(1, retriedOriginal.Record.Ordinal);
    }

    [Theory]
    [InlineData((int)NativeEpochBoundary.Reserved)]
    [InlineData((int)NativeEpochBoundary.PackageWritten)]
    [InlineData((int)NativeEpochBoundary.OriginWritten)]
    [InlineData((int)NativeEpochBoundary.PhaseWritten)]
    [InlineData((int)NativeEpochBoundary.FrontierWritten)]
    [InlineData((int)NativeEpochBoundary.SelectionPublished)]
    public async Task EveryPreparationCrashBoundaryPreservesEvidenceWithoutReplay(int injectedBoundary)
    {
        NativeEpochBoundary boundary = (NativeEpochBoundary)injectedBoundary;
        await CheckBoundary(boundary);
    }

    private static async Task CheckBoundary(NativeEpochBoundary boundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid(), testCrashBoundary:
            reached => { if (reached == boundary) { throw new IOException("injected crash"); } }));
        var reservations = Directory.EnumerateFiles(files.Root, "*.reserved").ToArray(); Assert.Single(reservations);
        if (boundary == NativeEpochBoundary.SelectionPublished)
        {
            Assert.Equal(NativeEpochPhase.Provisional, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
            Assert.Throws<InvalidDataException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()));
        }
        else
        {
            Assert.Null(files.ReadCurrent());
            if (boundary == NativeEpochBoundary.FrontierWritten)
            { Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid())); }
        }
    }

    [Fact]
    public async Task ReplayedSelectionAndInterruptedFrontierDenyBeforeNewPublication()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        byte[] provisional;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        { provisional = files.ReadCurrent()!; tx.CommitAfterBoundReadiness(new string('e', 64)); }
        files.PublishCurrent(provisional);
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()));
        string latest = Directory.EnumerateFiles(files.Root, "selection-*.bin").Order(StringComparer.Ordinal).Last();
        string frontier = NativeCanonical.Digest(File.ReadAllBytes(latest));
        WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, frontier);
        byte[] unresolved = files.ReadCurrent()!;
        using var recovery = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid(), NativeCanonical.Digest(unresolved));
        Assert.Equal(2, recovery.Record.Ordinal);
    }

    [Fact]
    public async Task ImmutableFilesRemainWriteAndDeleteExcludedThroughTransaction()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        using var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid());
        Assert.Throws<IOException>(() => File.Open(tx.ServicePath, FileMode.Open, FileAccess.Write, FileShare.Read));
        Assert.Throws<IOException>(() => File.Delete(tx.ServicePath));
        Assert.Throws<IOException>(() => Directory.Move(tx.GenerationDirectory, tx.GenerationDirectory + "-moved"));
    }

    [Fact]
    public void CrossLanguageCanonicalEpochVectorIsExactAndRejectsTrailingOrPhaseMutation()
    {
        using var vector = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-package-v1.json")));
        byte[] bytes = Convert.FromBase64String(vector.RootElement.GetProperty("epoch").GetString()!);
        var record = NativeEpochRecord.Parse(bytes);
        Assert.Equal(bytes, record.Encode()); Assert.Equal(NativeEpochPhase.Committed, record.Phase);
        Assert.Throws<InvalidDataException>(() => NativeEpochRecord.Parse([.. bytes, 10]));
        Assert.Throws<InvalidDataException>(() => NativeEpochRecord.Parse(Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(bytes).Replace("Committed", "Provisional", StringComparison.Ordinal))));
    }

    [Fact]
    public void CrossLanguageSignatureAndArtifactVectorUseActualCSharpContracts()
    {
        using var vector = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-package-v1.json")));
        byte[] Bytes(string name) => Convert.FromBase64String(vector.RootElement.GetProperty(name).GetString()!);
        byte[] spki = Bytes("spki");
        var policy = new OpenBladeReleaseTrustPolicy(true, [new(NativeCanonical.Digest(spki), Convert.ToBase64String(spki))]);
        var release = OpenBladeSignedReleaseVerifier.Verify(Bytes("manifest"), Bytes("signature"), policy);
        Assert.Equal("LocalDev", release.Manifest.Channel);
        Assert.Throws<OpenBladeReleaseVerificationException>(() => OpenBladeSignedReleaseVerifier.Verify(Bytes("manifest"), Bytes("wrongDomain"), policy));
        Assert.Equal(Bytes("serviceRecord"), NativeArtifact.Inspect([.. Bytes("serviceImage")], "service", release.Manifest.SourceRevision).Encode());
        Assert.Equal(Bytes("agentRecord"), NativeArtifact.Inspect([.. Bytes("agentImage")], "agent", release.Manifest.SourceRevision).Encode());
    }

    [Theory]
    [InlineData((int)NativeEpochBoundary.PhaseWritten)]
    [InlineData((int)NativeEpochBoundary.FrontierWritten)]
    [InlineData((int)NativeEpochBoundary.SelectionPublished)]
    public async Task EveryCommitCrashBoundaryHasIndependentRecoveryMeaning(int injectedBoundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        using var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid());
        Assert.Throws<IOException>(() => tx.CommitAfterBoundReadiness(new string('e', 64), reached =>
        { if ((int)reached == injectedBoundary) { throw new IOException("injected commit crash"); } }));
        var current = NativeEpochRecord.Parse(files.ReadCurrent()!);
        Assert.Equal(injectedBoundary == (int)NativeEpochBoundary.SelectionPublished ? NativeEpochPhase.Committed : NativeEpochPhase.Provisional, current.Phase);
        Assert.Throws<IOException>(() => tx.RequireProvisional());
    }

    [Fact]
    public void CompiledNativeBinariesExposeMetadataWhenReadOnlyDataInputsAreSupplied()
    {
        string? service = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_SERVICE_DATA");
        string? agent = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_AGENT_DATA");
        if (service is null || agent is null) { return; }
        string source = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_SOURCE") ?? throw new InvalidDataException();
        var a = NativeArtifact.Inspect([.. File.ReadAllBytes(service)], "service", source);
        var b = NativeArtifact.Inspect([.. File.ReadAllBytes(agent)], "agent", source);
        Assert.NotEqual(a.Id, b.Id); Assert.NotEqual(a.Sha256, b.Sha256);
    }

    [Theory]
    [InlineData((int)NativeEpochBoundary.PhaseWritten)]
    [InlineData((int)NativeEpochBoundary.FrontierWritten)]
    [InlineData((int)NativeEpochBoundary.SelectionPublished)]
    public async Task ExplicitRecoveryReconcilesEachCrashBoundaryWithoutReplayingNativeWork(int injectedBoundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid())) { }
        string Frontier() => NativeCanonical.Digest(File.ReadAllBytes(Directory.EnumerateFiles(files.Root, "selection-*.bin").Order(StringComparer.Ordinal).Last()));
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, Frontier(), reached =>
        { if ((int)reached == injectedBoundary) { throw new IOException("injected recovery crash"); } }));
        WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, Frontier());
        Assert.Equal(NativeEpochPhase.Unresolved, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
        using var fresh = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid(), NativeCanonical.Digest(files.ReadCurrent()!));
        Assert.Equal(2, fresh.Record.Ordinal);
    }

    [Theory]
    [InlineData((int)NativeFileBoundary.Created)]
    [InlineData((int)NativeFileBoundary.Written)]
    [InlineData((int)NativeFileBoundary.Flushed)]
    [InlineData((int)NativeFileBoundary.Verified)]
    [InlineData((int)NativeFileBoundary.Renamed)]
    public async Task AtomicPackageRecordCannotPoisonFutureEpochsAtAnyFilePublicationBoundary(int injectedBoundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        files.CrashBoundaryForTests = (NativeFileBoundary)injectedBoundary;
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()));
        foreach (string record in Directory.EnumerateFiles(files.Root, "package.bin", SearchOption.AllDirectories))
        { Assert.Equal(package.Record, File.ReadAllBytes(record)); }
        files.CrashBoundaryForTests = null;
        // A real crash may leave a partial temporary file. It is not a mapping,
        // phase or selection and must not poison the durable artifact ledger.
        string abandoned = Directory.EnumerateDirectories(files.Root).Single();
        File.WriteAllBytes(Path.Combine(abandoned, "receipt-abandoned.tmp"), [1, 2]);
        using var fresh = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid());
        Assert.Equal(1, fresh.Record.Ordinal);
    }

    [Theory]
    [InlineData((int)NativeEpochBoundary.PhaseWritten)]
    [InlineData((int)NativeEpochBoundary.FrontierWritten)]
    [InlineData((int)NativeEpochBoundary.SelectionPublished)]
    public async Task InterruptedRetirementAndEveryRecoveryBoundaryAreIdempotent(int retirementBoundary)
    {
        foreach (var recoveryBoundary in new[] { NativeEpochBoundary.PhaseWritten, NativeEpochBoundary.FrontierWritten, NativeEpochBoundary.SelectionPublished })
        {
            using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
            using var package = await Package(files);
            string epoch;
            using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
            {
                epoch = tx.Record.Epoch;
                tx.CommitAfterBoundReadiness(new string('e', 64));
                Assert.Throws<IOException>(() => tx.RetirePackageSelection(reached =>
                { if ((int)reached == retirementBoundary) { throw new IOException("retirement interrupted"); } }));
                Assert.Throws<IOException>(() => tx.RequireProvisional());
            }
            string Frontier() => NativeCanonical.Digest(File.ReadAllBytes(Directory.EnumerateFiles(files.Root, "selection-*.bin").Order(StringComparer.Ordinal).Last()));
            Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, Frontier(), reached =>
            { if (reached == recoveryBoundary) { throw new IOException("recovery interrupted"); } }));
            WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, Frontier());
            byte[] retired = files.ReadCurrent()!;
            WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, Frontier());
            Assert.Equal(retired, files.ReadCurrent());
            Assert.Equal(NativeEpochPhase.Retired, NativeEpochRecord.Parse(retired).Phase);
            using var fresh = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid());
            Assert.NotEqual(epoch, fresh.Record.Epoch);
            Assert.Equal(2, fresh.Record.Ordinal);
            Assert.Equal(NativeCanonical.Digest(retired), fresh.Record.Predecessor);
        }
    }

    [Fact]
    public async Task HeldDeleteSharingCurrentKeepsOldBytesButFreshSelectionAndFrontierRejectOldGeneration()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        using var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid());
        tx.CommitAfterBoundReadiness(new string('e', 64));
        byte[] committed = files.ReadCurrent()!;
        // Same NT access/share/options as native_package::open_file(current,true).
        using var old = OpenNativeCurrent(Path.Combine(files.Root, "current.bin"));
        tx.RetirePackageSelection();
        byte[] retired = files.ReadCurrent()!;
        Assert.Equal(committed, WindowsNativeEpochFiles.ReadBounded(old, 16384));
        Assert.NotEqual(committed, retired);
        string frontier = Directory.EnumerateFiles(files.Root, "selection-*.bin").Order(StringComparer.Ordinal).Last();
        Assert.Equal(retired, File.ReadAllBytes(frontier));
        // Actual transaction admission reopens the current path and frontier;
        // retaining/replaying the old committed object cannot select it again.
        tx.Dispose();
        files.PublishCurrent(committed);
        Assert.Throws<IOException>(() => WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()));
        WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, NativeCanonical.Digest(retired));
        Assert.Equal(retired, files.ReadCurrent());
    }

    [Theory]
    [InlineData((int)NativeFileBoundary.Created)]
    [InlineData((int)NativeFileBoundary.Written)]
    [InlineData((int)NativeFileBoundary.Flushed)]
    [InlineData((int)NativeFileBoundary.Verified)]
    [InlineData((int)NativeFileBoundary.Renamed)]
    public async Task RetirementFileFailureRecoversOnlyDurableIntent(int boundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await Package(files);
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            tx.CommitAfterBoundReadiness(new string('e', 64));
            files.CrashBoundaryForTests = (NativeFileBoundary)boundary;
            Assert.Throws<IOException>(() => tx.RetirePackageSelection());
            Assert.Throws<IOException>(() => tx.RequireProvisional());
        }
        files.CrashBoundaryForTests = null;
        string Frontier() => NativeCanonical.Digest(File.ReadAllBytes(Directory.EnumerateFiles(files.Root, "selection-*.bin").Order(StringComparer.Ordinal).Last()));
        WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, Frontier());
        var phase = NativeEpochRecord.Parse(files.ReadCurrent()!).Phase;
        Assert.Equal(boundary == (int)NativeFileBoundary.Renamed ? NativeEpochPhase.Retired : NativeEpochPhase.Unresolved, phase);
        byte[] recovered = files.ReadCurrent()!;
        WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, Frontier());
        Assert.Equal(recovered, files.ReadCurrent());
    }

    [Fact]
    public async Task RejectedConflictCannotPoisonCommittedOriginalAndItsFreshEpoch()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var first = await Package(files);
        using (var tx = WindowsNativeEpochTransaction.Begin(files, first, Guid.NewGuid()))
        { tx.CommitAfterBoundReadiness(new string('e', 64)); }
        using var conflict = await Package(files, marker: 9);
        byte[] committed = files.ReadCurrent()!;
        Assert.Throws<InvalidDataException>(() => WindowsNativeEpochTransaction.Begin(files, conflict, Guid.NewGuid()));
        Assert.Equal(committed, files.ReadCurrent());
        Assert.Single(Directory.EnumerateFiles(files.Root, "package.bin", SearchOption.AllDirectories));
        using var retried = WindowsNativeEpochTransaction.Begin(files, first, Guid.NewGuid());
        Assert.NotEqual(NativeEpochRecord.Parse(committed).Epoch, retried.Record.Epoch);
        Assert.Equal(2, retried.Record.Ordinal);
    }

    private static FileStream OpenNativeCurrent(string path)
    {
        var handle = CreateFileW(path, 0x00120089, 5, 0, 3, 0x00200000, 0);
        if (handle.IsInvalid) { throw new IOException("Exact native reader open failed.", new System.ComponentModel.Win32Exception()); }
        return new FileStream(handle, FileAccess.Read);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share,
        nint security, uint creation, uint flags, nint template);

    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
}
