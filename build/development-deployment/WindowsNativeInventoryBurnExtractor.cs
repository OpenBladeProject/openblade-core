using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Formats.Asn1;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace OpenBlade.Deployment;

internal enum WindowsNativeBurnProfile
{
    AuthoredOpenBlade,
    SinglePackageFixture,
}

// Bounded WiX 7.0.0 unsigned and detach/sign/reattach/sign layouts. The caller owns the authenticated,
// write-excluding snapshot lease throughout this call. This adapter grants no trust.
internal static class WindowsNativeInventoryBurnExtractor
{
    private const string Setup = "OpenBladeSetup";
    private const string VcRuntime = "OpenBladeVcRuntime";
    private const string DotNetRuntime = "OpenBladeDotNetRuntime";
    private const string Attached = "WixAttachedContainer";
    private const int MiB = 1024 * 1024;
    private const int MaxStub = 8 * MiB;
    private const int MaxUxCabinet = 2 * MiB;
    // The inert probe uses 18.73 MiB compressed and 71.27 MiB expanded.
    // Allow headroom over the observed 108.05 MiB installed payload while staying
    // well below the 1 GiB release artifact cap. The member cap matches the
    // deployment closure's per-file bound.
    private const int MaxAttachedCabinet = 128 * MiB;
    private const int MaxAttachedExpanded = 256 * MiB;
    private const int MaxAttachedMember = 128 * MiB;
    private const int MaxExternalPrerequisite = 1024 * MiB;
    private const int MaxUxExpanded = 4 * MiB;
    private const int MaxManifest = MiB;
    private const int MaxCertificateTable = MiB;
    private const int CertificateAlignment = 8;
    private const int CertificateHeaderSize = 8;
    private static readonly XNamespace Burn = "http://wixtoolset.org/schemas/v4/2008/Burn";

    internal static ImmutableArray<WindowsNativeInventoryEntry> Extract(
        Stream snapshot, CancellationToken cancellationToken) =>
        [ExtractCore(snapshot, requireRoles: false, WindowsNativeBurnProfile.SinglePackageFixture, null,
            cancellationToken).Inventory];

    internal static RoleExtraction ExtractRoles(Stream snapshot, WindowsNativeBurnProfile profile,
        string expectedVersion, CancellationToken cancellationToken)
    {
        var result = ExtractCore(snapshot, requireRoles: true, profile, expectedVersion, cancellationToken);
        return new(result.Inventory.Resource, result.Service!, result.SessionAgent!);
    }

    // Whole-installer authentication belongs to the caller. Reuse the same
    // physical/declaration/ownership reconciliation without requiring Rust roles.
    internal static ImmutableArray<ManagedDeclaration> ReadManagedDeclarations(Stream snapshot,
        string expectedVersion, CancellationToken cancellationToken)
    {
        var declarations = new List<ManagedDeclaration>();
        _ = ExtractCore(snapshot, false, WindowsNativeBurnProfile.AuthoredOpenBlade, expectedVersion,
            cancellationToken, declarations);
        if (!declarations.Any(p => p.Path == "OpenBlade.Service.exe")
            || !declarations.Any(p => p.Path == "OpenBlade.Service.dll")
            || !declarations.Any(p => p.Path == "OpenBlade.Service.deps.json")
            || !declarations.Any(p => p.Path == "OpenBlade.Service.runtimeconfig.json"))
        { throw Invalid("The signed managed upgrade has no complete Service load closure."); }
        return [.. declarations.OrderBy(p => p.Path, StringComparer.Ordinal)];
    }

    private static (WindowsNativeInventoryEntry Inventory, RoleMember? Service, RoleMember? SessionAgent) ExtractCore(
        Stream snapshot, bool requireRoles, WindowsNativeBurnProfile profile, string? expectedVersion,
        CancellationToken cancellationToken, List<ManagedDeclaration>? managed = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Burn fixture extraction requires Windows Cabinet FDI.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!snapshot.CanRead || !snapshot.CanSeek)
        {
            throw Invalid("A readable, seekable Burn snapshot is required.");
        }

