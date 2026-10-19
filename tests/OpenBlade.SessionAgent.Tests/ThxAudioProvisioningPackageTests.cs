using System.Diagnostics;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ThxAudioProvisioningPackageTests
{
    [Fact]
    public void PackagePinsFixedOfficialArtifactsAndInvocation()
    {
        Assert.Collection(ThxAudioProvisioningPackage.Artifacts,
            artifact =>
            {
                Assert.Equal(ThxAudioProvisioningStep.InstallDriverStack, artifact.Step);
                Assert.Equal(51585680, artifact.Length);
                Assert.Equal("0154A94D60D68882E92BF03DC1265A8C82FE8432B64DD348438862194E89E32B",
                    artifact.Sha256);
                Assert.Equal(new[] { "/s", "/norestart" }, artifact.Arguments);
            },
            artifact =>
            {
                Assert.Equal(ThxAudioProvisioningStep.InstallProperties, artifact.Step);
                Assert.Equal(4397200, artifact.Length);
                Assert.Equal("8333667C11EFFC71ED09BBECB439F29C0642385F70C835287BE00F04C02BD6C8",
                    artifact.Sha256);
                Assert.Empty(artifact.Arguments);
            },
            artifact =>
            {
                Assert.Equal(ThxAudioProvisioningStep.InstallSurroundConfiguration, artifact.Step);
                Assert.Equal(590992, artifact.Length);
                Assert.Equal("04F4D4A6A3F69391347BF08512FA5DC2C918E7443B5EA33024DFE1486DEB12AE",
                    artifact.Sha256);
                Assert.Empty(artifact.Arguments);
            });
        Assert.All(ThxAudioProvisioningPackage.Artifacts, artifact =>
        {
            Assert.Equal(Uri.UriSchemeHttps, artifact.Source.Scheme);
            Assert.Equal("app-assets.razer.com", artifact.Source.Host);
        });
    }

    [Theory]
    [InlineData("http://app-assets.razer.com/file.exe")]
    [InlineData("https://example.com/file.exe")]
    [InlineData("https://user@app-assets.razer.com/file.exe")]
    [InlineData("https://app-assets.razer.com:444/file.exe")]
    public void DownloadAddressRejectsAnythingOutsideFixedOfficialOrigin(string value)
    {
        Assert.Throws<InvalidDataException>(() => ThxAudioProvisioningPackage.ValidateUri(new Uri(value)));
    }

    [Fact]
    public async Task CopyRejectsShortInputWithoutPadding()
    {
        await using var input = new MemoryStream([1, 2]);
        await using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ThxAudioProvisioningPackage.CopyPinnedLengthAsync(input, output, 3, default));
        Assert.Equal(new byte[] { 1, 2 }, output.ToArray());
    }

    [Fact]
    public async Task CopyRejectsExcessInputBeforeWritingExcessByte()
    {
        await using var input = new MemoryStream([1, 2, 3, 4]);
        await using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ThxAudioProvisioningPackage.CopyPinnedLengthAsync(input, output, 3, default));
        Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
    }

    [Fact]
    public void ElevatedModeRequiresExactInstalledSessionAgentPath()
    {
        const string programFiles = @"C:\Program Files";
        Assert.True(WindowsThxAudioProvisioningPlatform.IsInstalledExecutable(
            @"C:\Program Files\OpenBlade\OpenBlade.SessionAgent.exe", programFiles));
        Assert.False(WindowsThxAudioProvisioningPlatform.IsInstalledExecutable(
            @"C:\src\OpenBlade.SessionAgent.exe", programFiles));
        Assert.False(WindowsThxAudioProvisioningPlatform.IsInstalledExecutable(null, programFiles));
    }

    [Fact]
    public void VendorProcessUsesOnlyFixedTrustedEnvironment()
    {
        ProcessStartInfo start = WindowsThxAudioProvisioningPlatform.CreateStartInfo(
            @"C:\protected\vendor.exe", @"C:\protected", @"C:\protected\temp", ["/s"]);
        Assert.Equal(@"C:\protected\temp", start.Environment["TEMP"]);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "cmd.exe"), start.Environment["ComSpec"]);
        Assert.False(start.Environment.ContainsKey("DOTNET_STARTUP_HOOKS"));
        Assert.False(start.Environment.ContainsKey("CORECLR_ENABLE_PROFILING"));
        Assert.Equal(["/s"], start.ArgumentList);
    }

    [Fact]
    public void ElevationBridgeUsesOnlyFixedInstalledHelperArgument()
    {
        ProcessStartInfo start = ThxAudioProvisioningElevationBridge.CreateStartInfo(
            @"C:\Program Files\OpenBlade\OpenBlade.SessionAgent.exe");
        Assert.True(start.UseShellExecute);
        Assert.Equal("runas", start.Verb);
        Assert.Equal(ThxAudioProvisioningCommand.Argument, start.Arguments);
    }

    [Fact]
    public void ActiveConfigurationEvidenceRejectsOversizedFileWithoutHashingIt()
    {
        string path = Path.GetTempFileName();
        try
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                file.SetLength(4 * 1024 * 1024 + 1);
            }
            Assert.Equal("oversized", WindowsThxAudioProvisioningPlatform.GetFileEvidence(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PublicResultValuesRemainProcessExitCodeSafe()
    {
        Assert.Equal(0, (int)ThxAudioProvisioningResult.Installed);
        Assert.Equal(1, (int)ThxAudioProvisioningResult.Failed);
        Assert.Equal(2, (int)ThxAudioProvisioningResult.Unavailable);
        Assert.Equal(3, (int)ThxAudioProvisioningResult.AlreadyInstalled);
        Assert.Equal(4, (int)ThxAudioProvisioningResult.RecoveryRequired);
        Assert.Equal(5, (int)ThxAudioProvisioningResult.SourceUnavailable);
        Assert.Equal(6, (int)ThxAudioProvisioningResult.ExistingInstallation);
        Assert.Equal(10, (int)ThxAudioProvisioningResult.AcPowerRequired);
        Assert.Equal(11, (int)ThxAudioProvisioningResult.ControllerActive);
        Assert.Equal(1602, (int)ThxAudioProvisioningResult.Canceled);
        Assert.Equal(3010, (int)ThxAudioProvisioningResult.RestartRequired);
    }
}
