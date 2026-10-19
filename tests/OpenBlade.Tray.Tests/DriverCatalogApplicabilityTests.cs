using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class DriverCatalogApplicabilityTests
{
    [Fact]
    public void EverySupportedModelHasAReviewedCatalogIdentity()
    {
        using JsonDocument ledger = Read("DriverCatalogApplicability.json");
        string[] actual = ledger.RootElement.GetProperty("profiles").EnumerateArray()
            .Select(ProfileIdentity).Order(StringComparer.Ordinal).ToArray();
        string[] expected = RazerBladeModelCatalog.All.Select(model => string.Join('|',
            model.ModelNumber, model.DisplayName, model.UsbVendorId, model.UsbProductId))
            .Order(StringComparer.Ordinal).ToArray();

        // Adding a model requires reviewing every catalog artifact, including entries
        // for which vendor evidence is still absent, then refreshing these snapshots.
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EveryCatalogEntryRecordsAnExplicitDecisionForEverySupportedModel()
    {
        using JsonDocument ledger = Read("DriverCatalogApplicability.json");
        string[] expected = RazerBladeModelCatalog.All.Select(model => model.ModelNumber)
            .Order(StringComparer.Ordinal).ToArray();
        JsonElement[] entries = ledger.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.NotEmpty(entries);
        Assert.Equal(entries.Length, entries.Select(entry => Text(entry, "path")).Distinct().Count());
        foreach (JsonElement entry in entries)
        {
            JsonElement[] reviews = entry.GetProperty("review").EnumerateArray().ToArray();
            Assert.Equal(expected, reviews.Select(review => Text(review, "modelNumber"))
                .Order(StringComparer.Ordinal).ToArray());
            foreach (JsonElement review in reviews)
            {
                Assert.Contains(Text(review, "status"), new[] { "Applicable", "NotEstablished" });
                Assert.False(string.IsNullOrWhiteSpace(Text(review, "evidence")));
                Assert.False(string.IsNullOrWhiteSpace(Text(review, "reason")));
            }
        }
    }

    [Fact]
    public void ThxApplicabilityExactlyMatchesTheReviewedCatalogDecisions()
    {
        using JsonDocument ledger = Read("DriverCatalogApplicability.json");
        using JsonDocument descriptor = Read("ThxNativeComponentCatalog.json");
        JsonElement entry = ledger.RootElement.GetProperty("entries").EnumerateArray()
            .Single(candidate => Text(candidate, "path") == "components/thx-v4-native/1.1.1.0/component.json");
        string[] expected = entry.GetProperty("review").EnumerateArray()
            .Where(review => Text(review, "status") == "Applicable")
            .Select(review => Text(review, "modelNumber")).Order(StringComparer.Ordinal).ToArray();
        JsonElement[] profiles = descriptor.RootElement.GetProperty("applicableProfiles").EnumerateArray().ToArray();
        Assert.Equal(expected, profiles.Select(profile => Text(profile, "modelNumber"))
            .Order(StringComparer.Ordinal).ToArray());
        foreach (JsonElement profile in profiles)
        {
            JsonElement reviewed = ledger.RootElement.GetProperty("profiles").EnumerateArray()
                .Single(candidate => Text(candidate, "modelNumber") == Text(profile, "modelNumber"));
            Assert.Equal(ProfileIdentity(reviewed), ProfileIdentity(profile));
            Assert.Equal(reviewed.GetProperty("vendorProductId").GetInt32(), profile.GetProperty("vendorProductId").GetInt32());
        }
    }

    [Fact]
    public void RuntimePinsMatchTheReviewedVendorComponentDescriptor()
    {
        using JsonDocument descriptor = Read("ThxNativeComponentCatalog.json");
        JsonElement root = descriptor.RootElement;
        JsonElement artifact = root.GetProperty("artifact");
        JsonElement signature = root.GetProperty("authenticode");
        Assert.Equal(ThxNativeComponentPackage.Version, Text(root.GetProperty("component"), "version"));
        Assert.Equal(ThxNativeComponentPackage.FileName, Text(artifact, "fileName"));
        Assert.Equal(ThxNativeComponentPackage.Length, artifact.GetProperty("size").GetInt64());
        Assert.Equal(ThxNativeComponentPackage.Sha256, Text(artifact, "sha256"));
        Assert.Equal(ThxNativeComponentPackage.SignerSubject, Text(signature, "signerSubject"));
        Assert.Equal(ThxNativeComponentPackage.SignerThumbprint, Text(signature, "signerThumbprint"));
        Assert.Equal(ThxNativeComponentPackage.DownloadUri.AbsoluteUri, Text(root.GetProperty("delivery"), "sourceUrl"));
    }

    private static JsonDocument Read(string name) => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;

    private static string ProfileIdentity(JsonElement profile) => string.Join('|',
        Text(profile, "modelNumber"), Text(profile, "productName"),
        Text(profile.GetProperty("usbIdentity"), "vid"), Text(profile.GetProperty("usbIdentity"), "pid"));
}