        try
        {
            bool authored = profile switch
            {
                WindowsNativeBurnProfile.AuthoredOpenBlade => true,
                WindowsNativeBurnProfile.SinglePackageFixture => false,
                _ => throw new ArgumentOutOfRangeException(nameof(profile)),
            };
            long length = snapshot.Length;
            if (length < 64 || length > MaxStub + MaxUxCabinet + MaxAttachedCabinet
                + 2 * (MaxCertificateTable + CertificateAlignment - 1))
            {
                throw Invalid("The Burn snapshot length is outside the fixture bounds.");
            }

            Layout layout = ReadLayout(snapshot, length, cancellationToken);
            // The caller's write-excluding lease covers all passes over these ranges.
            // Record their starting digests so persistent changes fail closed.
            byte[] uxHash = HashAt(snapshot, layout.Stub, layout.UxSize, cancellationToken);
            byte[] attachedHash = HashAt(snapshot, layout.AttachedOffset,
                layout.AttachedSize, cancellationToken);

            // Enumerate both physical tables before asking FDI to expand even the manifest.
            var ux = BurnFixtureCabinet.Open(snapshot, layout.Stub, layout.UxSize, ux: true,
                MaxUxExpanded, MaxUxExpanded, cancellationToken, authoredUx: authored);
            var attached = BurnFixtureCabinet.Open(snapshot, layout.AttachedOffset, layout.AttachedSize, ux: false,
                MaxAttachedExpanded, MaxAttachedMember, cancellationToken);
            int manifestLength = ux.SizeOf("0");
            if (manifestLength > MaxManifest)
            {
                throw Invalid("The Burn manifest exceeds the fixture bound.");
            }

            byte[] manifest = ux.Extract("0", cancellationToken);
            var (inventory, service, agent) = Select(manifest, layout, ux, attached, attachedHash, requireRoles,
                authored, expectedVersion, cancellationToken, managed);
            byte[] resource = inventory is null ? [] : attached.Extract(inventory.Member, cancellationToken);
            if (inventory is not null && !CryptographicOperations.FixedTimeEquals(SHA512.HashData(resource), inventory.Hash))
            {
                throw Invalid("The inventory does not match its Burn payload digest.");
            }

            RoleMember? serviceMember = service is null ? null : ReadRole(attached, service, cancellationToken);
            RoleMember? agentMember = agent is null ? null : ReadRole(attached, agent, cancellationToken);

            if (snapshot.Length != length || ReadLayout(snapshot, length, cancellationToken) != layout
                || !CryptographicOperations.FixedTimeEquals(uxHash,
                    HashAt(snapshot, layout.Stub, layout.UxSize, cancellationToken))
                || !CryptographicOperations.FixedTimeEquals(attachedHash,
                    HashAt(snapshot, layout.AttachedOffset, layout.AttachedSize, cancellationToken))
                || snapshot.Length != length)
            {
                throw Invalid("The Burn snapshot changed during extraction.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return (new(WindowsNativeRoleInventory.ResourcePath, Setup, Attached, [.. resource]),
                serviceMember, agentMember);
        }
        catch (Exception exception) when (exception is EndOfStreamException or BadImageFormatException
            or XmlException or FormatException or AsnContentException)
        {
            throw new InvalidDataException("The Burn fixture metadata is malformed or incomplete.", exception);
        }
    }

