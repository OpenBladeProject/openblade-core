using System.Security.AccessControl;
using System.Security.Principal;
using OpenBlade.DeploymentBroker;
using OpenBlade.DevelopmentDeployment.Setup;
using OpenBlade.Setup;

namespace OpenBlade.DevelopmentDeployment.Setup.Tests;

public sealed class ProvisionerContractTests
{
    [Theory]
    [InlineData()]
    [InlineData("INSTALL")]
    [InlineData("install", "extra")]
    [InlineData("repair", "extra")]
    [InlineData("REPAIR")]
    public async Task RunAsync_RejectsAnythingExceptExactFixedVerb(params string[] args) =>
        Assert.Equal(64, await DevelopmentDeploymentProvisioner.RunAsync(args, CancellationToken.None));

    [Fact]
    public void Layout_UsesDistinctFixedLocalDevelopmentNames()
    {
        Assert.Equal("OpenBlade Local Dev Deployment Broker", DevelopmentDeploymentLayout.ServiceName);
        Assert.EndsWith(@"OpenBlade\LocalDevDeploymentInbox", DevelopmentDeploymentLayout.InboxDirectory);
        Assert.Equal("OpenBlade.LocalDevDeployment.json", DevelopmentDeploymentLayout.TrustFile);
    }

    [Fact]
    public void ClientAcl_GrantsUserReadExecuteWithoutWrite()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        DirectorySecurity security = DevelopmentDeploymentProvisioner.CreateDirectorySecurity(sid, FileSystemRights.ReadAndExecute);
        FileSystemAccessRule rule = Assert.Single(security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), r => r.IdentityReference.Value == sid);
        Assert.True(rule.FileSystemRights.HasFlag(FileSystemRights.ReadAndExecute));
        Assert.False(rule.FileSystemRights.HasFlag(FileSystemRights.Write));
        Assert.True(security.AreAccessRulesProtected);
    }

    [Fact]
    public void InboxAcl_GrantsOnlyEnrolledUserModify()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        DirectorySecurity security = DevelopmentDeploymentProvisioner.CreateDirectorySecurity(sid, FileSystemRights.Modify);
        FileSystemAccessRule rule = Assert.Single(security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), r => r.IdentityReference.Value == sid);
        Assert.True(rule.FileSystemRights.HasFlag(FileSystemRights.Modify));
    }

    [Fact]
    public void ServiceAcl_GrantsEnrolledUserOnlyQueryAndStart()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        string sddl = DevelopmentDeploymentProvisioner.CreateServiceSddl(sid);
        Assert.Contains($"(A;;CCLCRP;;;{sid})", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain($"WP;;;{sid}", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain($"DC;;;{sid}", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain($"WD;;;{sid}", sddl, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceBinaryPath_IsQuotedAndHasNoArguments()
    {
        const string path =
            @"C:\Program Files\OpenBlade Local Dev Deployment Broker\OpenBlade.DeploymentBroker.exe";
        Assert.Equal($"\"{path}\"", DevelopmentDeploymentProvisioner.QuoteServiceBinaryPath(path));
    }

    [Fact]
    public void Uninstall_RetriesBothWindowsServiceExecutableLockForms()
    {
        Assert.True(DevelopmentDeploymentProvisioner.IsRetryableDeleteFailure(
            new IOException("The executable is still mapped.")));
        Assert.True(DevelopmentDeploymentProvisioner.IsRetryableDeleteFailure(
            new UnauthorizedAccessException("Windows denied deletion while mapped.")));
        Assert.False(DevelopmentDeploymentProvisioner.IsRetryableDeleteFailure(
            new InvalidDataException("A malformed path must not be retried.")));
    }

    [Fact]
    public void Receipt_BindsInstallationUserPolicyAndDevelopmentKey()
    {
        BrokerEnrollment enrollment = new(
            1,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "S-1-5-21-1-2-3-1001",
            "openblade-local-dev-v1",
            "1.2.3",
            new string('a', 64),
            "spki",
            DateTimeOffset.UnixEpoch);
        string receipt = System.Text.Encoding.UTF8.GetString(DevelopmentDeploymentProvisioner.CreateReceipt(enrollment));
        Assert.Contains("\"releasePolicyId\":\"openblade-local-dev-v1\"", receipt, StringComparison.Ordinal);
        Assert.Contains("\"installationId\":\"11111111-1111-1111-1111-111111111111\"", receipt, StringComparison.Ordinal);
        Assert.Contains("\"enrolledAccountSid\":\"S-1-5-21-1-2-3-1001\"", receipt, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalDevSetupEnrollment_UsesOnlyCanonicalFixedPolicyAccountSid()
    {
        using TemporaryEnrollment enrollment = new(
            """
            {"schemaVersion":1,"installationId":"11111111-1111-1111-1111-111111111111","enrolledAccountSid":"S-1-5-21-1-2-3-1001","deploymentPolicyId":"openblade-local-dev-v1","brokerVersion":"1.2.3","developmentSigningKeyId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","developmentSigningSubjectPublicKeyInfoBase64":"spki","enrolledAt":"1970-01-01T00:00:00.0000000Z"}
            """);

        Assert.Equal(
            "S-1-5-21-1-2-3-1001",
            LocalDevEnrollmentUser.ReadEnrolledAccountSid(enrollment.Path, verifyAcl: false));
    }

    [Fact]
    public void LocalDevSetupEnrollment_RejectsUnknownProperties()
    {
        using TemporaryEnrollment enrollment = new(
            """
            {"schemaVersion":1,"installationId":"11111111-1111-1111-1111-111111111111","enrolledAccountSid":"S-1-5-21-1-2-3-1001","deploymentPolicyId":"openblade-local-dev-v1","brokerVersion":"1.2.3","developmentSigningKeyId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","developmentSigningSubjectPublicKeyInfoBase64":"spki","enrolledAt":"1970-01-01T00:00:00.0000000Z","sid":"S-1-5-18"}
            """);

        Assert.Throws<InvalidDataException>(
            () => LocalDevEnrollmentUser.ReadEnrolledAccountSid(
                enrollment.Path,
                verifyAcl: false));
    }

    private sealed class TemporaryEnrollment : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "OpenBlade.LocalDevEnrollment.Tests.",
            Guid.NewGuid().ToString("N"));

        public TemporaryEnrollment(string json)
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "enrollment.json");
            File.WriteAllText(Path, json);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
