using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class CpuVoltageDeploymentTests
{
    [Fact]
    public void CpuManifestBindsFullSourceProfileAndClosureWithoutChangingOrdinaryBytes()
    {
        var ordinary = DeploymentPolicyTests.CreateManifest("65535.1.2.3");
        byte[] original = OpenBladeReleaseManifestCodec.SerializeCanonical(ordinary);
        Assert.DoesNotContain("validationProfile", Encoding.UTF8.GetString(original));
        var cpu = ordinary with
        {
            SchemaVersion = 2,
            ValidationProfile = "CpuVoltageValidation",
            CpuVoltageManifestSha256 = new string('a', 64),
            CpuVoltageServiceVersion = "65535.1.2.3+voltage-validation.bbbbbbbb"
        };
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] publicKey = key.ExportSubjectPublicKeyInfo();
        cpu = cpu with { SigningKeyId = OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(publicKey) };
        byte[] bytes = OpenBladeReleaseManifestCodec.SerializeCanonical(cpu);
        byte[] signature = key.SignHash(OpenBladeSignedReleaseVerifier.ComputeSigningDigest(bytes), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Assert.Equal(cpu, OpenBladeSignedReleaseVerifier.Verify(bytes, signature,
            new(true, [new(cpu.SigningKeyId, Convert.ToBase64String(publicKey))])).Manifest);
        Assert.Throws<OpenBladeReleaseVerificationException>(() => OpenBladeReleaseManifestCodec.SerializeCanonical(cpu with { SourceRevision = new string('a', 40) }));
        Assert.Throws<OpenBladeReleaseVerificationException>(() => OpenBladeReleaseManifestCodec.SerializeCanonical(cpu with { SchemaVersion = 1 }));
        Assert.Equal(original, OpenBladeReleaseManifestCodec.SerializeCanonical(ordinary));
    }

    [Theory]
    [InlineData("a/b/c.dll", true)]
    [InlineData("a/b/c/d.dll", false)]
    [InlineData("../x", false)]
    [InlineData("a//x", false)]
    [InlineData("C:/x", false)]
    [InlineData("a.dll:stream", false)]
    public void ClosureNamesMatchCaptureDepthAndTraversalBounds(string value, bool expected)
        => Assert.Equal(expected, CpuVoltageDeploymentClosure.RelativeName(value));

    [Fact]
    public void EnrollmentCapabilityIsExplicitAndOrdinaryEncodingRemainsAbsent()
    {
        var ordinary = BrokerEnrollmentTests.CreateEnrollment();
        byte[] bytes = BrokerEnrollmentCodec.SerializeCanonical(ordinary);
        Assert.DoesNotContain("cpuVoltageValidationEnabled", Encoding.UTF8.GetString(bytes));
        Assert.False(BrokerEnrollmentCodec.ParseCanonical(bytes).CpuVoltageValidationEnabled);
        var cpu = ordinary with { CpuVoltageValidationEnabled = true };
        Assert.True(BrokerEnrollmentCodec.ParseCanonical(BrokerEnrollmentCodec.SerializeCanonical(cpu)).CpuVoltageValidationEnabled);
    }

    [Fact]
    public void ClosedHostCannotMaskNewIntentOrDifferentBinding()
    {
        var host = new CpuVoltageHostEnrollment(Guid.NewGuid(), "sid", 1, 10, 20, @"C:\fixed\OpenBlade.Capture.exe", "source", "manifest", "version");
        var record = new CpuVoltageLaunchRecord(Guid.NewGuid(), "source", "manifest", "version", "sid", "Bound", 10, 20, 1, host.HostImage);
        Assert.True(WindowsCpuVoltageDeployment.MatchesClosedHost(record, host));
        Assert.False(WindowsCpuVoltageDeployment.MatchesClosedHost(record with { Phase = "Intent" }, host));
        Assert.False(WindowsCpuVoltageDeployment.MatchesClosedHost(record with { Created = 21 }, host));
        Assert.False(WindowsCpuVoltageDeployment.MatchesClosedHost(record with { Source = "new" }, host));
        Assert.False(WindowsCpuVoltageDeployment.MatchesClosedHost(record with { ProcessId = null }, host));
    }

    [Fact]
    public void FullTokenMustBelongToExactInteractiveEnrolledHuman()
    {
        var user = new WindowsCpuVoltageHostLauncher.TokenIdentity("sid", 7, 1, "S-1-16-8192", false, false, false, false, false, true);
        var full = user with { Elevated = true, Administrator = true, Integrity = "S-1-16-12288", Authentication = 8 };
        Assert.True(WindowsCpuVoltageHostLauncher.CanLaunch(user, full, "sid", 1));
        Assert.False(WindowsCpuVoltageHostLauncher.CanLaunch(user, full with { User = "S-1-5-18" }, "sid", 1));
        Assert.False(WindowsCpuVoltageHostLauncher.CanLaunch(user, full with { Interactive = false }, "sid", 1));
        Assert.False(WindowsCpuVoltageHostLauncher.CanLaunch(user, full with { Restricted = true }, "sid", 1));
        Assert.False(WindowsCpuVoltageHostLauncher.CanLaunch(user, full with { Session = 2 }, "sid", 1));
        Assert.False(WindowsCpuVoltageHostLauncher.CanLaunch(user, full with { Elevated = false }, "sid", 1));
    }

    [Fact]
    public void FixedLaunchDoesNotSelectAJobOrAcceptCallerArguments()
    {
        var manifest = DeploymentPolicyTests.CreateManifest("65535.1.2.3") with
        { CpuVoltageManifestSha256 = new string('a', 64), CpuVoltageServiceVersion = "65535.1.2.3+voltage-validation.bbbbbbbb" };
        string command = WindowsCpuVoltageHostLauncher.CommandLine(@"C:\fixed\OpenBlade.Capture.exe", @"C:\fixed", manifest);
        Assert.Contains("\"cpu-voltage-session-host\"", command);
        Assert.Contains("\"--source\" \"" + manifest.SourceRevision + "\"", command);
        Assert.DoesNotContain("ObserveOnly", command);
        Assert.DoesNotContain("VoltageCycle", command);
        Assert.Throws<InvalidDataException>(() => WindowsCpuVoltageHostLauncher.CommandLine("bad\"image", @"C:\fixed", manifest));
    }
}