    private static Layout ReadLayout(Stream snapshot, long length, CancellationToken cancellationToken)
    {
        Span<byte> dos = stackalloc byte[64];
        snapshot.Position = 0;
        snapshot.ReadExactly(dos);
        uint peOffset = U32(dos, 60);
        if (U16(dos, 0) != 0x5A4D || peOffset < dos.Length || peOffset > MiB)
        {
            throw Invalid("The Burn fixture PE header is unsupported.");
        }

        snapshot.Position = 0;
        using var reader = new PEReader(snapshot, PEStreamOptions.LeaveOpen);
        PEHeaders headers = reader.PEHeaders;
        if (headers.PEHeader is not { Magic: PEMagic.PE32Plus } pe
            || headers.CoffHeader.Machine != Machine.Amd64
            || (headers.CoffHeader.Characteristics & (Characteristics.ExecutableImage | Characteristics.Dll))
                != Characteristics.ExecutableImage
            || pe.CorHeaderTableDirectory is not { RelativeVirtualAddress: 0, Size: 0 }
            || pe.SizeOfHeaders < peOffset + 24 + headers.CoffHeader.SizeOfOptionalHeader
                + 40 * headers.SectionHeaders.Length
            || pe.SizeOfHeaders > MaxStub || headers.SectionHeaders.Length is < 1 or > 96)
        {
            throw Invalid("The Burn fixture requires a bounded Windows x64 native PE.");
        }

        long stubEnd = pe.SizeOfHeaders;
        SectionHeader? burn = null;
        foreach (SectionHeader section in headers.SectionHeaders.OrderBy(section => section.PointerToRawData))
        {
            if (section.Name == ".wixburn")
            {
                if (burn is not null)
                {
                    throw Invalid("The PE contains duplicate Burn sections.");
                }

                burn = section;
            }

            if (section.SizeOfRawData <= 0 || section.PointerToRawData < stubEnd)
            {
                throw Invalid("The Burn PE sections overlap or have invalid raw ranges.");
            }

            stubEnd = (long)section.PointerToRawData + section.SizeOfRawData;
        }

        // WixToolset.Core.Burn 7.0.0 BurnCommon: raw section version 2, CAB format 1.
        // The attached slot is in the raw section padding of the pinned x64 stub.
        Span<byte> metadata = stackalloc byte[56];
        if (stubEnd > MaxStub || stubEnd > length
            || burn is not { } sectionHeader || sectionHeader.SizeOfRawData < metadata.Length)
        {
            throw Invalid("The Burn section is missing or outside the fixture bounds.");
        }

        snapshot.Position = sectionHeader.PointerToRawData;
        snapshot.ReadExactly(metadata);
        if (U32(metadata, 0) != 0x00F14300 || U32(metadata, 4) != 2 || U32(metadata, 24) != stubEnd
            || U32(metadata, 40) != 1 || U32(metadata, 44) != 2)
        {
            throw Invalid("The Burn fixture requires one UX and one attached cabinet.");
        }

        uint ux = U32(metadata, 48);
        uint attached = U32(metadata, 52);
        if (ux is < 36 or > MaxUxCabinet || attached is < 36 or > MaxAttachedCabinet)
        {
            throw Invalid("The Burn cabinet sizes are outside the fixture bounds.");
        }

        long uxEnd = stubEnd + ux;
        uint engineChecksum = U32(metadata, 28);
        uint engineOffset = U32(metadata, 32);
        uint engineSize = U32(metadata, 36);
        DirectoryEntry outer = pe.CertificateTableDirectory;
        var bundleId = new Guid(metadata.Slice(8, 16));
        if (engineChecksum == 0 && engineOffset == 0 && engineSize == 0
            && outer is { RelativeVirtualAddress: 0, Size: 0 })
        {
            if (uxEnd + attached != length)
            {
                throw Invalid("The Burn cabinet ranges do not exactly cover the fixture overlay.");
            }

            return new((int)stubEnd, (int)ux, (int)uxEnd, (int)attached, bundleId, null);
        }

        // WiX reattach saves the detached engine's checksum and security directory
        // in .wixburn. SignTool then places the outer security directory at EOF.
        // Despite the PE API's property name, its security directory uses a file offset.
        long attachedOffset = AlignCertificate(uxEnd) + engineSize;
        long attachedEnd = attachedOffset + attached;
        if (headers.CoffHeader.SizeOfOptionalHeader != 240 || pe.NumberOfRvaAndSizes != 16
            || engineChecksum == 0 || pe.CheckSum == 0
            || engineSize is <= CertificateHeaderSize or > MaxCertificateTable
            || outer.Size is <= CertificateHeaderSize or > MaxCertificateTable
            || engineOffset != AlignCertificate(uxEnd)
            || outer.RelativeVirtualAddress != AlignCertificate(attachedEnd)
            || (long)outer.RelativeVirtualAddress + outer.Size != length)
        {
            throw Invalid("The signed Burn certificate ranges do not exactly cover the fixture overlay.");
        }

        string engineHash = ReadCertificateTable(snapshot, uxEnd, (int)engineOffset, (int)engineSize, cancellationToken);
        string outerHash = ReadCertificateTable(snapshot, attachedEnd, outer.RelativeVirtualAddress, outer.Size,
            cancellationToken);
        return new((int)stubEnd, (int)ux, (int)attachedOffset, (int)attached, bundleId,
            new(engineChecksum, pe.CheckSum, engineHash, outerHash));
    }

