using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

public sealed class WindowsNativeRoleInventoryTests
{
    private static readonly WindowsNativeBuildIdentity Build =
        new("4e1a312c1e208f5c4f75701be0ee2eab1f7631d1", new(0, 58, 4, 0));
    private static readonly WindowsNativePayloadIdentity Payload = new(new string('a', 64), Build);
    private static readonly ImmutableArray<byte> Service = InertImage(1);
    private static readonly ImmutableArray<byte> Agent = InertImage(2);

    [Fact]
    public void SelectedAuthenticatedResourceYieldsTwoIndependentRoleHashes()
    {
        var inventory = WindowsNativeRoleInventory.Bind(Payload, name =>
        {
            Assert.Equal("OpenBlade.WindowsNativeRoleInventory.bin", name);
            return SimulateProductReader(Resource());
        });

        Assert.Equal(Sha256(Service), inventory.GetExpectedSha256(WindowsNativeRole.Service));
        Assert.Equal(Sha256(Agent), inventory.GetExpectedSha256(WindowsNativeRole.SessionAgent));
        inventory.VerifyCandidate(WindowsNativeRole.Service, Service);
        inventory.VerifyCandidate(WindowsNativeRole.SessionAgent, Agent);
        Assert.Throws<InvalidDataException>(() => inventory.VerifyCandidate(WindowsNativeRole.Service, Agent));
        Assert.Throws<InvalidDataException>(() => inventory.VerifyCandidate(WindowsNativeRole.SessionAgent, Service));
        Assert.Throws<InvalidDataException>(() =>
            inventory.VerifyCandidate(WindowsNativeRole.Service, Service.SetItem(Service.Length - 1, 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => inventory.GetExpectedSha256((WindowsNativeRole)2));
    }

    [Fact]
    public void BindingRejectsMissingTrustAndIndependentlyAuthenticatedScratchBytes()
    {
        var resource = Resource();
        Assert.Throws<InvalidDataException>(() => WindowsNativeRoleInventory.Bind(Payload, _ => null));

        // A real scratch signature even over identical resource bytes cannot supply product authority.
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(signer.ExportSubjectPublicKeyInfo(), out _);
        byte[] signature = signer.SignData(resource.AsSpan(), HashAlgorithmName.SHA256);
        AuthenticatedWindowsNativeInventory? AuthenticateScratch(ImmutableArray<byte> bytes) =>
            verifier.VerifyData(bytes.AsSpan(), signature, HashAlgorithmName.SHA256)
                ? new(WindowsNativeInventoryAuthority.ScratchFixture, Payload, bytes)
                : null;

        Assert.NotNull(AuthenticateScratch(resource));
        Assert.Throws<InvalidDataException>(() =>
            WindowsNativeRoleInventory.Bind(Payload, _ => AuthenticateScratch(resource)));
        var tampered = resource.SetItem(resource.Length - 1, (byte)(resource[^1] ^ 1));
        Assert.Null(AuthenticateScratch(tampered));
        Assert.Throws<InvalidDataException>(() =>
            WindowsNativeRoleInventory.Bind(Payload, _ => AuthenticateScratch(tampered)));
    }

    [Fact]
    public void BindingChecksOuterPackageAndEmbeddedBuildSeparately()
    {
        var resource = Resource();
        var wrongPackage = Payload with { PackageSha256 = new string('b', 64) };
        Assert.Throws<InvalidDataException>(() =>
            WindowsNativeRoleInventory.Bind(wrongPackage, _ => SimulateProductReader(resource)));

        var wrongVersion = Build with { ProductVersion = new Version(0, 58, 4, 1) };
        var wrongRevision = Build with { SourceRevision = new string('c', 40) };
        foreach (var wrongBuild in new[] { wrongVersion, wrongRevision })
        {
            var wrongPayload = Payload with { Build = wrongBuild };
            var claim = SimulateProductReader(resource) with { Identity = wrongPayload };
            Assert.Throws<InvalidDataException>(() => WindowsNativeRoleInventory.Bind(wrongPayload, _ => claim));
        }
    }

    [Fact]
    public void BindingRejectsMalformedAuthenticatedResources()
    {
        var resource = Resource();
        int x64 = resource.AsSpan().IndexOf("win-x64"u8) + "win-"u8.Length;
        var wrongArchitecture = resource.SetItem(x64, (byte)'a');
        var duplicateRole = resource.AsSpan().ToArray();
        duplicateRole.AsSpan(duplicateRole.Length - 64, 32).CopyTo(duplicateRole.AsSpan(duplicateRole.Length - 32));
        foreach (var bad in new[] { wrongArchitecture, resource.Add(0), resource.RemoveAt(0), [.. duplicateRole] })
        {
            Assert.Throws<InvalidDataException>(() =>
                WindowsNativeRoleInventory.Bind(Payload, _ => SimulateProductReader(bad)));
        }
    }

    [Theory]
    [InlineData(0x014c, 0x0002)] // I386
    [InlineData(0xaa64, 0x0002)] // ARM64
    [InlineData(0x8664, 0x2002)] // AMD64 DLL
    public void NativeRoleCannotBeBuiltOrVerifiedWithWrongImageType(ushort machine, ushort flags)
    {
        var wrongImage = InertImage(3, machine, flags);
        Assert.Throws<InvalidDataException>(() =>
            WindowsNativeRoleInventory.CreateResource(Build, _ => wrongImage));
        var inventory = WindowsNativeRoleInventory.Bind(Payload, _ => SimulateProductReader(Resource()));
        Assert.Throws<InvalidDataException>(() =>
            inventory.VerifyCandidate(WindowsNativeRole.SessionAgent, wrongImage));
    }

    [Fact]
    public void BuildRejectsMissingOrAliasedOutputs()
    {
        Assert.Throws<InvalidDataException>(() => WindowsNativeRoleInventory.CreateResource(Build, _ => []));
        Assert.Throws<InvalidDataException>(() => WindowsNativeRoleInventory.CreateResource(Build, _ => Service));
    }

    private static ImmutableArray<byte> Resource() => WindowsNativeRoleInventory.CreateResource(Build, file => file switch
    {
        "openblade-windows-service-candidate.exe" => Service,
        "openblade-windows-agent-candidate.exe" => Agent,
        _ => throw new InvalidDataException("Unexpected build output name."),
    });

    // Tests the handoff from a trusted reader; no test opens or authenticates a real Burn package.
    private static AuthenticatedWindowsNativeInventory SimulateProductReader(ImmutableArray<byte> bytes) =>
        new(WindowsNativeInventoryAuthority.ProductPayload, Payload, bytes);

    private static string Sha256(ImmutableArray<byte> image) =>
        Convert.ToHexStringLower(SHA256.HashData(image.AsSpan()));

    private static ImmutableArray<byte> InertImage(byte marker, ushort machine = 0x8664, ushort flags = 0x0002)
    {
        // Just headers and a distinguishing byte: no sections, entry point, or executable code.
        byte[] bytes = new byte[512];
        "MZ"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3c), 0x80);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x94), 240);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x98), 0x20b);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x98 + 108), 16);
        bytes[^1] = marker;
        return [.. bytes];
    }
}
