using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace OpenBlade.Deployment;

internal enum WindowsNativeRole
{
    Service,
    SessionAgent,
}

internal enum WindowsNativeInventoryAuthority
{
    ScratchFixture,
    ProductPayload,
}

internal sealed record WindowsNativeBuildIdentity(string SourceRevision, Version ProductVersion);

internal sealed record WindowsNativePayloadIdentity(string PackageSha256, WindowsNativeBuildIdentity Build);

// Only the trusted payload reader may construct this claim. It must read the fixed resource
// from the same authenticated package snapshot, after deciding its purpose independently.
internal sealed record AuthenticatedWindowsNativeInventory(
    WindowsNativeInventoryAuthority Authority,
    WindowsNativePayloadIdentity Identity,
    ImmutableArray<byte> Resource);

internal sealed class WindowsNativeRoleInventory
{
    internal const string ResourceName = "OpenBlade.WindowsNativeRoleInventory.bin";
    private const int RevisionBytes = 20;
    private const int BuildBytes = RevisionBytes + (4 * sizeof(ushort));
    private const int DigestBytes = SHA256.HashSizeInBytes;
    private static ReadOnlySpan<byte> Header => "OpenBlade.WindowsNativeRoles\0v1\0win-x64\0"u8;

    private readonly string serviceSha256;
    private readonly string sessionAgentSha256;

    private WindowsNativeRoleInventory(ReadOnlySpan<byte> service, ReadOnlySpan<byte> sessionAgent)
    {
        serviceSha256 = Convert.ToHexStringLower(service);
        sessionAgentSha256 = Convert.ToHexStringLower(sessionAgent);
    }

    internal static string FileName(WindowsNativeRole role) => role switch
    {
        WindowsNativeRole.Service => "openblade-windows-service-candidate.exe",
        WindowsNativeRole.SessionAgent => "openblade-windows-agent-candidate.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    // Build output alone is not a trust decision. Take both roles from one controlled x64 build.
    internal static ImmutableArray<byte> CreateResource(
        WindowsNativeBuildIdentity build, Func<string, ImmutableArray<byte>> readBuildOutput)
    {
        byte[] identity = EncodeBuild(build);
        byte[] service = HashNativeImage(readBuildOutput(FileName(WindowsNativeRole.Service)));
        byte[] agent = HashNativeImage(readBuildOutput(FileName(WindowsNativeRole.SessionAgent)));
        if (CryptographicOperations.FixedTimeEquals(service, agent))
        {
            throw new InvalidDataException("The native Service and Session Agent outputs must be distinct.");
        }

        return [.. Header, .. identity, .. service, .. agent];
    }

    // No raw-inventory or expected-role-hash overload. The selector alone grants no trust.
    internal static WindowsNativeRoleInventory Bind(
        WindowsNativePayloadIdentity expected, Func<string, AuthenticatedWindowsNativeInventory?> authenticate)
    {
        if (!IsLowerHex(expected.PackageSha256, 2 * DigestBytes))
        {
            throw new InvalidDataException("The expected outer package SHA-256 is invalid.");
        }

        byte[] build = EncodeBuild(expected.Build);
        AuthenticatedWindowsNativeInventory? authenticated = authenticate(ResourceName);
        if (authenticated is null
            || authenticated.Authority != WindowsNativeInventoryAuthority.ProductPayload
            || authenticated.Identity != expected)
        {
            throw new InvalidDataException("The native inventory has no trust for the selected product payload.");
        }

        ReadOnlySpan<byte> resource = authenticated.Resource.AsSpan();
        if (resource.Length != Header.Length + BuildBytes + (2 * DigestBytes)
            || !resource.StartsWith(Header)
            || !resource.Slice(Header.Length, BuildBytes).SequenceEqual(build))
        {
            throw new InvalidDataException("The authenticated native inventory does not match the selected Windows x64 build.");
        }

        ReadOnlySpan<byte> service = resource.Slice(Header.Length + BuildBytes, DigestBytes);
        ReadOnlySpan<byte> agent = resource[^DigestBytes..];
        if (CryptographicOperations.FixedTimeEquals(service, agent))
        {
            throw new InvalidDataException("The authenticated native inventory does not identify two distinct roles.");
        }

        return new(service, agent);
    }

    internal string GetExpectedSha256(WindowsNativeRole role) => role switch
    {
        WindowsNativeRole.Service => serviceSha256,
        WindowsNativeRole.SessionAgent => sessionAgentSha256,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    internal void VerifyCandidate(WindowsNativeRole role, ImmutableArray<byte> image)
    {
        byte[] expected = Convert.FromHexString(GetExpectedSha256(role));
        if (!CryptographicOperations.FixedTimeEquals(expected, HashNativeImage(image)))
        {
            throw new InvalidDataException("The native image differs from the authenticated hash for its role.");
        }
    }

    private static byte[] EncodeBuild(WindowsNativeBuildIdentity build)
    {
        Version version = build.ProductVersion;
        if (!IsLowerHex(build.SourceRevision, 2 * RevisionBytes)
            || (uint)version.Major > ushort.MaxValue || (uint)version.Minor > ushort.MaxValue
            || (uint)version.Build > ushort.MaxValue || (uint)version.Revision > ushort.MaxValue)
        {
            throw new InvalidDataException("A full Git revision and four-part product version are required.");
        }

        byte[] encoded = new byte[BuildBytes];
        Convert.FromHexString(build.SourceRevision).CopyTo(encoded, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(RevisionBytes), (ushort)version.Major);
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(RevisionBytes + 2), (ushort)version.Minor);
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(RevisionBytes + 4), (ushort)version.Build);
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(RevisionBytes + 6), (ushort)version.Revision);
        return encoded;
    }

    private static bool IsLowerHex(string text, int length) =>
        text.Length == length && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static byte[] HashNativeImage(ImmutableArray<byte> image)
    {
        if (image.IsDefaultOrEmpty)
        {
            throw new InvalidDataException("A native role image is missing.");
        }

        using PEReader pe = new(image);
        PEHeaders headers = pe.PEHeaders;
        const Characteristics executable = Characteristics.ExecutableImage | Characteristics.Dll;
        if (headers.CoffHeader.Machine != Machine.Amd64
            || (headers.CoffHeader.Characteristics & executable) != Characteristics.ExecutableImage
            || headers.PEHeader?.Magic != PEMagic.PE32Plus
            || headers.PEHeader.CorHeaderTableDirectory.Size != 0)
        {
            throw new InvalidDataException("A native role must be a Windows x64 executable image.");
        }

        return SHA256.HashData(image.AsSpan());
    }
}
