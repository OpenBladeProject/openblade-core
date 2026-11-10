using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace OpenBlade.Deployment;

// Offline WiX 7.0.0 fixture layout only. The caller owns the authenticated,
// write-excluding snapshot lease throughout this call. This adapter grants no trust.
internal static class WindowsNativeInventoryBurnExtractor
{
    private const string Setup = "OpenBladeSetup";
    private const string Attached = "WixAttachedContainer";
    private const int MiB = 1024 * 1024;
    private const int MaxStub = 8 * MiB;
    private const int MaxUxCabinet = 2 * MiB;
    private const int MaxAttachedCabinet = 16 * MiB;
    private const int MaxManifest = MiB;
    private static readonly XNamespace Burn = "http://wixtoolset.org/schemas/v4/2008/Burn";

    internal static ImmutableArray<WindowsNativeInventoryEntry> Extract(
        Stream snapshot, CancellationToken cancellationToken)
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
            long length = snapshot.Length;
            if (length < 64 || length > MaxStub + MaxUxCabinet + MaxAttachedCabinet)
            {
                throw Invalid("The Burn snapshot length is outside the fixture bounds.");
            }

            Layout layout = ReadLayout(snapshot, length);
            byte[] uxBytes = ReadAt(snapshot, layout.Stub, layout.UxSize, cancellationToken);
            byte[] attachedBytes = ReadAt(snapshot, layout.Stub + layout.UxSize,
                layout.AttachedSize, cancellationToken);

            // Enumerate both physical tables before asking FDI to expand even the manifest.
            var ux = BurnFixtureCabinet.Open(uxBytes, ux: true, 4 * MiB);
            var attached = BurnFixtureCabinet.Open(attachedBytes, ux: false, 64 * MiB);
            int manifestLength = ux.SizeOf("0");
            if (manifestLength > MaxManifest)
            {
                throw Invalid("The Burn manifest exceeds the fixture bound.");
            }

            byte[] manifest = ux.Extract("0", cancellationToken);
            Payload inventory = Select(manifest, layout.BundleId, ux, attached, attachedBytes);
            byte[] resource = attached.Extract(inventory.Member, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(SHA512.HashData(resource), inventory.Hash))
            {
                throw Invalid("The inventory does not match its Burn payload digest.");
            }

            if (snapshot.Length != length || ReadLayout(snapshot, length) != layout
                || !MatchesAt(snapshot, layout.Stub, uxBytes, cancellationToken)
                || !MatchesAt(snapshot, layout.Stub + layout.UxSize, attachedBytes, cancellationToken)
                || snapshot.Length != length)
            {
                throw Invalid("The Burn snapshot changed during extraction.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return [new(WindowsNativeRoleInventory.ResourcePath, Setup, Attached, [.. resource])];
        }
        catch (Exception exception) when (exception is EndOfStreamException or BadImageFormatException
            or XmlException or FormatException)
        {
            throw new InvalidDataException("The Burn fixture metadata is malformed or incomplete.", exception);
        }
    }

    private static Layout ReadLayout(Stream snapshot, long length)
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

        if (pe.CertificateTableDirectory is not { RelativeVirtualAddress: 0, Size: 0 })
        {
            throw Invalid("Signed Burn layouts are outside the verified fixture scope.");
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

        if (U32(metadata, 28) != 0 || U32(metadata, 32) != 0 || U32(metadata, 36) != 0)
        {
            throw Invalid("Signed Burn layouts are outside the verified fixture scope.");
        }

        uint ux = U32(metadata, 48);
        uint attached = U32(metadata, 52);
        if (ux is < 36 or > MaxUxCabinet || attached is < 36 or > MaxAttachedCabinet
            || stubEnd + ux + attached != length)
        {
            throw Invalid("The Burn cabinet ranges do not exactly cover the fixture overlay.");
        }

        return new((int)stubEnd, (int)ux, (int)attached, new Guid(metadata.Slice(8, 16)));
    }

