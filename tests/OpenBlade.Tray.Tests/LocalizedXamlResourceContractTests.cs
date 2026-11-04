using System.Xml.Linq;

namespace OpenBlade.Tray.Tests;

public sealed class LocalizedXamlResourceContractTests
{
    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("TrayDashboardWindow.xaml")]
    public void LocalizedViewsUseCatalogBackedResourcesWithoutUidSubtrees(string fileName)
    {
        XDocument source = XDocument.Load(Path.Combine(CanonicalLocalizedXaml.RepositoryRoot(),
            "src", "OpenBlade.Tray", fileName));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.DoesNotContain(source.Descendants().Attributes(), attribute => attribute.Name == x + "Uid");
        XAttribute[] resources = source.Descendants().Attributes()
            .Where(attribute => attribute.Value.StartsWith("{CustomResource", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(resources);

        foreach (string culture in new[] { "en-US", "ru-RU", "zh-CN", "qps-ploc" })
        {
            IReadOnlyDictionary<string, string> catalog = CanonicalLocalizedXaml.ReadCatalog(culture);
            foreach (XAttribute resource in resources)
            {
                string key = CanonicalLocalizedXaml.Key(resource.Value);
                Assert.True(catalog.TryGetValue(key, out string? value), $"Missing {culture} resource {key}");
                Assert.False(string.IsNullOrWhiteSpace(value), $"Empty {culture} resource {key}");
            }
        }

        // English layout contracts must inspect precisely the same values referenced by source.
        XDocument resolved = CanonicalLocalizedXaml.Load(fileName);
        XElement[] sourceElements = source.Descendants().ToArray();
        XElement[] resolvedElements = resolved.Descendants().ToArray();
        Assert.Equal(sourceElements.Length, resolvedElements.Length);
        IReadOnlyDictionary<string, string> english = CanonicalLocalizedXaml.ReadCatalog();
        for (int index = 0; index < sourceElements.Length; index++)
        {
            foreach (XAttribute resource in sourceElements[index].Attributes()
                         .Where(attribute => attribute.Value.StartsWith("{CustomResource", StringComparison.Ordinal)))
            {
                Assert.Equal(english[CanonicalLocalizedXaml.Key(resource.Value)],
                    (string?)resolvedElements[index].Attribute(resource.Name));
            }
        }
    }

    [Fact]
    public void CanonicalLayoutResolutionRejectsUnknownResourcesRatherThanHidingThem()
    {
        Assert.Throws<KeyNotFoundException>(() => CanonicalLocalizedXaml.Resolve(
            "<TextBlock Text=\"{CustomResource MissingResource.Text}\" />"));
        Assert.Throws<InvalidDataException>(() => CanonicalLocalizedXaml.Key("{CustomResource MissingProperty}"));
        Assert.Throws<InvalidDataException>(() => CanonicalLocalizedXaml.Key("{CustomResource Foo.Text, Mode=OneWay}"));
    }

    [Fact]
    public void CanonicalLayoutTextEscapesXmlWithoutChangingItsMeaning()
    {
        const string key = "MainWindow_ButtonWhatsNew.Content";
        string resolved = CanonicalLocalizedXaml.Resolve($"<Button Content=\"{{CustomResource {key}}}\" />");
        Assert.Equal(CanonicalLocalizedXaml.ReadCatalog()[key],
            (string?)XDocument.Parse(resolved).Root!.Attribute("Content"));
    }

    [Fact]
    public void SharedLoaderUsesEmbeddedCanonicalFallbackBeforeAnyLocalizedViewLoads()
    {
        string tray = Path.Combine(CanonicalLocalizedXaml.RepositoryRoot(), "src", "OpenBlade.Tray");
        XDocument project = XDocument.Load(Path.Combine(tray, "OpenBlade.Tray.csproj"));
        XElement fallback = Assert.Single(project.Descendants(), element =>
            element.Name.LocalName == "EmbeddedResource"
            && (string?)element.Attribute("LogicalName") == "OpenBlade.Tray.LocalizationFallbacks.resw");
        Assert.Equal("Strings/en-US/Resources.resw", ((string)fallback.Attribute("Include")!).Replace('\\', '/'));
        Assert.Equal("false", (string?)fallback.Attribute("WithCulture"));

        string loader = File.ReadAllText(Path.Combine(tray, "LocalizedXamlResourceLoader.cs"));
        Assert.Contains("GetManifestResourceStream(\"OpenBlade.Tray.LocalizationFallbacks.resw\")", loader,
            StringComparison.Ordinal);
        Assert.Contains("LocalizedResources.Get(resourceId, EnglishFallbacks.Value[resourceId])", loader,
            StringComparison.Ordinal);
        string app = File.ReadAllText(Path.Combine(tray, "App.xaml.cs"));
        int registration = app.IndexOf("CustomXamlResourceLoader.Current = new LocalizedXamlResourceLoader()",
            StringComparison.Ordinal);
        int firstViewEntry = app.IndexOf("TryRunDeveloperHarness(commandLineArguments)", StringComparison.Ordinal);
        Assert.True(registration >= 0 && registration < firstViewEntry);
    }
}
