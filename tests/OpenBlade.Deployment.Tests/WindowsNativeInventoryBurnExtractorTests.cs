using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace OpenBlade.Deployment.Tests;

public sealed class WindowsNativeInventoryBurnExtractorTests
{
    private const string FixtureVariable = "OPENBLADE_PHASE_9P_BURN_FIXTURE";
    private const string SignedFixtureVariable = "OPENBLADE_PHASE_9U_SIGNED_BURN_FIXTURE";
    private const string ProductSizeFixtureVariable = "OPENBLADE_PHASE_9V_PRODUCT_SIZE_BURN_FIXTURE";
    private const string FixtureSha256 = "eec934f595a1f35831c3051ea0f45d2c3829547720764e7b38e98690805c4e10";
    private const string SignedFixtureSha256 = "cc01de2afa4b92cf4756d0565eb2baddd1a50344b900f483405badbfa8f5b5b6";
    private const string ProductSizeFixtureSha256 = "65c49b47ab7421f7316f52dd1dde3a257e008b8ae1fbb0d5c74162800e596fda";
    private const string InventorySha256 = "c11e8bd73c0f9926e76f69b9df9af1bb510d4c0fe4413fbe22e3e7964ecdba09";
    private const string CanonicalPath = WindowsNativeRoleInventory.ResourcePath;
    private const int PeOffset = 128;
    private const int Optional = PeOffset + 24;
    private const int BurnOffset = 512;
    private const int StubSize = 1024;
    private const int CertificateDirectory = Optional + 144;
    private const int CertificateSize = 32;
    private static readonly XNamespace Burn = "http://wixtoolset.org/schemas/v4/2008/Burn";
    private static readonly Guid BundleId = new("4cd501f3-23d0-4c58-9a83-ed511e3d2ec5");

    [PinnedBurnFixture(FixtureVariable)]
    public void ReadsTheExactInertWiX7Fixture()
        => ReadPinnedFixture(FixtureVariable, FixtureSha256);

    [PinnedBurnFixture(SignedFixtureVariable)]
    public void ReadsTheExactDetachedAndReattachedSignedWiX7Fixture()
        => ReadPinnedFixture(SignedFixtureVariable, SignedFixtureSha256);

