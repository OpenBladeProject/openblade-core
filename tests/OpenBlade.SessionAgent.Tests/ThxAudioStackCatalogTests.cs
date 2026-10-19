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
            AssertStackPins(profile.Stack, catalog);
            JsonElement[] active = catalog.GetProperty("activeConfiguration").EnumerateArray().ToArray();
            Assert.Equal(profile.ActivePropertiesSha256, Text(active.Single(file => Text(file, "fileName") == $"thx_{family}_props.dat"), "sha256"));
            Assert.Equal(profile.SurroundConfigSha256, Text(active.Single(file => Text(file, "fileName") == "surround_714_config.json"), "sha256"));
        }
    }

    [Fact]
    public void CachedRznb12IsASeparateWholeStackWithConditionalEndpointAdmission()
    {
        using JsonDocument document = Read("ThxRznb12CachedStackCatalog.json");
        JsonElement catalog = document.RootElement;
        var profile = ThxAudioCompatibility.Profiles.Single(value => value.ModelNumber == "RZ09-0581");
        AssertStackPins(ThxAudioCompatibility.GetProvisioningStack(profile), catalog);
        Assert.Equal("RZ09-0581", Text(Assert.Single(catalog.GetProperty("applicableProfiles").EnumerateArray()), "modelNumber"));
        JsonElement admission = catalog.GetProperty("provisioning").GetProperty("freshInstallAdmission");
        Assert.Equal("ConditionalWholeStack", Text(admission, "mode"));
        Assert.True(admission.GetProperty("completePinnedStackRequired").GetBoolean());
        Assert.Equal(["Speakers", "Headphones"], admission.GetProperty("requiredPreMutationEndpointReads")
            .EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal("FreshInstallCandidate", Text(catalog.GetProperty("provisioning"), "status"));
        Assert.Equal(profile.ActivePropertiesSha256, Text(catalog.GetProperty("activeConfiguration")
            .EnumerateArray().Single(file => Text(file, "fileName") == "thx_rznb12_props.dat"), "sha256"));
    }

    [Theory]
    [InlineData("rznb11", "ThxRznb11StackCatalog.json")]
    [InlineData("rznb12", "ThxRznb12CachedStackCatalog.json")]
    public void FixedProvisioningArtifactsMatchTheReviewedDownloadsAndProtectedInnerRecipe(string family, string fixture)
    {
        using JsonDocument document = Read(fixture);
        JsonElement catalog = document.RootElement;
        JsonElement[] artifacts = catalog.GetProperty("artifacts").EnumerateArray().ToArray();
        var recipe = ThxAudioProvisioningPackage.ForProduct(family);
        Assert.Equal(recipe.Count, artifacts.Length);
        foreach (ThxAudioProvisioningArtifact artifact in recipe)
        {
            JsonElement entry = artifacts.Single(value => Text(value, "fileName") == artifact.FileName);
            Assert.Equal(artifact.Source.AbsoluteUri, Text(entry, "sourceUrl"));
            Assert.Equal(artifact.Length, entry.GetProperty("size").GetInt64());
            Assert.Equal(artifact.Sha256, Text(entry, "sha256"));
            Assert.Equal(ThxAudioProvisioningPackage.SignerSubject, Text(entry.GetProperty("authenticode"), "signerSubject"));
            Assert.Equal(ThxAudioProvisioningPackage.SignerThumbprint, Text(entry.GetProperty("authenticode"), "signerThumbprint"));
            if (artifact.EmbeddedInstaller is { } inner)
            {
                JsonElement embedded = entry.GetProperty("embeddedInstaller");
                Assert.Equal(inner.FileName, Text(embedded, "fileName"));
                Assert.Equal(inner.CompressedOffset, embedded.GetProperty("compressedOffset").GetInt64());
                Assert.Equal(inner.CompressedLength, embedded.GetProperty("compressedLength").GetInt32());
                Assert.Equal(inner.Length, embedded.GetProperty("size").GetInt64());
                Assert.Equal(inner.Sha256, Text(embedded, "sha256"));
                Assert.Equal(ThxAudioProvisioningPackage.SignerThumbprint,
                    Text(embedded.GetProperty("authenticode"), "signerThumbprint"));
                Assert.Equal(artifact.Arguments, embedded.GetProperty("arguments").EnumerateArray()
                    .Select(value => value.GetString()!).ToArray());
                Assert.False(embedded.GetProperty("carrierExecutionAllowed").GetBoolean());
                Assert.Equal("ProtectedComponentDirectory", Text(embedded, "extractionDestination"));
                Assert.Equal("CreateNew", Text(embedded, "outputCreation"));
            }
            else
            {
                Assert.False(entry.TryGetProperty("embeddedInstaller", out _));
            }
        }
        using JsonDocument unavailable = Read("ThxRznb12StackCatalog.json");
        Assert.Equal("BaseInstallerUnavailable", Text(unavailable.RootElement.GetProperty("provisioning"), "status"));
        Assert.DoesNotContain(unavailable.RootElement.GetProperty("artifacts").EnumerateArray(), artifact => Text(artifact, "role") == "BaseInstaller");
    }

    [Fact]
    public void LegacyRuntimePinsMatchComponentEvidenceWithoutClaimingProvisioning()
    {
        using JsonDocument document = Read("ThxLegacyStackCatalog.json");
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("modelBinding").GetProperty("installableCandidate").GetBoolean());
        Assert.Equal("Blocked", Text(root.GetProperty("modelBinding"), "status"));
        var profiles = ThxAudioCompatibility.Profiles.Where(profile => profile.Backend == ThxAudioNativeBackend.Legacy265).ToArray();
        Assert.Equal(profiles.Select(profile => profile.ModelNumber).Order(StringComparer.Ordinal),
            root.GetProperty("candidateProfiles").EnumerateArray().Select(profile => Text(profile, "modelNumber")).Order(StringComparer.Ordinal));
        foreach (var profile in profiles)
        {
            var candidate = root.GetProperty("candidateProfiles").EnumerateArray().Single(value => Text(value, "modelNumber") == profile.ModelNumber);
            Assert.Equal(profile.CodecHardwareIds.Single(), Text(candidate.GetProperty("baseAudioPackage"), "hardwareId"));
            Assert.Equal(profile.UsbProductId, Text(candidate.GetProperty("usbIdentity"), "pid"));
        }
        foreach (var package in ThxAudioCompatibility.Legacy265Stack.Packages)
        {
            var source = root.GetProperty("genericComponentPackages").EnumerateArray()
                .Single(value => Text(value, "infName").Equals(package.InfName, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(package.Files.Select(file => file.Name + "|" + file.Sha256).Order(StringComparer.OrdinalIgnoreCase),
                source.GetProperty("files").EnumerateArray().Select(file => Text(file, "name") + "|" + Text(file, "sha256")).Order(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static void AssertStackPins(ThxAudioDriverStack stack, JsonElement catalog)
    {
        Assert.Equal(stack.ServiceName, Text(catalog.GetProperty("stack"), "serviceName"));
        Assert.Equal(stack.ServiceExecutable, Text(catalog.GetProperty("stack"), "serviceExecutable"));
        Assert.Equal(stack.Packages.Select(package => package.InfName).Order(StringComparer.Ordinal),
            catalog.GetProperty("driverPackages").EnumerateArray().Select(package => Text(package, "infName")).Order(StringComparer.Ordinal));
        foreach (ThxAudioDriverPackage package in stack.Packages)
        {
            JsonElement descriptor = catalog.GetProperty("driverPackages").EnumerateArray()
                .Single(value => Text(value, "infName") == package.InfName);
            Assert.Equal(package.Files.Select(file => file.Name + "|" + file.Sha256).Order(StringComparer.Ordinal),
                descriptor.GetProperty("files").EnumerateArray().Select(file => Text(file, "name") + "|" + Text(file, "sha256")).Order(StringComparer.Ordinal));
        }
    }

    private static JsonDocument Read(string name) => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
}
