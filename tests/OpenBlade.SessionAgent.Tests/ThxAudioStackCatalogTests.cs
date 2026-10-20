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
        Assert.Equal(profile.SurroundConfigSha256, Text(catalog.GetProperty("activeConfiguration")
            .EnumerateArray().Single(file => Text(file, "fileName") == "surround_714_config.json"), "sha256"));
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
                Assert.Equal(ThxAudioProvisioningPackage.SignerSubject,
                    Text(embedded.GetProperty("authenticode"), "signerSubject"));
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

    [Theory]
    [InlineData("RZ09-0581", "intel")]
    [InlineData("RZ09-0528", "realtek")]
    [InlineData("RZ09-0530", "realtek")]
    public void BaseAudioRecipeMatchesEveryCatalogMemberAndRootMatch(string model, string family)
    {
        using JsonDocument document = Read("ThxBaseAudioPrerequisitesCatalog.json");
        JsonElement evidence = document.RootElement.GetProperty(family);
        ThxBaseAudioRecipe recipe = ThxBaseAudioPackage.ForModel(model);
        JsonElement source = evidence.GetProperty("source");
        Assert.Equal(Text(source, "downloadUrl"), recipe.Source.AbsoluteUri);
        Assert.Equal(source.GetProperty("cabSize").GetInt64(), recipe.Length);
        Assert.Equal(Text(source, "cabSha256"), recipe.Sha256);
        Assert.Equal(Text(evidence, family == "intel" ? "root" : "inf"), recipe.RootInfName);
        Assert.Equal(evidence.GetProperty("rootHardwareIds").EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.Ordinal),
            recipe.RootHardwareIds.Order(StringComparer.Ordinal));
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Pin(JsonElement file) => Text(file, "path") + "|" + file.GetProperty("size").GetInt64() + "|" + Text(file, "sha256");
        if (family == "intel")
        {
            JsonElement[] packages = evidence.GetProperty("packages").EnumerateArray().ToArray();
            Assert.Equal(packages.Length, recipe.Packages.Count);
            Assert.Equal(packages.Length, recipe.Catalogs.Count);
            foreach (JsonElement package in packages)
            {
                JsonElement catalog = package.GetProperty("catalog");
                string catalogName = Text(catalog, "file");
                JsonElement[] members = package.GetProperty("files").EnumerateArray().ToArray();
                ThxBaseAudioDriverPackage runtime = recipe.Packages.Single(value => value.InfName == Text(package, "inf"));
                Assert.Equal(catalogName, runtime.CatalogFile);
                Assert.Equal(members.Select(file => Text(file, "path")).Append(catalogName).Order(StringComparer.Ordinal),
                    runtime.Files.Select(file => file.Name).Order(StringComparer.Ordinal));
                Assert.Equal(members.Select(file => Text(file, "path")).Order(StringComparer.Ordinal),
                    recipe.Catalogs.Single(value => value.FileName == catalogName).Members.Order(StringComparer.Ordinal));
                files[catalogName] = catalogName + "|" + catalog.GetProperty("size").GetInt64() + "|" + Text(catalog, "sha256");
                foreach (JsonElement file in members)
                {
                    string path = Text(file, "path");
                    if (files.TryGetValue(path, out string? previous))
                    {
                        Assert.Equal(previous, Pin(file));
                    }
                    files[path] = Pin(file);
                }
            }
        }
        else
        {
            foreach (JsonElement file in evidence.GetProperty("files").EnumerateArray())
            {
                files.Add(Text(file, "path"), Pin(file));
            }
            ThxBaseAudioCatalog catalog = Assert.Single(recipe.Catalogs);
            Assert.Equal(Text(evidence.GetProperty("catalog"), "file"), catalog.FileName);
            Assert.Equal(files.Keys.Where(name => name != catalog.FileName).Order(StringComparer.Ordinal),
                catalog.Members.Order(StringComparer.Ordinal));
            Assert.Equal(recipe.Files, Assert.Single(recipe.Packages).Files);
        }
        Assert.Equal(files.Values.Order(StringComparer.Ordinal),
            recipe.Files.Select(file => file.Name + "|" + file.Length + "|" + file.Sha256).Order(StringComparer.Ordinal));
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
