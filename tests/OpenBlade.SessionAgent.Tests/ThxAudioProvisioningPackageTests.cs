using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ThxAudioProvisioningPackageTests
{
    [Fact]
    public void Rznb11PackagePinsCarrierEmbeddedInstallerAndOfficialUpdates()
    {
        Assert.Collection(ThxAudioProvisioningPackage.Artifacts,
            artifact =>
            {
                Assert.Equal(ThxAudioProvisioningStep.InstallDriverStack, artifact.Step);
                Assert.Equal("THXAPO_rznb11_v4.2.29.1159-9d162a6-i1974a_MSSIGN-0ae022fb.517bae6e.exe",
                    artifact.FileName);
                Assert.Equal("https://app-assets.razer.com/files/synapse/devices/common/THX/THXAPO_rznb11_v4.2.29.1159-9d162a6-i1974a_MSSIGN-0ae022fb.517bae6e.exe",
                    artifact.Source.AbsoluteUri);
                Assert.Equal(51585680, artifact.Length);
                Assert.Equal("0154A94D60D68882E92BF03DC1265A8C82FE8432B64DD348438862194E89E32B",
                    artifact.Sha256);
                Assert.Equal(new[] { "/quiet", "/norestart" }, artifact.Arguments);
                ThxAudioEmbeddedInstaller embedded = Assert.IsType<ThxAudioEmbeddedInstaller>(
                    artifact.EmbeddedInstaller);
                Assert.Equal("thx-rznb11-base.exe", artifact.ExecutableFileName);
                Assert.Equal(51037408, artifact.ExecutableLength);
                Assert.Equal("819B1504B025105589B5899CC09BB612B7AD83F8DED8DE0D087F81D4085542ED",
                    artifact.ExecutableSha256);
                Assert.Equal(0x8B152, embedded.CompressedOffset);
                Assert.Equal(50445093, embedded.CompressedLength);
            },
            artifact =>
            {
                Assert.Equal(ThxAudioProvisioningStep.InstallProperties, artifact.Step);
                Assert.Equal(4397200, artifact.Length);
                Assert.Equal("8333667C11EFFC71ED09BBECB439F29C0642385F70C835287BE00F04C02BD6C8",
                    artifact.Sha256);
                Assert.Empty(artifact.Arguments);
                Assert.Null(artifact.EmbeddedInstaller);
                Assert.Equal(artifact.FileName, artifact.ExecutableFileName);
                Assert.Equal(artifact.Length, artifact.ExecutableLength);
                Assert.Equal(artifact.Sha256, artifact.ExecutableSha256);
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

    [Fact]
    public void Rznb12PackagePinsAuthorizedCarrierEmbeddedInstallerAndOfficialUpdates()
    {
        Assert.Collection(ThxAudioProvisioningPackage.Rznb12Artifacts,
            artifact =>
            {
                Assert.Equal(ThxAudioProvisioningStep.InstallDriverStack, artifact.Step);
                Assert.Equal("THXAPO_rznb12_v4.2.37.1621-5117e8e-i2546a_MSHLKSIGN-2255eea3.3219a439.exe",
                    artifact.FileName);
                Assert.Equal("https://github.com/OpenBladeProject/blade-driver-catalog/releases/download/thx-rznb12-4.2.37.1621/THXAPO_rznb12_v4.2.37.1621-5117e8e-i2546a_MSHLKSIGN-2255eea3.3219a439.exe",
                    artifact.Source.AbsoluteUri);
                Assert.Equal(53720208, artifact.Length);
                Assert.Equal("32392A63FC704DFC4BFBE4F33ED038773BBDE21AC88C61BAEEA013E3F98E8CF2",
                    artifact.Sha256);
                Assert.Equal(new[] { "/quiet", "/norestart" }, artifact.Arguments);
                ThxAudioEmbeddedInstaller embedded = Assert.IsType<ThxAudioEmbeddedInstaller>(
                    artifact.EmbeddedInstaller);
                Assert.Equal("thx-rznb12-base.exe", artifact.ExecutableFileName);
                Assert.Equal(0x8CE40, embedded.CompressedOffset);
                Assert.Equal(52553413, embedded.CompressedLength);
                Assert.Equal(53145160, artifact.ExecutableLength);
                Assert.Equal("DE47E110A3E528249EAF59FA7932C1A568183A38F7B52246EE395A0418B14620",
                    artifact.ExecutableSha256);
            },
            artifact =>
            {
                Assert.Equal(ThxAudioProvisioningStep.InstallProperties, artifact.Step);
                Assert.Equal("thx_rznb12_properties_updater_4.2.37.1846.exe", artifact.FileName);
                Assert.Equal("https://app-assets.razer.com/files/synapse/devices/common/THX/thx_rznb12_properties_updater_4.2.37.1846.exe",
                    artifact.Source.AbsoluteUri);
                Assert.Equal(4375696, artifact.Length);
                Assert.Equal("5BBD258C599720B8C81ABB623CA512613081CD913BB2BDA68357F8C7713F845B",
                    artifact.Sha256);
                Assert.Empty(artifact.Arguments);
            },
            artifact =>
            {
                Assert.Equal(ThxAudioProvisioningStep.InstallSurroundConfiguration, artifact.Step);
                Assert.Equal("thx_rznb12_714_patcher_config_updater_4.2.37.1846.exe",
                    artifact.FileName);
                Assert.Equal("https://app-assets.razer.com/files/synapse/devices/common/THX/thx_rznb12_714_patcher_config_updater_4.2.37.1846.exe",
                    artifact.Source.AbsoluteUri);
                Assert.Equal(590992, artifact.Length);
                Assert.Equal("1F542677BCE9CE0D3235D9A441C6FD9C8182DC364180B5E8632B3A67A591895F",
                    artifact.Sha256);
                Assert.Empty(artifact.Arguments);
            });
    }

    [Fact]
    public void ProductSelectionPreservesRznb11CompatibilityAndRejectsUnknownFamilies()
    {
        Assert.Same(ThxAudioProvisioningPackage.Artifacts,
            ThxAudioProvisioningPackage.ForProduct("rznb11"));
        Assert.Same(ThxAudioProvisioningPackage.Rznb12Artifacts,
            ThxAudioProvisioningPackage.ForProduct("rznb12"));
        IList<ThxAudioProvisioningArtifact> immutable = Assert.IsAssignableFrom<
            IList<ThxAudioProvisioningArtifact>>(ThxAudioProvisioningPackage.Rznb12Artifacts);
        Assert.True(immutable.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => { immutable[0] = immutable[0]; });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ThxAudioProvisioningPackage.ForProduct("rznb13"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ThxAudioProvisioningPackage.ForProduct("RZNB12"));
    }

    [Theory]
    [InlineData("http://app-assets.razer.com/file.exe")]
    [InlineData("https://example.com/file.exe")]
    [InlineData("https://user@app-assets.razer.com/file.exe")]
    [InlineData("https://app-assets.razer.com:444/file.exe")]
    [InlineData("https://github.com/evil/blade-driver-catalog/releases/download/thx-rznb12-4.2.37.1621/THXAPO_rznb12_v4.2.37.1621-5117e8e-i2546a_MSHLKSIGN-2255eea3.3219a439.exe")]
    [InlineData("https://github.com/OpenBladeProject/blade-driver-catalog/releases/download/evil/THXAPO_rznb12_v4.2.37.1621-5117e8e-i2546a_MSHLKSIGN-2255eea3.3219a439.exe")]
    [InlineData("http://release-assets.githubusercontent.com/asset")]
    public void DownloadAddressRejectsUnreviewedOriginsAndCatalogPaths(string value)
    {
        Assert.Throws<InvalidDataException>(() => ThxAudioProvisioningPackage.ValidateUri(new Uri(value)));
    }

    [Theory]
    [InlineData("https://app-assets.razer.com/file.exe")]
    [InlineData("https://github.com/OpenBladeProject/blade-driver-catalog/releases/download/thx-rznb12-4.2.37.1621/THXAPO_rznb12_v4.2.37.1621-5117e8e-i2546a_MSHLKSIGN-2255eea3.3219a439.exe")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/file?token=fixed")]
    public void DownloadAddressAcceptsOnlyReviewedOriginsAndExactCatalogSource(string value)
    {
        ThxAudioProvisioningPackage.ValidateUri(new Uri(value));
    }

    [Fact]
    public async Task EmbeddedInstallerCopiesOnlyPinnedDeflateRangeAndVerifiesHash()
    {
        byte[] expected = Enumerable.Range(0, 32768).Select(value => (byte)(value % 251)).ToArray();
        (MemoryStream carrier, ThxAudioEmbeddedInstaller metadata) = CreateEmbeddedCarrier(expected);
        await using (carrier)
        await using (var output = new MemoryStream())
        {
            await ThxAudioProvisioningPackage.CopyEmbeddedInstallerAsync(
                carrier, output, metadata, default);
            Assert.Equal(expected, output.ToArray());
        }
    }

    [Fact]
    public async Task EmbeddedInstallerRejectsNegativeOversizedAndOutOfRangeMetadata()
    {
        (MemoryStream carrier, ThxAudioEmbeddedInstaller metadata) = CreateEmbeddedCarrier([1, 2, 3]);
        await using (carrier)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                ThxAudioProvisioningPackage.CopyEmbeddedInstallerAsync(carrier, new MemoryStream(),
                    metadata with { CompressedOffset = -1 }, default));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                ThxAudioProvisioningPackage.CopyEmbeddedInstallerAsync(carrier, new MemoryStream(),
                    metadata with { CompressedLength = -1 }, default));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                ThxAudioProvisioningPackage.CopyEmbeddedInstallerAsync(carrier, new MemoryStream(),
                    metadata with { Length = -1 }, default));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ThxAudioProvisioningPackage.CopyEmbeddedInstallerAsync(carrier, new MemoryStream(),
                    metadata with { CompressedLength = 64 * 1024 * 1024 + 1 }, default));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ThxAudioProvisioningPackage.CopyEmbeddedInstallerAsync(carrier, new MemoryStream(),
                    metadata with { CompressedOffset = carrier.Length }, default));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ThxAudioProvisioningPackage.CopyEmbeddedInstallerAsync(carrier, new MemoryStream(),
                    metadata with { CompressedOffset = long.MaxValue }, default));
        }
    }

    [Fact]
    public async Task EmbeddedInstallerRejectsTruncatedAndExcessDecodedBytes()
    {
        byte[] expected = [1, 2, 3, 4];
        (MemoryStream carrier, ThxAudioEmbeddedInstaller metadata) = CreateEmbeddedCarrier(expected);
        await using (carrier)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ThxAudioProvisioningPackage.CopyEmbeddedInstallerAsync(carrier, new MemoryStream(),
                    metadata with { Length = expected.Length + 1 }, default));
            carrier.Position = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ThxAudioProvisioningPackage.CopyEmbeddedInstallerAsync(carrier, new MemoryStream(),
                    metadata with { Length = expected.Length - 1 }, default));
        }
    }

    [Fact]
    public async Task EmbeddedInstallerRejectsWrongInnerHash()
    {
        (MemoryStream carrier, ThxAudioEmbeddedInstaller metadata) = CreateEmbeddedCarrier([1, 2, 3]);
        await using (carrier)
        await using (var output = new MemoryStream())
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ThxAudioProvisioningPackage.CopyEmbeddedInstallerAsync(carrier, output,
                    metadata with { Sha256 = new string('0', 64) }, default));
        }
    }

    [Fact]
    public async Task EmbeddedInstallerObservesCancellation()
    {
        (MemoryStream carrier, ThxAudioEmbeddedInstaller metadata) = CreateEmbeddedCarrier([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using (carrier)
        await using (var output = new MemoryStream())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ThxAudioProvisioningPackage.CopyEmbeddedInstallerAsync(
                    carrier, output, metadata, cancellation.Token));
        }
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
        Assert.Equal(12, (int)ThxAudioProvisioningResult.BaseAudioUnverified);
        Assert.Equal(1602, (int)ThxAudioProvisioningResult.Canceled);
        Assert.Equal(3010, (int)ThxAudioProvisioningResult.RestartRequired);
    }

    private static (MemoryStream Carrier, ThxAudioEmbeddedInstaller Metadata)
        CreateEmbeddedCarrier(byte[] decoded)
    {
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(decoded);
        }
        const int offset = 17;
        byte[] carrierBytes = new byte[offset + checked((int)compressed.Length) + 11];
        compressed.ToArray().CopyTo(carrierBytes, offset);
        return (new MemoryStream(carrierBytes, writable: false),
            new ThxAudioEmbeddedInstaller("inner.exe", offset, checked((int)compressed.Length),
                decoded.Length, Convert.ToHexString(SHA256.HashData(decoded))));
    }
}
