using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class BrokerEnrollmentTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.DeploymentBroker.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProtectedStateAcls_AreAdministratorOwnedWithOnlySystemAndAdministratorControl()
    {
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);

        AssertProtected(
            ProtectedBrokerStorage.CreateProtectedDirectorySecurity(),
            administrators,
            system,
            directory: true);
        AssertProtected(
            ProtectedBrokerStorage.CreateProtectedFileSecurity(),
            administrators,
            system,
            directory: false);
        ProtectedBrokerStorage.VerifyProtectedSecurity(
            ProtectedBrokerStorage.CreateProtectedDirectorySecurity(),
            directory: true);
        ProtectedBrokerStorage.VerifyProtectedSecurity(
            ProtectedBrokerStorage.CreateProtectedFileSecurity(),
            directory: false);

        FileSecurity weak = new();
        weak.SetOwner(administrators);
        weak.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() =>
            ProtectedBrokerStorage.VerifyProtectedSecurity(weak, directory: false));
    }

    [Fact]
    public void Codec_RoundTripsCanonicalEnrollment()
    {
        BrokerEnrollment expected = CreateEnrollment();

        byte[] bytes = BrokerEnrollmentCodec.SerializeCanonical(expected);
        BrokerEnrollment actual = BrokerEnrollmentCodec.ParseCanonical(bytes);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain(" ", Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"installationId\":\"11111111-1111-1111-1111-111111111111\",\"enrolledAccountSid\":\"S-1-5-21-1-2-3-1001\",\"brokerVersion\":\"1.0.0\",\"releasePolicyId\":\"stable-v1\",\"enrolledAt\":\"2026-08-27T00:00:00.0000000Z\"}")]
    [InlineData("{\"schemaVersion\":1,\"installationId\":\"11111111-1111-1111-1111-111111111111\",\"enrolledAccountSid\":\"S-1-5-18\",\"brokerVersion\":\"1.0.0\",\"releasePolicyId\":\"stable-v1\",\"enrolledAt\":\"2026-08-27T00:00:00.0000000Z\"}")]
    public void Codec_RejectsMalformedOrNonAccountEnrollment(string json)
    {
        Assert.Throws<InvalidDataException>(() =>
            BrokerEnrollmentCodec.ParseCanonical(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task Store_IsCreateOnceAndMissingFailsClosed()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        BrokerEnrollmentStore store = new(paths);
        Directory.CreateDirectory(root);

        await Assert.ThrowsAsync<FileNotFoundException>(() => store.LoadAsync());

        BrokerEnrollment expected = CreateEnrollment();
        await store.CreateOnceAsync(expected);
        await Assert.ThrowsAsync<IOException>(() => store.CreateOnceAsync(expected));

        Assert.Equal(expected, await store.LoadAsync());
    }

    [Fact]
    public async Task Store_MissingContainingDirectoryFailsClosedAsMissingEnrollment()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(Path.Combine(root, "broker"));
        BrokerEnrollmentStore store = new(paths);

        await Assert.ThrowsAsync<FileNotFoundException>(() => store.LoadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static BrokerEnrollment CreateEnrollment()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] spki = key.ExportSubjectPublicKeyInfo();
        return new BrokerEnrollment(
            BrokerEnrollmentCodec.CurrentSchemaVersion,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "S-1-5-21-1-2-3-1001",
            "openblade-local-dev-v1",
            "1.0.0",
            OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(spki),
            Convert.ToBase64String(spki),
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));
    }

    private static void AssertProtected(
        FileSystemSecurity security,
        SecurityIdentifier administrators,
        SecurityIdentifier system,
        bool directory)
    {
        Assert.Equal(administrators, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        FileSystemAccessRule[] rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.Equal(2, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Contains(rule.IdentityReference, new[] { administrators, system });
            Assert.Equal(
                directory
                    ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                    : InheritanceFlags.None,
                rule.InheritanceFlags);
        });
    }
}
