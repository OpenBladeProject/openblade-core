using System.Text.Json;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ThxAudioStackCatalogTests
{
    [Theory]
    [InlineData("rznb11", "ThxRznb11StackCatalog.json")]
    [InlineData("rznb12", "ThxRznb12StackCatalog.json")]
    public void RuntimeStackPinsAndExactModelCodecScopeMatchCatalog(string family, string fixture)
    {
        using JsonDocument document = Read(fixture);
        JsonElement catalog = document.RootElement;
        ThxAudioCompatibilityProfile[] profiles = ThxAudioCompatibility.Profiles.Where(profile => profile.ProductId == family).ToArray();
        Assert.NotEmpty(profiles);
        Assert.Equal(profiles.Select(profile => profile.ModelNumber).Order(StringComparer.Ordinal),
            catalog.GetProperty("applicableProfiles").EnumerateArray().Select(profile => Text(profile, "modelNumber")).Order(StringComparer.Ordinal));
        foreach (ThxAudioCompatibilityProfile profile in profiles)
        {
            JsonElement identity = catalog.GetProperty("applicableProfiles").EnumerateArray()
                .Single(value => Text(value, "modelNumber") == profile.ModelNumber);
            Assert.Equal(profile.UsbProductId, Text(identity.GetProperty("usbIdentity"), "pid"));
            Assert.Equal(profile.CodecHardwareIds.Order(StringComparer.OrdinalIgnoreCase),
                identity.GetProperty("codecHardwareIds").EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.OrdinalIgnoreCase));
            Assert.Equal(profile.Stack.ServiceName, Text(catalog.GetProperty("stack"), "serviceName"));
            Assert.Equal(profile.Stack.ServiceExecutable, Text(catalog.GetProperty("stack"), "serviceExecutable"));
            Assert.Equal(profile.Stack.Packages.Select(package => package.InfName).Order(StringComparer.Ordinal),
                catalog.GetProperty("driverPackages").EnumerateArray().Select(package => Text(package, "infName")).Order(StringComparer.Ordinal));
            foreach (ThxAudioDriverPackage package in profile.Stack.Packages)
            {
                JsonElement descriptor = catalog.GetProperty("driverPackages").EnumerateArray()
                    .Single(value => Text(value, "infName") == package.InfName);
                Assert.Equal(package.Files.Select(file => file.Name + "|" + file.Sha256).Order(StringComparer.Ordinal),
                    descriptor.GetProperty("files").EnumerateArray().Select(file => Text(file, "name") + "|" + Text(file, "sha256")).Order(StringComparer.Ordinal));
            }
            JsonElement[] active = catalog.GetProperty("activeConfiguration").EnumerateArray().ToArray();
            Assert.Equal(profile.ActivePropertiesSha256, Text(active.Single(file => Text(file, "fileName") == $"thx_{family}_props.dat"), "sha256"));
            Assert.Equal(profile.SurroundConfigSha256, Text(active.Single(file => Text(file, "fileName") == "surround_714_config.json"), "sha256"));
        }
    }

    [Fact]
    public void FixedProvisioningArtifactsMatchTheReviewedVendorDownloads()
    {
        using JsonDocument document = Read("ThxRznb11StackCatalog.json");
        JsonElement catalog = document.RootElement;
        JsonElement[] artifacts = catalog.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.Equal(ThxAudioProvisioningPackage.Artifacts.Count, artifacts.Length);
        foreach (ThxAudioProvisioningArtifact artifact in ThxAudioProvisioningPackage.Artifacts)
        {
            JsonElement entry = artifacts.Single(value => Text(value, "fileName") == artifact.FileName);
            Assert.Equal(artifact.Source.AbsoluteUri, Text(entry, "sourceUrl"));
            Assert.Equal(artifact.Length, entry.GetProperty("size").GetInt64());
            Assert.Equal(artifact.Sha256, Text(entry, "sha256"));
            Assert.Equal(ThxAudioProvisioningPackage.SignerSubject, Text(entry.GetProperty("authenticode"), "signerSubject"));
            Assert.Equal(ThxAudioProvisioningPackage.SignerThumbprint, Text(entry.GetProperty("authenticode"), "signerThumbprint"));
        }
        using JsonDocument unavailable = Read("ThxRznb12StackCatalog.json");
        Assert.Equal("BaseInstallerUnavailable", Text(unavailable.RootElement.GetProperty("provisioning"), "status"));
        Assert.DoesNotContain(unavailable.RootElement.GetProperty("artifacts").EnumerateArray(), artifact => Text(artifact, "role") == "BaseInstaller");
    }

    private static JsonDocument Read(string name) => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
}