    private static string ReadCertificateTable(Stream snapshot, long cabinetEnd, int offset, int size,
        CancellationToken cancellationToken)
    {
        if (size % CertificateAlignment != 0)
        {
            throw Invalid("The Burn certificate table size is not aligned.");
        }

        byte[] bytes = ReadAt(snapshot, (int)cabinetEnd, size + (offset - (int)cabinetEnd), cancellationToken);
        int padding = offset - (int)cabinetEnd;
        ReadOnlySpan<byte> record = bytes.AsSpan(padding);
        if (bytes.AsSpan(0, padding).ContainsAnyExcept((byte)0)
            || U32(record, 0) != size || U16(record, 4) != 0x0200 || U16(record, 6) != 0x0002)
        {
            throw Invalid("The Burn certificate table requires zero alignment padding and one WIN_CERTIFICATE 2.0 PKCS#7 record.");
        }

        // Frame the SignedData envelope so surplus bytes cannot masquerade as
        // alignment padding. The CMS contents remain opaque to this layout reader.
        ReadOnlySpan<byte> body = record[CertificateHeaderSize..];
        AsnDecoder.ReadSequence(body, AsnEncodingRules.DER, out int contentOffset, out int contentLength,
            out int consumed);
        ReadOnlySpan<byte> content = body.Slice(contentOffset, contentLength);
        string oid = AsnDecoder.ReadObjectIdentifier(content, AsnEncodingRules.DER, out int oidLength);
        content = content[oidLength..];
        AsnDecoder.ReadSequence(content, AsnEncodingRules.DER, out int dataOffset, out int dataLength,
            out int wrapperLength, new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        AsnDecoder.ReadSequence(content.Slice(dataOffset, dataLength), AsnEncodingRules.DER,
            out _, out int signedDataLength, out int sequenceLength);
        if (oid != "1.2.840.113549.1.7.2" || wrapperLength != content.Length
            || sequenceLength != dataLength || signedDataLength == 0
            || AlignCertificate(CertificateHeaderSize + consumed) != size
            || body[consumed..].ContainsAnyExcept((byte)0))
        {
            throw Invalid("The Burn WIN_CERTIFICATE has an unsupported SignedData envelope or padding.");
        }

        return Convert.ToHexString(SHA256.HashData(record));
    }

    private static long AlignCertificate(long offset) =>
        (offset + CertificateAlignment - 1) & -CertificateAlignment;

    private static (Payload? Inventory, Payload? Service, Payload? SessionAgent) Select(
        byte[] manifest, Layout layout, BurnFixtureCabinet ux, BurnFixtureCabinet attached,
        byte[] attachedHash, bool requireRoles, bool authored, string? expectedVersion,
        CancellationToken cancellationToken, List<ManagedDeclaration>? managed = null)
    {
        using var stream = new MemoryStream(manifest, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxManifest,
        });
        XElement? root = XDocument.Load(reader).Root;
        if (root?.Name != Burn + "BurnManifest" || Attr(root, "EngineVersion") != "7.0.0.0"
            || Attr(root, "ProtocolVersion") != "1" || Attr(root, "Win64") != "yes")
        {
            throw Invalid("The Burn manifest engine or protocol is unsupported.");
        }

        XElement uxElement = Only(root, "UX");
        XElement chain = Only(root, "Chain");
        string[] packageIds = authored ? [VcRuntime, DotNetRuntime, Setup] : [Setup];
        XElement[] packages = chain.Elements().Take(packageIds.Length + 1).ToArray();
        XElement container = Only(root, "Container");
        XElement registration = Only(root, "Registration");
        if (packages.Length != packageIds.Length
            || !packages.Select(element => element.Name == Burn + "ExePackage" ? Attr(element, "Id") : "")
                .SequenceEqual(packageIds)
            || Attr(container, "Id") != Attached || Attr(container, "AttachedIndex") != "1"
            || Attr(container, "Attached") != "yes" || Attr(container, "Primary") != "yes"
            || !Guid.TryParseExact(Attr(registration, "Code"), "B", out Guid registrationId)
            || layout.BundleId == Guid.Empty || layout.BundleId != registrationId
            || ParseSize(container) != layout.AttachedSize)
        {
            throw Invalid("The Burn manifest has an unsupported package or container layout.");
        }

        if ((expectedVersion is not null && Attr(registration, "Version") != expectedVersion)
            || (authored && (Attr(registration, "BundleId") != OpenBladeReleaseManifestCodec.BundleId
                || Attr(registration, "ProviderKey") != OpenBladeReleaseManifestCodec.ProviderKey
                || Attr(registration, "PrimaryUpgradeCode") != $"{{{OpenBladeReleaseManifestCodec.UpgradeCode}}}"
                || Attr(registration, "Scope") != "perMachine")))
        {
            throw Invalid("The Burn registration does not match the expected bundle identity and version.");
        }

        if (!CryptographicOperations.FixedTimeEquals(attachedHash, Digest(container)))
        {
            throw Invalid("The attached cabinet does not match its Burn container digest.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uxMembers = new HashSet<string>(StringComparer.Ordinal) { "0" };
        var attachedMembers = new HashSet<string>(StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var declarations = new HashSet<string>(StringComparer.Ordinal);
        var managedOwners = new List<string>();
        Payload? inventory = null;
        Payload? service = null;
        Payload? agent = null;
        string servicePath = RolePath(WindowsNativeRole.Service, authored);
        string agentPath = RolePath(WindowsNativeRole.SessionAgent, authored);
        foreach (XElement element in root.Descendants())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (element.Name.LocalName)
            {
                case "UX" when element != uxElement:
                case "Chain" when element != chain:
                case "Container" when element != container:
                case "Registration" when element != registration:
                case "ExePackage" when !packages.Contains(element):
                    throw Invalid("The Burn manifest contains an extra layout declaration.");
                case "PayloadRef":
                    if (element.Name != Burn + "PayloadRef" || !packages.Contains(element.Parent)
                        || !owners.TryAdd(Attr(element, "Id"), Attr(element.Parent!, "Id")))
                    {
                        throw Invalid("The Burn manifest contains an ambiguous payload reference.");
                    }

                    break;
                case "Payload":
                    if (element.Name != Burn + "Payload" || (element.Parent != root && element.Parent != uxElement))
                    {
                        throw Invalid("A Burn payload declaration is outside its supported location.");
                    }

                    string id = Attr(element, "Id");
                    string path = Attr(element, "FilePath");
                    string member = Attr(element, "SourcePath");
                    if (!IsCanonicalId(id) || !IsCanonicalPath(path) || !ids.Add(id) || !paths.Add(path))
                    {
                        throw Invalid("The Burn manifest contains an invalid or aliased payload.");
                    }

                    string leaf = path[(path.LastIndexOf('\\') + 1)..];
                    bool namesInventory = leaf.Equals(WindowsNativeRoleInventory.ResourceName,
                        StringComparison.OrdinalIgnoreCase);
                    bool namesService = requireRoles && NamesRole(leaf, WindowsNativeRole.Service);
                    bool namesAgent = requireRoles && NamesRole(leaf, WindowsNativeRole.SessionAgent);
                    if (element.Parent == uxElement)
                    {
                        if (namesInventory || namesService || namesAgent || !uxMembers.Add(member) || !ux.Contains(member))
                        {
                            throw Invalid("The Burn UX payload mapping is invalid or ambiguous.");
                        }

                        break;
                    }

                    int size = ParseSize(element);
                    byte[] hash = Digest(element);
                    declarations.Add(id);
                    if (authored && id == DotNetRuntime)
                    {
                        ValidateDotNetDeclaration(element, path);
                        if (Attr(element, "Packaging") == "external")
                        {
                            // This is a manifest record only. Never pass the source to a file or URL API.
                            if (element.Attribute("Container") is not null || member != path || size > MaxExternalPrerequisite)
                            {
                                throw Invalid("The external .NET prerequisite declaration is inconsistent.");
                            }
                            break;
                        }
                    }

                    if (Attr(element, "Packaging") != "embedded" || Attr(element, "Container") != Attached
                        || !attachedMembers.Add(member) || attached.SizeOf(member) != size
                        || (authored && id != DotNetRuntime && element.Attribute("DownloadUrl") is not null))
                    {
                        throw Invalid("The Burn attached payload mapping is invalid or ambiguous.");
                    }

                    if (authored && ((id == Setup && path != "OpenBlade.Setup.exe")
                        || (id == VcRuntime && path != "VC_redist.x64.exe")))
                    {
                        throw Invalid("A Burn package primary executable is not canonical.");
                    }

                    if (namesInventory)
                    {
                        if (inventory is not null || path != WindowsNativeRoleInventory.ResourcePath
                            || size != WindowsNativeRoleInventory.ResourceSize)
                        {
                            throw Invalid("The Burn inventory declaration is not canonical and unique.");
                        }

                        inventory = new(id, member, size, hash, path);
                    }

                    if (managed is not null && (path.StartsWith("payload\\", StringComparison.Ordinal) || path == "OpenBlade.Setup.exe"))
                    {
                        string relative = path == "OpenBlade.Setup.exe" ? path : path[8..];
                        if (relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            || relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            || relative.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                            || relative.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                        {
                            managed.Add(new(relative, size, Convert.ToHexStringLower(hash)));
                            managedOwners.Add(id);
                        }
                    }

                    if (namesService)
                    {
                        service = SelectRole(service, servicePath, path, id, member, size, hash);
                    }
                    if (namesAgent)
                    {
                        agent = SelectRole(agent, agentPath, path, id, member, size, hash);
                    }

                    break;
            }
        }

        bool OwnedBy(string id, string owner) => owners.TryGetValue(id, out string? actual) && actual == owner;
        if (inventory is null && managed is null || !declarations.SetEquals(owners.Keys)
            || !OwnedBy(Setup, Setup) || inventory is not null && !OwnedBy(inventory.Id, Setup)
            || managedOwners.Any(id => !OwnedBy(id, Setup))
            || (authored && (!OwnedBy(VcRuntime, VcRuntime) || !OwnedBy(DotNetRuntime, DotNetRuntime)
                || owners.Any(pair => pair.Value != Setup && pair.Key != pair.Value)))
            || uxMembers.Count != ux.Count || attachedMembers.Count != attached.Count)
        {
            throw Invalid("The Burn payload declarations, Setup ownership and cabinet entries do not reconcile.");
        }

        if (requireRoles && (service is null || agent is null
            || !OwnedBy(service.Id, Setup) || !OwnedBy(agent.Id, Setup)
            || Attr(packages[^1].Elements(Burn + "PayloadRef").First(), "Id") != Setup))
        {
            throw Invalid("The Setup package must have its own primary executable and both native roles.");
        }

        return (inventory, service, agent);
    }

    private static void ValidateDotNetDeclaration(XElement element, string path)
    {
        const string prefix = "windowsdesktop-runtime-";
        const string suffix = "-win-x64.exe";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal)
            || path.Length <= prefix.Length + suffix.Length)
        {
            throw Invalid("The .NET prerequisite name is not canonical.");
        }

        string version = path[prefix.Length..^suffix.Length];
        if (!Version.TryParse(version, out Version? parsed) || parsed.Major != 10 || parsed.Build < 0
            || parsed.Revision >= 0 || parsed.ToString() != version
            || Attr(element, "DownloadUrl") != $"https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/{version}/{path}")
        {
            throw Invalid("The .NET prerequisite manifest origin is not canonical.");
        }
    }

    private static bool NamesRole(string leaf, WindowsNativeRole role) =>
        leaf.Equals(WindowsNativeRoleInventory.FileName(role), StringComparison.OrdinalIgnoreCase)
        || leaf.Equals(WindowsNativeRoleInventory.InstalledFileName(role), StringComparison.OrdinalIgnoreCase);

    private static string RolePath(WindowsNativeRole role, bool authored) =>
        authored ? @"payload\" + WindowsNativeRoleInventory.InstalledFileName(role) : WindowsNativeRoleInventory.PayloadPath(role);

    private static Payload SelectRole(Payload? current, string expectedPath, string path, string id,
        string member, int size, byte[] hash)
    {
        if (current is not null || path != expectedPath || id == Setup
            || size > WindowsNativeRoleInventory.MaximumRoleImageBytes)
        {
            throw Invalid("A native role declaration is not canonical and unique or exceeds its size bound.");
        }

        return new(id, member, size, hash, path);
    }

    private static RoleMember ReadRole(BurnFixtureCabinet cabinet, Payload payload, CancellationToken cancellationToken)
    {
        byte[] image = cabinet.Extract(payload.Member, cancellationToken);
        byte[] sha512 = SHA512.HashData(image);
        byte[] sha256 = SHA256.HashData(image);
        if (!CryptographicOperations.FixedTimeEquals(sha512, payload.Hash))
        {
            throw Invalid("A native role does not match its Burn payload digest.");
        }

        return new(payload.Id, payload.Path, payload.Member, payload.Length, Convert.ToHexStringLower(sha256), [.. image]);
    }

    private static bool IsCanonicalId(string value) =>
        value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.');

    private static bool IsCanonicalPath(string path) =>
        path.Length is > 0 and <= 240 && path.Split('\\').All(part =>
            part.Length > 0 && char.IsAsciiLetterOrDigit(part[0]) && part[^1] != '.'
            && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')
            && !IsDeviceName(part.Split('.')[0]));

    private static bool IsDeviceName(string stem) =>
        stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
        || (stem.Length == 4 && stem[^1] is >= '0' and <= '9'
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));