    [PinnedBurnFixture(ProductSizeFixtureVariable)]
    public void ReadsTheExactProductSizeWiX7FixtureWithBoundedManagedAllocations()
    {
        using FileStream snapshot = OpenPinnedFixture(ProductSizeFixtureVariable, ProductSizeFixtureSha256);
        long before = GC.GetAllocatedBytesForCurrentThread();

        AssertPinnedInventory(snapshot);

        // The attached cabinet is 19,641,520 bytes. This leaves ample room for
        // runtime setup while catching a return to a full managed cabinet buffer.
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 12 * 1024 * 1024);
    }

    [PinnedBurnFixture(ProductSizeFixtureVariable)]
    public void CancelsAfterReadingIntoTheProductSizeCabinet()
    {
        using FileStream snapshot = OpenPinnedFixture(ProductSizeFixtureVariable, ProductSizeFixtureSha256);
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancelAfterReadStream(snapshot, snapshot.Length - 1024 * 1024, cancellation);

        var failure = Assert.Throws<OperationCanceledException>(() =>
            WindowsNativeInventoryBurnExtractor.Extract(stream, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.True(snapshot.CanRead);
    }

    [PinnedBurnFixture(ProductSizeFixtureVariable)]
    public void RefusesProductSizeCabinetMutationOutsideTheInventory()
    {
        using FileStream snapshot = OpenPinnedFixture(ProductSizeFixtureVariable, ProductSizeFixtureSha256);
        byte[] bytes = new byte[snapshot.Length];
        snapshot.ReadExactly(bytes);
        // The final block lies past the selected inventory's folder range.
        using var changing = new ChangingSnapshotStream(bytes, truncate: false, bytes.Length - 1);

        var failure = Assert.Throws<InvalidDataException>(() =>
            WindowsNativeInventoryBurnExtractor.Extract(changing, default));
        Assert.Contains("changed during extraction", failure.Message, StringComparison.Ordinal);
    }

    private static void ReadPinnedFixture(string variable, string sha256)
    {
        using FileStream snapshot = OpenPinnedFixture(variable, sha256);
        AssertPinnedInventory(snapshot);
    }

    private static FileStream OpenPinnedFixture(string variable, string sha256)
    {
        FileStream snapshot = File.Open(Environment.GetEnvironmentVariable(variable)!,
            FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(snapshot)));
            snapshot.Position = 0;
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private static void AssertPinnedInventory(Stream snapshot)
    {
        var entry = Assert.Single(WindowsNativeInventoryBurnExtractor.Extract(snapshot, default));
        Assert.Equal(CanonicalPath, entry.FilePath);
        Assert.Equal("OpenBladeSetup", entry.OwnerPackageId);
        Assert.Equal("WixAttachedContainer", entry.AttachedContainerId);
        Assert.Equal(InventorySha256, Convert.ToHexStringLower(SHA256.HashData(entry.Resource.AsSpan())));
        Assert.True(snapshot.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsOnlyTheInventoryFromTheAuthenticatedLease(bool signedLayout)
    {
        byte[] resource = Resource();
        byte[] bundle = Bundle(resource);
        if (signedLayout)
        {
            bundle = WithCertificateTables(bundle);
        }

        using var signed = OpenBladeSignedReleaseVerifierTests.TestRelease.CreateWithInstallerBytes(bundle);
        var verified = OpenBladeSignedReleaseVerifier.Verify(signed.ManifestBytes, signed.SignatureBytes, signed.Policy);
        var selected = new WindowsNativePayloadIdentity(signed.InstallerSha256,
            new(new string('a', 40), new(65535, 1, 2, 3)));
        DirectoryInfo directory = Directory.CreateTempSubdirectory("openblade-burn-fixture-");
        string path = Path.Combine(directory.FullName, OpenBladeReleaseManifestCodec.InstallerFileName);
        try
        {
            File.WriteAllBytes(path, bundle);
            WindowsNativeInventorySnapshot result = await WindowsNativeRoleInventory.ReadVerifiedPackageAsync(
                path, verified, selected, (stream, token) =>
                {
                    Assert.Equal(0, stream.Position);
                    Assert.Throws<IOException>(() => File.OpenWrite(path));
                    return WindowsNativeInventoryBurnExtractor.Extract(stream, token);
                });

            Assert.Equal(selected, result.Identity);
            Assert.Equal(resource, result.Resource.AsSpan().ToArray());

            if (signedLayout)
            {
                // Change an opaque byte in each table without changing any extent or envelope.
                foreach (int offset in new[] { ReadU32(bundle, BurnOffset + 32), ReadU32(bundle, CertificateDirectory) })
                {
                    byte[] changed = (byte[])bundle.Clone();
                    changed[offset + 27] ^= 1;
                    File.WriteAllBytes(path, changed);
                    var failure = await Assert.ThrowsAsync<OpenBladeReleaseVerificationException>(() =>
                        WindowsNativeRoleInventory.ReadVerifiedPackageAsync(path, verified, selected,
                            (_, _) => throw new InvalidOperationException("Extraction preceded authentication.")));
                    Assert.Equal(OpenBladeReleaseVerificationFailure.InstallerHashMismatch, failure.Failure);
                }
            }
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    [Theory]
    [InlineData("case", "canonical and unique")]
    [InlineData("slash", "invalid or aliased")]
    [InlineData("elsewhere", "canonical and unique")]
    [InlineData("duplicate-path", "invalid or aliased")]
    [InlineData("duplicate-id", "invalid or aliased")]
    [InlineData("duplicate-source", "mapping is invalid or ambiguous")]
    [InlineData("missing-reference", "do not reconcile")]
    [InlineData("other-owner", "missing or duplicated")]
    [InlineData("extra-reference", "ambiguous payload reference")]
    [InlineData("ux-shadow", "UX payload mapping")]
    [InlineData("missing-declaration", "do not reconcile")]
    [InlineData("container-hash", "container digest")]
    [InlineData("payload-hash", "payload digest")]
    public void RefusesAmbiguousOrInconsistentLogicalSelections(string mutation, string diagnostic)
    {
        byte[] bundle = Bundle(Resource(), editManifest: root =>
        {
            XElement payload = root.Elements(Burn + "Payload").First();
            XElement setup = root.Elements(Burn + "Payload").Last();
            XElement chain = root.Element(Burn + "Chain")!;
            XElement owner = chain.Element(Burn + "ExePackage")!;
            XElement reference = owner.Elements().First();
            switch (mutation)
            {
                case "case": payload.SetAttributeValue("FilePath", CanonicalPath.ToLowerInvariant()); break;
                case "slash": payload.SetAttributeValue("FilePath", CanonicalPath.Replace('\\', '/')); break;
                case "elsewhere": payload.SetAttributeValue("FilePath", @"other\" + WindowsNativeRoleInventory.ResourceName); break;
                case "duplicate-path": setup.SetAttributeValue("FilePath", CanonicalPath.ToUpperInvariant()); break;
                case "duplicate-id": setup.SetAttributeValue("Id", "inventory"); break;
                case "duplicate-source": setup.SetAttributeValue("SourcePath", "a0"); break;
                case "missing-reference": reference.Remove(); break;
                case "other-owner":
                    chain.Add(new XElement(Burn + "ExePackage", new XAttribute("Id", "Other"),
                        new XElement(reference)));
                    break;
                case "extra-reference": owner.Add(new XElement(reference)); break;
                case "ux-shadow":
                    root.Element(Burn + "UX")!.Element(Burn + "Payload")!
                        .SetAttributeValue("FilePath", @"other\" + WindowsNativeRoleInventory.ResourceName);
                    break;
                case "missing-declaration": setup.Remove(); owner.Elements().Last().Remove(); break;
                case "container-hash": root.Element(Burn + "Container")!.SetAttributeValue("Hash", new string('0', 128)); break;
                case "payload-hash": payload.SetAttributeValue("Hash", new string('0', 128)); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        });

        Reject(bundle, diagnostic);
    }

    [Theory]
    [InlineData("physical-duplicate", "physical member")]
    [InlineData("physical-case", "physical member")]
    [InlineData("physical-overlap", "members overlap")]
    [InlineData("physical-extra", "do not reconcile")]
    [InlineData("expanded-overflow", "expanded size")]
    [InlineData("member-limit", "expanded size")]
    [InlineData("aggregate-limit", "expanded size")]
    [InlineData("compressed-overflow", "cabinet sizes")]
    [InlineData("cabinet-span", "cabinet header")]
    [InlineData("cabinet-folder-offset-overflow", "cabinet folder")]
    [InlineData("cabinet-continuation", "cabinet header")]
    [InlineData("block-length", "blocks do not cover")]
    [InlineData("signed-pe", "certificate ranges")]
    [InlineData("signed-burn", "certificate ranges")]
    [InlineData("extra-container", "one UX and one attached")]
    [InlineData("appended", "exactly cover")]
    [InlineData("truncated", "exactly cover")]
    public void RefusesUnsupportedOrAmbiguousPhysicalLayouts(string mutation, string diagnostic)
    {
        const uint maxMember = 128 * 1024 * 1024;
        byte[] resource = Resource();
        byte[] bundle = Bundle(resource, editCabinet: cabinet =>
        {
            switch (mutation)
            {
                case "physical-duplicate": cabinet[44 + 19 + 17] = (byte)'0'; break;
                case "physical-case": cabinet[44 + 16] = (byte)'A'; break;
                case "physical-overlap": U32(cabinet, 44 + 19 + 4, 0); break;
                case "expanded-overflow": U32(cabinet, 44, uint.MaxValue); break;
                case "member-limit": U32(cabinet, 44, maxMember + 1); break;
                case "aggregate-limit":
                    U32(cabinet, 44, maxMember);
                    U32(cabinet, 44 + 19, maxMember);
                    U32(cabinet, 44 + 19 + 4, maxMember);
                    U32(cabinet, 44 + 38 + 4, 2 * maxMember);
                    break;
                case "cabinet-span": U32(cabinet, 8, (uint)cabinet.Length + 1); break;
                case "cabinet-folder-offset-overflow": U32(cabinet, 36, uint.MaxValue); break;
                case "cabinet-continuation": U16(cabinet, 30, 1); break;
                case "block-length":
                    int dataOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(cabinet.AsSpan(36));
                    ushort shortSize = (ushort)(resource.Length + 2);
                    U16(cabinet, dataOffset + 4, shortSize);
                    U16(cabinet, dataOffset + 6, shortSize);
                    break;
            }
        }, extraMember: mutation is "physical-extra" or "aggregate-limit");

        switch (mutation)
        {
            case "signed-pe":
                U32(bundle, Optional + 144, (uint)bundle.Length);
                U32(bundle, Optional + 148, 8);
                break;
            case "signed-burn":
                U32(bundle, BurnOffset + 32, StubSize);
                U32(bundle, BurnOffset + 36, 8);
                break;
            case "extra-container": U32(bundle, BurnOffset + 44, 3); break;
            case "compressed-overflow": U32(bundle, BurnOffset + 52, uint.MaxValue); break;
            case "appended": bundle = [.. bundle, 0]; break;
            case "truncated": bundle = bundle[..^1]; break;
        }

        Reject(bundle, diagnostic);
    }

    [Theory]
    [InlineData("engine-checksum-missing", "certificate ranges")]
    [InlineData("engine-overlaps-ux", "certificate ranges")]
    [InlineData("engine-size-outside-bound", "certificate ranges")]
    [InlineData("outer-overlaps-attached", "certificate ranges")]
    [InlineData("outer-missing", "certificate ranges")]
    [InlineData("outer-trailing-overlay", "certificate ranges")]
    [InlineData("outer-truncated", "certificate ranges")]
    [InlineData("outer-unaligned-size", "table size is not aligned")]
    [InlineData("engine-padding", "zero alignment padding")]
    [InlineData("outer-padding", "zero alignment padding")]
    [InlineData("engine-record-length", "one WIN_CERTIFICATE")]
    [InlineData("outer-revision", "one WIN_CERTIFICATE")]
    [InlineData("engine-record-type", "one WIN_CERTIFICATE")]
    [InlineData("outer-record-padding", "SignedData envelope or padding")]
    [InlineData("outer-second-record", "one WIN_CERTIFICATE")]
    [InlineData("engine-wrong-content-type", "SignedData envelope or padding")]
    [InlineData("outer-der-length", "malformed or incomplete")]
    public void RefusesInconsistentSignedBoundaries(string mutation, string diagnostic)
    {
        // Make the synthetic UX cabinet end unaligned so both gaps can be exercised.
        byte[] bundle = WithCertificateTables(Bundle(Resource(), editManifest: root => root.Add(new XComment("pad"))));
        int engine = ReadU32(bundle, BurnOffset + 32);
        int outer = ReadU32(bundle, CertificateDirectory);
        switch (mutation)
        {
            case "engine-checksum-missing": U32(bundle, BurnOffset + 28, 0); break;
            case "engine-overlaps-ux": U32(bundle, BurnOffset + 32, (uint)engine - 8); break;
            case "engine-size-outside-bound": U32(bundle, BurnOffset + 36, uint.MaxValue); break;
            case "outer-overlaps-attached": U32(bundle, CertificateDirectory, (uint)outer - 8); break;
            case "outer-missing": bundle.AsSpan(CertificateDirectory, 8).Clear(); break;
            case "outer-trailing-overlay": bundle = [.. bundle, 0]; break;
            case "outer-truncated": bundle = bundle[..^1]; break;
            case "outer-unaligned-size":
                bundle = [.. bundle, 0];
                U32(bundle, CertificateDirectory + 4, CertificateSize + 1);
                break;
            case "engine-padding":
                Assert.True(engine > StubSize + ReadU32(bundle, BurnOffset + 48));
                bundle[engine - 1] = 1;
                break;
            case "outer-padding":
                Assert.True(outer > engine + CertificateSize + ReadU32(bundle, BurnOffset + 52));
                bundle[outer - 1] = 1;
                break;
            case "engine-record-length": U32(bundle, engine, CertificateSize - 8); break;
            case "outer-revision": U16(bundle, outer + 4, 0x0100); break;
            case "engine-record-type": U16(bundle, engine + 6, 1); break;
            case "outer-record-padding": bundle[^1] = 1; break;
            case "outer-second-record":
                bundle = [.. bundle, .. bundle.AsSpan(outer, CertificateSize)];
                U32(bundle, CertificateDirectory + 4, 2 * CertificateSize);
                break;
            case "engine-wrong-content-type": bundle[engine + 20] = 1; break;
            case "outer-der-length": bundle[outer + 9] = 127; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Reject(bundle, diagnostic);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RefusesAChangedSnapshotAndHonorsCancellation(bool signedLayout, bool truncate)
    {
        byte[] bundle = Bundle(Resource());
        if (signedLayout)
        {
            bundle = WithCertificateTables(bundle);
        }

        int changedOffset = signedLayout ? ReadU32(bundle, CertificateDirectory) + 27 : bundle.Length - 1;
        using var changing = new ChangingSnapshotStream(bundle, truncate, changedOffset);
        var failure = Assert.Throws<InvalidDataException>(() =>
            WindowsNativeInventoryBurnExtractor.Extract(changing, default));
        Assert.Contains("changed during extraction", failure.Message, StringComparison.Ordinal);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var stream = new MemoryStream(bundle);
        Assert.Throws<OperationCanceledException>(() =>
            WindowsNativeInventoryBurnExtractor.Extract(stream, cancelled.Token));
    }

    private static void Reject(byte[] bundle, string diagnostic)
    {
        using var stream = new MemoryStream(bundle, writable: false);
        var failure = Assert.Throws<InvalidDataException>(() =>
            WindowsNativeInventoryBurnExtractor.Extract(stream, default));
        Assert.Contains(diagnostic, failure.Message, StringComparison.Ordinal);
    }

    private static byte[] Resource() =>
        [.. "OpenBlade.WindowsNativeRoles\0v1\0win-x64\0"u8,
         .. Convert.FromHexString(new string('a', 40)),
         255, 255, 1, 0, 2, 0, 3, 0,
         .. SHA256.HashData("service"u8), .. SHA256.HashData("agent"u8)];

    private static byte[] Bundle(byte[] resource, Action<XElement>? editManifest = null,
        Action<byte[]>? editCabinet = null, bool extraMember = false)
    {
        byte[] setup = "abc"u8.ToArray();
        byte[] attached = extraMember
            ? Cabinet(("a0", resource), ("a1", setup), ("a2", [1]))
            : Cabinet(("a0", resource), ("a1", setup));
        editCabinet?.Invoke(attached);

        var root = new XElement(Burn + "BurnManifest",
            new XAttribute("EngineVersion", "7.0.0.0"), new XAttribute("ProtocolVersion", "1"),
            new XAttribute("Win64", "yes"),
            new XElement(Burn + "UX", new XElement(Burn + "Payload", new XAttribute("Id", "UX1"),
                new XAttribute("FilePath", "FixtureBA.exe"), new XAttribute("SourcePath", "u0"))),
            new XElement(Burn + "Container", new XAttribute("Id", "WixAttachedContainer"),
                new XAttribute("AttachedIndex", "1"), new XAttribute("Attached", "yes"), new XAttribute("Primary", "yes"),
                Size(attached), Hash(attached)),
            Payload("Inventory", CanonicalPath, "a0", resource), Payload("OpenBladeSetup", "Setup.exe", "a1", setup),
            new XElement(Burn + "Registration", new XAttribute("Code", BundleId.ToString("B"))),
            new XElement(Burn + "Chain", new XElement(Burn + "ExePackage", new XAttribute("Id", "OpenBladeSetup"),
                new XElement(Burn + "PayloadRef", new XAttribute("Id", "Inventory")),
                new XElement(Burn + "PayloadRef", new XAttribute("Id", "OpenBladeSetup")))));
        editManifest?.Invoke(root);
        byte[] ux = Cabinet(("0", Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting))), ("u0", [1]));

        // Synthetic PE header with a single raw .wixburn section and no executable code.
        byte[] stub = new byte[StubSize];
        U16(stub, 0, 0x5A4D);
        U32(stub, 60, PeOffset);
        "PE\0\0"u8.CopyTo(stub.AsSpan(PeOffset));
        U16(stub, PeOffset + 4, 0x8664);
        U16(stub, PeOffset + 6, 1);
        U16(stub, PeOffset + 20, 240);
        U16(stub, PeOffset + 22, 2);
        U16(stub, Optional, 0x20B);
        U32(stub, Optional + 60, BurnOffset);
        U32(stub, Optional + 108, 16);
        ".wixburn"u8.CopyTo(stub.AsSpan(Optional + 240));
        U32(stub, Optional + 240 + 16, 512);
        U32(stub, Optional + 240 + 20, BurnOffset);
        U32(stub, BurnOffset, 0x00F14300);
        U32(stub, BurnOffset + 4, 2);
        BundleId.ToByteArray().CopyTo(stub, BurnOffset + 8);
        U32(stub, BurnOffset + 24, StubSize);
        U32(stub, BurnOffset + 40, 1);
        U32(stub, BurnOffset + 44, 2);
        U32(stub, BurnOffset + 48, (uint)ux.Length);
        U32(stub, BurnOffset + 52, (uint)attached.Length);
        return [.. stub, .. ux, .. attached];
    }

    private static XElement Payload(string id, string path, string source, byte[] bytes) =>
        new(Burn + "Payload", new XAttribute("Id", id), new XAttribute("FilePath", path),
            new XAttribute("SourcePath", source), new XAttribute("Container", "WixAttachedContainer"),
            new XAttribute("Packaging", "embedded"), Size(bytes), Hash(bytes));

    private static byte[] WithCertificateTables(byte[] unsigned)
    {
        int uxEnd = StubSize + ReadU32(unsigned, BurnOffset + 48);
        int engine = (uxEnd + 7) & ~7;
        int attached = engine + CertificateSize;
        int outer = (attached + (unsigned.Length - uxEnd) + 7) & ~7;
        byte[] bundle = new byte[outer + CertificateSize];
        unsigned.AsSpan(0, uxEnd).CopyTo(bundle);
        unsigned.AsSpan(uxEnd).CopyTo(bundle.AsSpan(attached));
        U32(bundle, Optional + 64, 2);
        U32(bundle, BurnOffset + 28, 1);
        U32(bundle, BurnOffset + 32, (uint)engine);
        U32(bundle, BurnOffset + 36, CertificateSize);
        U32(bundle, CertificateDirectory, (uint)outer);
        U32(bundle, CertificateDirectory + 4, CertificateSize);
        foreach (int offset in new[] { engine, outer })
        {
            U32(bundle, offset, CertificateSize);
            U16(bundle, offset + 4, 0x0200);
            U16(bundle, offset + 6, 0x0002);
            // Only the SignedData framing is needed. These bytes carry no signature.
            Convert.FromHexString("301206092A864886F70D010702A0053003020101").CopyTo(bundle, offset + 8);
        }

        return bundle;
    }

    private static XAttribute Size(byte[] bytes) => new("FileSize", bytes.Length);
    private static XAttribute Hash(byte[] bytes) => new("Hash", Convert.ToHexString(SHA512.HashData(bytes)));

    private static byte[] Cabinet(params (string Name, byte[] Bytes)[] members)
    {
        int dataOffset = 44 + members.Sum(member => 17 + member.Name.Length);
        int length = members.Sum(member => member.Bytes.Length);
        byte[] cabinet = new byte[dataOffset + 8 + length];
        "MSCF"u8.CopyTo(cabinet);
        U32(cabinet, 8, (uint)cabinet.Length);
        U32(cabinet, 16, 44);
        cabinet[24] = 3;
        cabinet[25] = 1;
        U16(cabinet, 26, 1);
        U16(cabinet, 28, (ushort)members.Length);
        U32(cabinet, 36, (uint)dataOffset);
        U16(cabinet, 40, 1);
        U16(cabinet, dataOffset + 4, (ushort)length);
        U16(cabinet, dataOffset + 6, (ushort)length);

        int cursor = 44;
        int offset = 0;
        foreach ((string name, byte[] bytes) in members)
        {
            U32(cabinet, cursor, (uint)bytes.Length);
            U32(cabinet, cursor + 4, (uint)offset);
            U16(cabinet, cursor + 14, 0x20);
            Encoding.ASCII.GetBytes(name).CopyTo(cabinet, cursor + 16);
            cursor += 17 + name.Length;
            bytes.CopyTo(cabinet, dataOffset + 8 + offset);
            offset += bytes.Length;
        }

        return cabinet;
    }

    private static void U16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void U32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static int ReadU32(byte[] bytes, int offset) =>
        checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));

    private sealed class CancelAfterReadStream(Stream snapshot, long offset, CancellationTokenSource cancellation)
        : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => snapshot.Length;
        public override long Position { get => snapshot.Position; set => snapshot.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => snapshot.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read = snapshot.Read(buffer);
            if (read > 0 && Position > offset)
            {
                cancellation.Cancel();
            }

            return read;
        }
    }

    private sealed class ChangingSnapshotStream(byte[] bytes, bool truncate, int changedOffset)
        : MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true)
    {
        private bool changed;
        public override long Length => changed && truncate ? base.Length - 1 : base.Length;

        public override int Read(Span<byte> buffer)
        {
            int read = base.Read(buffer);
            if (!changed && Position == base.Length)
            {
                changed = true;
                if (!truncate)
                {
                    GetBuffer()[changedOffset] ^= 1;
                }
            }

            return read;
        }
    }

    private sealed class PinnedBurnFixtureAttribute : FactAttribute
    {
        public PinnedBurnFixtureAttribute(string variable)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
            {
                Skip = $"Set {variable} to its pinned fixture path.";
            }
        }
    }
}
