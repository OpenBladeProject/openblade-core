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

internal sealed record WindowsNativeInventoryEntry(
    string FilePath,
    string OwnerPackageId,
    string? AttachedContainerId,
    ImmutableArray<byte> Resource);

// A snapshot binds bytes to a selected release. Product admission is a separate decision.
internal sealed record WindowsNativeInventorySnapshot(
    WindowsNativePayloadIdentity Identity,
    ImmutableArray<byte> Resource);

internal sealed class WindowsNativeRoleInventory
{
    internal const string ResourceName = "OpenBlade.WindowsNativeRoleInventory.bin";
    internal const string ResourcePath = @"payload\" + ResourceName;
    internal const int MaximumRoleImageBytes = 64 * 1024 * 1024;
    private const int RevisionBytes = 20;
    private const int BuildBytes = RevisionBytes + (4 * sizeof(ushort));
    private const int DigestBytes = SHA256.HashSizeInBytes;
    private static ReadOnlySpan<byte> Header => "OpenBlade.WindowsNativeRoles\0v1\0win-x64\0"u8;

    internal static int ResourceSize => Header.Length + BuildBytes + (2 * DigestBytes);

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

    internal static string PayloadPath(WindowsNativeRole role) => @"payload\" + FileName(role);

    internal static string InstalledFileName(WindowsNativeRole role) => role switch
    {
        WindowsNativeRole.Service => "OpenBlade.Service.exe",
        WindowsNativeRole.SessionAgent => "OpenBlade.SessionAgent.exe",
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

    // The caller supplies the selected release from existing signed-release verification.
    // The vetted adapter must reconcile declarations with physical members across containers.
    // It must account for aliases, reject ambiguous ownership or layout, and bound extraction
    // to ResourceSize. Each returned entry represents a uniquely resolved declaration and member.
    internal static Task<WindowsNativeInventorySnapshot> ReadVerifiedPackageAsync(
        string installerPath,
        OpenBladeVerifiedRelease release,
        WindowsNativePayloadIdentity selected,
        Func<Stream, CancellationToken, ImmutableArray<WindowsNativeInventoryEntry>> extract,
        CancellationToken cancellationToken = default) =>
        WithVerifiedSnapshotAsync(installerPath, release, selected, (snapshot, token) =>
        {
            var entries = extract(snapshot, token);
            token.ThrowIfCancellationRequested();
            return ReadInventory(selected, entries);
        }, cancellationToken);

    internal static async Task<T> WithVerifiedSnapshotAsync<T>(
        string installerPath, OpenBladeVerifiedRelease release, WindowsNativePayloadIdentity selected,
        Func<Stream, CancellationToken, T> read, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The installer snapshot requires Windows file sharing.");
        }

        OpenBladeReleaseManifest manifest = release.Manifest;
        var identity = new WindowsNativePayloadIdentity(
            manifest.InstallerSha256, new(manifest.SourceRevision, Version.Parse(manifest.Version)));
        if (manifest.SchemaVersion != OpenBladeReleaseManifestCodec.CurrentSchemaVersion
            || identity != selected
            || !Path.IsPathFullyQualified(installerPath)
            || !string.Equals(Path.GetFileName(installerPath), OpenBladeReleaseManifestCodec.InstallerFileName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The installer is not the selected standard LocalDev release.");
        }

        // On Windows this handle excludes writers and deletion through verification and extraction.
        // The adapter receives the same handle rewound to the beginning.
        await using FileStream snapshot = new(
            installerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        await OpenBladeSignedReleaseVerifier.VerifyInstallerAsync(snapshot, release, cancellationToken)
            .ConfigureAwait(false);
        snapshot.Position = 0;
        T result = read(snapshot, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static WindowsNativeInventorySnapshot ReadInventory(
        WindowsNativePayloadIdentity identity, ImmutableArray<WindowsNativeInventoryEntry> entries)
    {
        if (entries.IsDefaultOrEmpty || entries.Length != 1
            || !string.Equals(entries[0].FilePath, ResourcePath, StringComparison.Ordinal)
            || !string.Equals(entries[0].OwnerPackageId, "OpenBladeSetup", StringComparison.Ordinal)
            || !string.Equals(entries[0].AttachedContainerId, "WixAttachedContainer", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The installer must contain one exact attached Setup inventory.");
        }

        ReadOnlySpan<byte> resource = entries[0].Resource.AsSpan();
        if (resource.Length != ResourceSize)
        {
            throw new InvalidDataException("The attached Setup inventory has an invalid size.");
        }

        ImmutableArray<byte> bytes = [.. resource];
        _ = DecodeForBuild(identity.Build, bytes.AsSpan());
        return new(identity, bytes);
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

        return Decode(build, authenticated.Resource.AsSpan());
    }

    internal static WindowsNativeRoleInventory DecodeForBuild(WindowsNativeBuildIdentity build, ReadOnlySpan<byte> resource)
        => Decode(EncodeBuild(build), resource);

    private static WindowsNativeRoleInventory Decode(ReadOnlySpan<byte> build, ReadOnlySpan<byte> resource)
    {
        if (resource.Length != ResourceSize
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