    private static XElement Only(XElement parent, string name)
    {
        var elements = parent.Elements(Burn + name).Take(2).ToArray();
        return elements.Length == 1 ? elements[0] : throw Invalid("A Burn manifest element is missing or duplicated.");
    }

    private static string Attr(XElement element, string name) => (string?)element.Attribute(name) ?? "";

    private static int ParseSize(XElement element) =>
        int.TryParse(Attr(element, "FileSize"), NumberStyles.None, CultureInfo.InvariantCulture, out int size)
        && size > 0 ? size : throw Invalid("A Burn file size is invalid.");

    private static byte[] Digest(XElement element)
    {
        string value = Attr(element, "Hash");
        return value.Length == 2 * SHA512.HashSizeInBytes
            ? Convert.FromHexString(value)
            : throw Invalid("A Burn SHA-512 digest is missing or invalid.");
    }

    private static byte[] ReadAt(Stream snapshot, int offset, int count, CancellationToken cancellationToken)
    {
        snapshot.Position = offset;
        byte[] bytes = new byte[count];
        for (int position = 0; position < count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chunk = Math.Min(64 * 1024, count - position);
            snapshot.ReadExactly(bytes.AsSpan(position, chunk));
            position += chunk;
        }

        return bytes;
    }

    private static byte[] HashAt(Stream snapshot, int offset, int count, CancellationToken cancellationToken)
    {
        snapshot.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[Math.Min(64 * 1024, count)];
        while (count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<byte> chunk = buffer.AsSpan(0, Math.Min(count, buffer.Length));
            snapshot.ReadExactly(chunk);
            hash.AppendData(chunk);
            count -= chunk.Length;
        }

        return hash.GetHashAndReset();
    }

    private static InvalidDataException Invalid(string message) => new(message);
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private sealed record Layout(int Stub, int UxSize, int AttachedOffset, int AttachedSize, Guid BundleId,
        SignedLayout? Signing);
    private sealed record SignedLayout(uint EngineChecksum, uint OuterChecksum, string EngineHash, string OuterHash);
    private sealed record Payload(string Id, string Member, int Length, byte[] Hash, string Path);

    internal sealed record RoleMember(string PayloadId, string PayloadPath, string CabinetMember, int Length, string Sha256, ImmutableArray<byte> Image);
    internal sealed record RoleExtraction(ImmutableArray<byte> Resource, RoleMember Service, RoleMember SessionAgent);
    internal sealed record ManagedDeclaration(string Path, long Length, string Sha512);
}
