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
    [InlineData("upgrade-cpu-voltage", "extra")]
    [InlineData("upgrade")]
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
        Assert.DoesNotContain("cpuVoltageValidationEnabled", receipt, StringComparison.Ordinal);
    }

    [Fact]
    public void CpuUpgrade_PreservesIdentityAndOnlyUpdatesVersionAndCapability()
    {
        BrokerEnrollment original = CreateEnrollment();
        BrokerEnrollment upgraded = DevelopmentDeploymentProvisioner.CreateCpuVoltageUpgrade(
            original, new(original.DevelopmentSigningKeyId, original.DevelopmentSigningSubjectPublicKeyInfoBase64),
            original.EnrolledAccountSid, "2.3.4");
        Assert.Equal(original with { BrokerVersion = "2.3.4", CpuVoltageValidationEnabled = true }, upgraded);
        Assert.Equal(original, original with { BrokerVersion = "1.2.3", CpuVoltageValidationEnabled = false });
        string receipt = System.Text.Encoding.UTF8.GetString(DevelopmentDeploymentProvisioner.CreateReceipt(upgraded));
        Assert.EndsWith("\"cpuVoltageValidationEnabled\":true}", receipt, StringComparison.Ordinal);
        Assert.Equal(upgraded, BrokerEnrollmentCodec.ParseCanonical(BrokerEnrollmentCodec.SerializeCanonical(upgraded)));
    }

    [Theory]
    [InlineData("sid")]
    [InlineData("keyId")]
    [InlineData("publicKey")]
    public void CpuUpgrade_RejectsChangesToEnrolledUserOrKey(string mismatch)
    {
        BrokerEnrollment original = CreateEnrollment();
        Assert.Throws<InvalidDataException>(() => DevelopmentDeploymentProvisioner.CreateCpuVoltageUpgrade(
            original,
            new(mismatch == "keyId" ? new string('b', 64) : original.DevelopmentSigningKeyId,
                mismatch == "publicKey" ? "AQID" : original.DevelopmentSigningSubjectPublicKeyInfoBase64),
            mismatch == "sid" ? "S-1-5-21-1-2-3-1002" : original.EnrolledAccountSid,
            "2.3.4"));
    }

    [Fact]
    public void CpuUpgrade_ServiceAdmissionRequiresExactProtectedBinaryLocalSystemAndDemandStart()
    {
        string binary = DevelopmentDeploymentProvisioner.QuoteServiceBinaryPath(Path.Combine(
            DevelopmentDeploymentLayout.BrokerDirectory, DevelopmentDeploymentLayout.BrokerExe));
        Assert.True(CpuUpgradeService.IsExpectedConfiguration(0x10, 3, binary, "LocalSystem", false));
        Assert.True(CpuUpgradeService.IsExpectedConfiguration(0x10, 4, binary, "LocalSystem", true));
        Assert.False(CpuUpgradeService.IsExpectedConfiguration(0x10, 3, binary + " --arbitrary", "LocalSystem", false));
        Assert.False(CpuUpgradeService.IsExpectedConfiguration(0x10, 3, binary.Trim('"'), "LocalSystem", false));
        Assert.False(CpuUpgradeService.IsExpectedConfiguration(0x10, 3, binary, "LocalService", false));
        Assert.False(CpuUpgradeService.IsExpectedConfiguration(0x20, 3, binary, "LocalSystem", false));
        Assert.False(CpuUpgradeService.IsExpectedConfiguration(0x10, 2, binary, "LocalSystem", false));
        Assert.False(CpuUpgradeService.IsExpectedConfiguration(0x10, 4, binary, "LocalSystem", false));
    }

    [Theory]
    [InlineData("GW")]
    [InlineData("GA")]
    [InlineData("FW")]
    public void CpuUpgrade_FileAdmissionRejectsGenericAndSpecificUserMutation(string rights)
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        FileSecurity security = new();
        security.SetSecurityDescriptorSddlForm($"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;{rights};;;{sid})");
        Assert.Throws<UnauthorizedAccessException>(() =>
            DevelopmentDeploymentProvisioner.RequireUpgradeFileSecurity(security, sid));
    }

    [Fact]
    public void CpuUpgrade_FileAdmissionAllowsEnrolledUserReadOnly()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        FileSecurity security = new();
        security.SetSecurityDescriptorSddlForm($"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;{sid})");
        DevelopmentDeploymentProvisioner.RequireUpgradeFileSecurity(security, sid);
    }

    [Fact]
    public void CpuUpgrade_RecheckRejectsWorkAcceptedAfterInitialAdmission()
    {
        using TemporaryEnrollment file = new("enrollment");
        string directory = Path.GetDirectoryName(file.Path)!;
        DevelopmentDeploymentProvisioner.RequireNoUnresolvedDeployment(directory);
        string unresolved = Path.Combine(directory, "unresolved-deployment.json");
        File.WriteAllText(unresolved, "malformed or pending state must remain untouched");
        Assert.Throws<InvalidOperationException>(() => DevelopmentDeploymentProvisioner.RequireNoUnresolvedDeployment(directory));
        Assert.Equal("malformed or pending state must remain untouched", File.ReadAllText(unresolved));
    }

    [Fact]
    public void CpuUpgrade_MissingStateDirectoryDoesNotEstablishIdle()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() => DevelopmentDeploymentProvisioner.RequireNoUnresolvedDeployment(missing));
    }

    [Fact]
    public async Task CpuUpgrade_AtomicReplacementRetainsOtherDurableFiles()
    {
        using TemporaryEnrollment file = new("original");
        string history = file.Path + ".history";
        File.WriteAllText(history, "accepted sequence and replay history");
        using MemoryStream input = new("replacement"u8.ToArray());
        await DevelopmentDeploymentProvisioner.ReplaceUpgradeFileAsync(file.Path, input, CancellationToken.None);
        Assert.Equal("replacement", File.ReadAllText(file.Path));
        Assert.Equal("accepted sequence and replay history", File.ReadAllText(history));
        Assert.False(File.Exists(file.Path + ".cpu-upgrade"));
    }

    [Fact]
    public async Task CpuUpgrade_VerifiesInstalledLengthAndHashAgainstPinnedSource()
    {
        using TemporaryEnrollment file = new("expected");
        using MemoryStream input = new("expected"u8.ToArray());
        await DevelopmentDeploymentProvisioner.VerifyUpgradePayloadAsync(file.Path, input, CancellationToken.None);
        File.WriteAllText(file.Path, "mismatch");
        await Assert.ThrowsAsync<InvalidDataException>(() => DevelopmentDeploymentProvisioner.VerifyUpgradePayloadAsync(file.Path, input, CancellationToken.None));
        File.WriteAllText(file.Path, "short");
        await Assert.ThrowsAsync<InvalidDataException>(() => DevelopmentDeploymentProvisioner.VerifyUpgradePayloadAsync(file.Path, input, CancellationToken.None));
    }

    [Fact]
    public async Task CpuUpgrade_ExistingInterruptedReplacementFailsWithoutDeletingEvidence()
    {
        using TemporaryEnrollment file = new("original");
        File.WriteAllText(file.Path + ".cpu-upgrade", "interrupted evidence");
        using MemoryStream input = new("replacement"u8.ToArray());
        await Assert.ThrowsAsync<IOException>(() => DevelopmentDeploymentProvisioner.ReplaceUpgradeFileAsync(file.Path, input, CancellationToken.None));
        Assert.Equal("original", File.ReadAllText(file.Path));
        Assert.Equal("interrupted evidence", File.ReadAllText(file.Path + ".cpu-upgrade"));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", false)]
    [InlineData("null", false)]
    public void LocalDevSetupEnrollment_RecognizesOnlyExplicitTrueCpuCapability(string jsonValue, bool accepted)
    {
        string json = System.Text.Encoding.UTF8.GetString(BrokerEnrollmentCodec.SerializeCanonical(CreateEnrollment()));
        using TemporaryEnrollment enrollment = new(json[..^1] + ",\"cpuVoltageValidationEnabled\":" + jsonValue + "}");
        if (accepted)
        {
            Assert.Equal(CreateEnrollment().EnrolledAccountSid, LocalDevEnrollmentUser.ReadEnrolledAccountSid(enrollment.Path, verifyAcl: false));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => LocalDevEnrollmentUser.ReadEnrolledAccountSid(enrollment.Path, verifyAcl: false));
        }
    }

    private static BrokerEnrollment CreateEnrollment()
    {
        byte[] key = [1, 2, 3, 4];
        return new(1, Guid.Parse("11111111-1111-1111-1111-111111111111"), "S-1-5-21-1-2-3-1001",
            "openblade-local-dev-v1", "1.2.3", OpenBlade.Deployment.OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(key),
            Convert.ToBase64String(key), DateTimeOffset.UnixEpoch);
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