    private static Payload Select(byte[] manifest, Guid bundleId, BurnFixtureCabinet ux,
        BurnFixtureCabinet attached, byte[] attachedBytes)
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
        XElement package = Only(chain, "ExePackage");
        XElement container = Only(root, "Container");
        XElement registration = Only(root, "Registration");
        if (chain.Elements().Count() != 1 || Attr(package, "Id") != Setup
            || Attr(container, "Id") != Attached || Attr(container, "AttachedIndex") != "1"
            || Attr(container, "Attached") != "yes" || Attr(container, "Primary") != "yes"
            || !Guid.TryParseExact(Attr(registration, "Code"), "B", out Guid registrationId)
            || bundleId == Guid.Empty || bundleId != registrationId
            || ParseSize(container) != attachedBytes.Length)
        {
            throw Invalid("The Burn manifest has an unsupported package or container layout.");
        }

        if (!CryptographicOperations.FixedTimeEquals(SHA512.HashData(attachedBytes), Digest(container)))
        {
            throw Invalid("The attached cabinet does not match its Burn container digest.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uxMembers = new HashSet<string>(StringComparer.Ordinal) { "0" };
        var attachedMembers = new HashSet<string>(StringComparer.Ordinal);
        var owned = new HashSet<string>(StringComparer.Ordinal);
        Payload? inventory = null;
        foreach (XElement element in root.Descendants())
        {
            switch (element.Name.LocalName)
            {
                case "UX" when element != uxElement:
                case "Chain" when element != chain:
                case "Container" when element != container:
                case "ExePackage" when element != package:
                    throw Invalid("The Burn manifest contains an extra layout declaration.");
                case "PayloadRef":
                    if (element.Name != Burn + "PayloadRef" || element.Parent != package
                        || !owned.Add(Attr(element, "Id")))
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
                    if (element.Parent == uxElement)
                    {
                        if (namesInventory || !uxMembers.Add(member) || !ux.Contains(member))
                        {
                            throw Invalid("The Burn UX payload mapping is invalid or ambiguous.");
                        }

                        break;
                    }

                    int size = ParseSize(element);
                    if (Attr(element, "Packaging") != "embedded" || Attr(element, "Container") != Attached
                        || !attachedMembers.Add(member) || attached.SizeOf(member) != size)
                    {
                        throw Invalid("The Burn attached payload mapping is invalid or ambiguous.");
                    }

                    byte[] hash = Digest(element);
                    if (namesInventory)
                    {
                        if (inventory is not null || path != WindowsNativeRoleInventory.ResourcePath
                            || size != WindowsNativeRoleInventory.ResourceSize)
                        {
                            throw Invalid("The Burn inventory declaration is not canonical and unique.");
                        }

                        inventory = new(member, hash);
                    }

                    break;
            }
        }

        var attachedIds = root.Elements(Burn + "Payload").Select(payload => Attr(payload, "Id"));
        if (inventory is null || !owned.SetEquals(attachedIds) || !owned.Contains(Setup)
            || uxMembers.Count != ux.Count || attachedMembers.Count != attached.Count)
        {
            throw Invalid("The Burn payload declarations, Setup ownership and cabinet entries do not reconcile.");
        }

        return inventory;
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

    private static bool MatchesAt(Stream snapshot, int offset, ReadOnlySpan<byte> expected,
        CancellationToken cancellationToken)
    {
        snapshot.Position = offset;
        Span<byte> buffer = stackalloc byte[8192];
        while (!expected.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<byte> chunk = buffer[..Math.Min(expected.Length, buffer.Length)];
            snapshot.ReadExactly(chunk);
            if (!chunk.SequenceEqual(expected[..chunk.Length]))
            {
                return false;
            }

            expected = expected[chunk.Length..];
        }

        return true;
    }

    private static InvalidDataException Invalid(string message) => new(message);
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private sealed record Layout(int Stub, int UxSize, int AttachedSize, Guid BundleId);
    private sealed record Payload(string Member, byte[] Hash);
}
